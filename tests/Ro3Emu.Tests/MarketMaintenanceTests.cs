using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Periodic escrow settlement must own its durable participants, not an incidental online socket.</summary>
[Collection("Market server")]
public sealed class MarketMaintenanceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-market-maintenance-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new();
    private WebApplication _app = null!;
    private string _url = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private OwnerRegistry Owners => _app.Services.GetRequiredService<OwnerRegistry>();
    private AccountStore Accounts => _app.Services.GetRequiredService<AccountStore>();
    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();
    private MarketStore Market => _app.Services.GetRequiredService<MarketStore>();
    private StallsService Stalls => _app.Services.GetRequiredService<StallsService>();
    private TradeService Trade => _app.Services.GetRequiredService<TradeService>();
    private MailService Mail => _app.Services.GetRequiredService<MailService>();
    private World World => _app.Services.GetRequiredService<World>();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        // Exact AOI snapshots below assume market maintenance is the only producer changing world units.
        // Stop the unrelated MVP ticker before the loop starts so its delayed boss spawns cannot enter a snapshot.
        _app.Services.GetRequiredService<GameLoop>().Remove(_app.Services.GetRequiredService<MvpService>());
        await _app.StartAsync();
        _url = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        Accounts.BeforeOwnedCommitForTest = null;
        Owners.DeferredForTest = null;
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) return path.FullName;
        throw new InvalidOperationException("repository root");
    }

    private async Task<(BotClient Bot, GameSession Session)> Login(string name)
    {
        var bot = new BotClient(_url, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        // C/D 15: the login and scene load request their scene's stall maintenance; run it now, so its system stalls
        // exist before a test's AOI or ledger baseline.
        await Loop.InvokeAsync(() => Stalls.RefreshWorldMaintenanceAsync());
        var session = await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<SessionRegistry>()
            .All().Single(s => s.Role?.Id == id)));
        return (bot, session);
    }

    private async Task SeedAsync(long stallOwner, long tradeSeller)
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        await Loop.InvokeAsync(async () => Assert.Equal(0, await Market.RunMaintenanceAsync(tx =>
        {
            if (stallOwner > 0) tx.Economy.Source(new(EconomyAssetKind.Item, 1000100), 2, EconomyReason.GmGrant);
            if (tradeSeller > 0) tx.Economy.Source(new(EconomyAssetKind.Item, 1254013), 1, EconomyReason.GmGrant);
            if (stallOwner > 0)
                tx.Put("stall", "p:" + stallOwner, new StallsService.StallRow
                {
                    Key = "p:" + stallOwner, Owner = stallOwner, Scene = 10100,
                    OpenTime = now, Timeout = now + 5, MaxGrid = 1,
                    Stock = [new StallsService.StallItem
                    {
                        Id = 1, Item = new InvItem { Entry = 1000100, Stack = 2, Pack = 1 }, Price = 10, Currency = 2,
                    }],
                });
            if (tradeSeller > 0)
                tx.Put("trade_listing", "maintenance-sku", new TradeService.Listing
                {
                    Sku = "maintenance-sku", Spu = "maintenance-spu", SellerId = tradeSeller,
                    Entry = 1254013, Currency = 1, UnitPrice = 100, Quantity = 1,
                    SaleTimeMs = now * 1000, ExpiresAtMs = (now + 5) * 1000,
                    Item = new InvItem { Entry = 1254013, Stack = 1, Pack = 1 },
                });
            return 0;
        })));
    }

    private static MailService.Mailbox Mailbox(RoleStore roles, long role) =>
        JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(role)!)!;

    [ClientDataFact]
    public async Task StallsAndTradeSettleWhileUnrelatedOnlineRoleHoldsStateAndWholeRole()
    {
        var (bot, unrelated) = await Login("MaintOther"); await using var client = bot;
        long unrelatedId = unrelated.Role!.Id;
        long stallOwner = Roles.Create("stall-owner", 1, "MaintStall", 1, 1000, 1)!.Id;
        long tradeSeller = Roles.Create("trade-seller", 1, "MaintTrade", 1, 1000, 1)!.Id;
        await SeedAsync(stallOwner, tradeSeller);
        long stallUnit = 7_000_000_000_000L + stallOwner;
        // Monsters spawn and despawn on their own (wall-clock) timers; the assertion is about every other unit.
        long[] aoiBefore = await Loop.InvokeAsync(() => Task.FromResult(World.AllUnits()
            .Where(u => u is not Ro3Emu.Server.Maps.MonsterUnit).Select(u => u.Uid).Order().ToArray()));
        var held = Signal(); var releaseHold = Signal();
        var stallEntered = Signal(); var tradeEntered = Signal(); var stallCommitted = Signal();
        var releaseStall = Signal(); var releaseTrade = Signal();
        var holder = Loop.InvokeAsync(() => Owners.RunAsync("unrelated state holder", [ReservationKey.Role(unrelatedId)], async () =>
        {
            using var state = await CharacterService.Lock(unrelated);
            held.TrySetResult(); await releaseHold.Task;
        }));
        Task? settlement = null, stallSweep = null, tradeSweep = null;
        long stallWorkId = 0, tradeWorkId = 0;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "market/transaction" ||
                    Loop.CurrentWork?.Id != stallWorkId && Loop.CurrentWork?.Id != tradeWorkId)
                    return Task.CompletedTask;
                long operation = Owners.Current!.Operation;
                Assert.False(Owners.Reservations.Covers(operation, [ReservationKey.Role(unrelatedId)]));
                if (Owners.Reservations.Covers(operation, [ReservationKey.Entity("market_record:stall", "p:" + stallOwner)]))
                {
                    Assert.True(Owners.Reservations.Covers(operation, [ReservationKey.Role(stallOwner)]));
                    Assert.True(Owners.Reservations.Covers(operation, [ReservationKey.Entity("market_kind", "stall")]));
                    stallEntered.TrySetResult(); return releaseStall.Task;
                }
                if (Owners.Reservations.Covers(operation, [ReservationKey.Entity("market_record:trade_listing", "maintenance-sku")]))
                {
                    Assert.True(Owners.Reservations.Covers(operation, [ReservationKey.Role(tradeSeller)]));
                    Assert.True(Owners.Reservations.Covers(operation, [ReservationKey.Entity("market_kind", "trade_listing")]));
                    tradeEntered.TrySetResult(); return releaseTrade.Task;
                }
                return Task.CompletedTask;
            };
            Accounts.BeforeOwnedApplyForTest = label =>
            {
                if (label == "market/transaction" && Loop.CurrentWork?.Id == stallWorkId)
                    stallCommitted.TrySetResult();
                return Task.CompletedTask;
            };
            settlement = Loop.InvokeAsync(() =>
            {
                _clock.Advance(TimeSpan.FromSeconds(10));
                stallSweep = Loop.TrackAsync("test/stall maintenance", () =>
                {
                    stallWorkId = Loop.CurrentWork!.Id;
                    return Stalls.RefreshWorldMaintenanceAsync();
                });
                tradeSweep = Loop.TrackAsync("test/trade maintenance", () =>
                {
                    tradeWorkId = Loop.CurrentWork!.Id;
                    return Trade.TickAsync(unrelated);
                });
                return Task.WhenAll(stallSweep, tradeSweep);
            });
            await stallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(settlement.IsCompleted);
            Assert.Null(Roles.LoadMailbox(stallOwner));
            releaseStall.TrySetResult();
            await tradeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stallCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(holder.IsCompleted);
            Assert.Single(Mailbox(Roles, stallOwner).Letters);
            Assert.Null(Roles.LoadMailbox(tradeSeller));
            releaseTrade.TrySetResult();
            await tradeSweep!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stallSweep!.IsCompleted);
            Assert.False(holder.IsCompleted);
        }
        finally
        {
            releaseStall.TrySetResult(); releaseTrade.TrySetResult(); releaseHold.TrySetResult();
            Accounts.BeforeOwnedCommitForTest = null; Accounts.BeforeOwnedApplyForTest = null;
        }
        await holder.WaitAsync(TimeSpan.FromSeconds(10));
        await settlement!.WaitAsync(TimeSpan.FromSeconds(10));
        var stall = Market.ReadCurrent<StallsService.StallRow>("stall", "p:" + stallOwner)!;
        Assert.True(stall.Ended); Assert.Empty(stall.Stock);
        Assert.Equal(1031, Assert.Single(Mailbox(Roles, stallOwner).Letters).TemplateId);
        Assert.Equal(2, Assert.Single(Assert.Single(Mailbox(Roles, stallOwner).Letters).Items).Stack);
        Assert.Null(Market.ReadCurrent<TradeService.Listing>("trade_listing", "maintenance-sku"));
        Assert.Equal(1067, Assert.Single(Mailbox(Roles, tradeSeller).Letters).TemplateId);
        long[] aoiAfter = await Loop.InvokeAsync(() => Task.FromResult(World.AllUnits()
            .Where(u => u is not Ro3Emu.Server.Maps.MonsterUnit).Select(u => u.Uid).Order().ToArray()));
        Assert.Equal(aoiBefore.Where(id => id != stallUnit), aoiAfter);
        Assert.DoesNotContain(stallUnit, aoiAfter);
        await Loop.InvokeAsync(async () => { await Stalls.RefreshWorldMaintenanceAsync(); await Trade.TickMaintenanceAsync(); });
        Assert.Single(Mailbox(Roles, stallOwner).Letters);
        Assert.Single(Mailbox(Roles, tradeSeller).Letters);
    }

    [ClientDataFact]
    public async Task MaintenanceCompletesWhileStateLockHolderWaitsForRealmAndMailGateIsHeld()
    {
        var (bot, unrelated) = await Login("MaintRealm"); await using var client = bot;
        long tradeSeller = Roles.Create("realm-seller", 1, "RealmSeller", 1, 1000, 1)!.Id;
        await SeedAsync(0, tradeSeller);
        var commitEntered = Signal(); var releaseCommit = Signal();
        var stateHeld = Signal(); var realmDeferred = Signal(); var realmEntered = Signal(); var releaseRealm = Signal();
        Task? trade = null, holder = null;
        bool mailHeld = false;
        try
        {
            await Loop.InvokeAsync(() => Mail.EnterTransactionAsync());
            mailHeld = true;
            Accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "market/transaction" ||
                    !Owners.Reservations.Covers(Owners.Current!.Operation,
                        [ReservationKey.Entity("market_record:trade_listing", "maintenance-sku")]))
                    return Task.CompletedTask;
                commitEntered.TrySetResult(); return releaseCommit.Task;
            };
            Owners.DeferredForTest = origin => { if (origin == "state holder to realm") realmDeferred.TrySetResult(); };
            trade = Loop.InvokeAsync(async () =>
            {
                _clock.Advance(TimeSpan.FromSeconds(10));
                await Trade.TickMaintenanceAsync();
            });
            await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            holder = Loop.InvokeAsync(async () =>
            {
                await unrelated.StateLock.WaitAsync();
                try
                {
                    stateHeld.TrySetResult();
                    await Owners.RunAsync("state holder to realm", [ReservationKey.Realm], async () =>
                    {
                        realmEntered.TrySetResult(); await releaseRealm.Task;
                    });
                }
                finally { unrelated.StateLock.Release(); }
            });
            await stateHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await realmDeferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseCommit.TrySetResult();
            await trade.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(holder.IsCompleted);
            Assert.Single(Mailbox(Roles, tradeSeller).Letters);
            await realmEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseCommit.TrySetResult(); releaseRealm.TrySetResult();
            if (mailHeld) Mail.LeaveTransaction();
            Accounts.BeforeOwnedCommitForTest = null; Owners.DeferredForTest = null;
        }
        if (trade != null) await trade.WaitAsync(TimeSpan.FromSeconds(10));
        if (holder != null) await holder.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [ClientDataFact]
    public async Task TradeExpiryIsDurableWhenTheIncidentalRequesterAttachmentWasCancelled()
    {
        var (bot, incidental) = await Login("MaintStale"); await using var client = bot;
        long seller = Roles.Create("stale-seller", 1, "StaleSeller", 1, 1000, 1)!.Id;
        await SeedAsync(0, seller);
        await Loop.InvokeAsync(async () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            var lease = incidental.Attachment!;
            lease.Cancelled = true;
            try { await Trade.TickAsync(incidental); }
            finally { lease.Cancelled = false; }
        });
        Assert.Null(Market.ReadCurrent<TradeService.Listing>("trade_listing", "maintenance-sku"));
        Assert.Equal(1067, Assert.Single(Mailbox(Roles, seller).Letters).TemplateId);
        await Loop.InvokeAsync(() => Trade.TickAsync(incidental));
        Assert.Single(Mailbox(Roles, seller).Letters);
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
