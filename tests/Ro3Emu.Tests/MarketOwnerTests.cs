using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

[Collection("Market server")]
public sealed class MarketOwnerTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-market-owner-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _url = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private OwnerRegistry Owners => _app.Services.GetRequiredService<OwnerRegistry>();
    private AccountStore Accounts => _app.Services.GetRequiredService<AccountStore>();
    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();
    private MarketStore Market => _app.Services.GetRequiredService<MarketStore>();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync(); _url = $"http://127.0.0.1:{http}";
    }
    public async Task DisposeAsync()
    {
        Accounts.BeforeOwnedCommitForTest = null; Accounts.BeforeOwnedApplyForTest = null;
        Accounts.AfterWriterCommitForTest = null;
        await _app.StopAsync(); await _app.DisposeAsync();
        Directory.Delete(_directory, true);
    }
    private async Task<(BotClient Bot, GameSession Session)> Login(string name)
    {
        var bot = new BotClient(_url, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<SessionRegistry>()
            .All().Single(s => s.Role?.Id == id)));
        return (bot, session);
    }
    private int StagePurchase(MarketTransaction tx, long buyer, params long[] sellers)
    {
        tx.Economy.Source(new(EconomyAssetKind.Item, 1000000), 2, EconomyReason.GmGrant);
        if (100 - sellers.Length * 50 > 0)
            tx.Economy.Sink(new(EconomyAssetKind.Currency, 1), 100 - sellers.Length * 50, EconomyReason.MarketTax);
        tx.Role(buyer).Currency[1] -= 100;
        foreach (long seller in sellers)
        {
            var participant = tx.Role(seller);
            participant.Currency[1] = participant.Currency.GetValueOrDefault(1) + 40;
            tx.DeliverMail(seller, 1063, currency: new Dictionary<int, long> { [1] = 10 });
            tx.Put("trade_sequence", seller.ToString(), buyer);
        }
        tx.DeliverMail(buyer, 1064, [new InvItem { Entry = 1000000, Stack = 2, Pack = 1 }]);
        return 0;
    }

    [Fact]
    public async Task MultiSellerCommitAndSlowNoticeFenceOnlyParticipantsAndPublishAllTogether()
    {
        var (buyerBot, buyer) = await Login("Ownerbuyer"); await using var b = buyerBot;
        var (firstBot, first) = await Login("Ownerseller"); await using var s = firstBot;
        long second = Roles.Create("offline", 1, "OfflineSeller", 1, 1000, 1)!.Id;
        long unrelated = Roles.Create("unrelated", 1, "OtherOwner", 1, 1000, 1)!.Id;
        long buyerId = buyer.Role!.Id, firstId = first.Role!.Id;
        long buyerBefore = buyer.Progress!.Currency[1], firstBefore = first.Progress!.Currency[1];
        long secondBefore = Roles.LoadProgress(second).Currency.GetValueOrDefault(1);
        var commitEntered = Signal(); var releaseCommit = Signal(); var deferred = Signal();
        var noticeEntered = Signal(); var releaseNotice = Signal();
        Accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "market/transaction") return Task.CompletedTask;
            commitEntered.TrySetResult(); return releaseCommit.Task;
        };
        await Loop.InvokeAsync(() => { Owners.DeferredForTest = origin => { if (origin == "selected seller") deferred.TrySetResult(); }; return Task.CompletedTask; });
        var purchase = Loop.InvokeAsync(() => Market.RunAsync(buyer, tx => StagePurchase(tx, buyerId, firstId, second), async _ =>
        {
            Assert.Equal(buyerBefore - 100, buyer.Progress!.Currency[1]);
            Assert.Equal(firstBefore + 40, first.Progress!.Currency[1]);
            Assert.Equal(secondBefore + 40, Roles.LoadProgress(second).Currency[1]);
            Assert.All(new[] { buyerId, firstId, second }, id => Assert.NotNull(Roles.LoadMailbox(id)));
            noticeEntered.TrySetResult(); await releaseNotice.Task;
            throw new IOException("injected slow notice failure");
        }));
        Task? contender = null;
        try
        {
            await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            contender = Loop.InvokeAsync(() => Owners.RunAsync("selected seller",
                [ReservationKey.Role(firstId), ReservationKey.Role(unrelated)], () => Task.CompletedTask));
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long started = Stopwatch.GetTimestamp();
            await Loop.InvokeAsync(() => Owners.RunAsync("unrelated progress", [ReservationKey.Role(unrelated)], () =>
            {
                Assert.False(contender.IsCompleted);
                Assert.Equal(buyerBefore, buyer.Progress!.Currency[1]);
                Assert.Equal(firstBefore, first.Progress!.Currency[1]);
                var progress = Roles.LoadProgress(unrelated); progress.Exp = 123; Roles.SaveProgress(unrelated, progress);
                return Task.CompletedTask;
            })).WaitAsync(TimeSpan.FromSeconds(10));
            output.WriteLine("Unrelated durable progress during held market commit: {0:F3} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(123, Roles.LoadProgress(unrelated).Exp);
            Assert.Null(Roles.LoadMailbox(second));
            releaseCommit.TrySetResult();
            await noticeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await contender.WaitAsync(TimeSpan.FromSeconds(10));
            await Loop.InvokeAsync(() => Owners.RunAsync("participants during notice",
                [ReservationKey.Role(buyerId), ReservationKey.Role(firstId), ReservationKey.Role(second)], () => Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(purchase.IsCompleted);
        }
        finally { releaseCommit.TrySetResult(); releaseNotice.TrySetResult(); }
        Assert.Equal(0, await purchase.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(buyerBefore - 100, Roles.LoadProgress(buyerId).Currency[1]);
        output.WriteLine("Owner queue age maximum: {0:F3} ms; DB metrics: {1}", Owners.MaxQueueAgeMs, JsonSerializer.Serialize(Accounts.AccessMetrics.Snapshot()));
        Assert.True(Owners.MaxQueueAgeMs > 0);
    }

    private sealed class RejectMailboxPlan(bool enabled) : DbPlan
    {
        internal override int Bytes => 256;
        internal override string Label => "test/mail-conflict";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = DbSql.Command(db, tx, enabled
                ? "CREATE TRIGGER reject_owner_mail BEFORE UPDATE OF mailbox ON role BEGIN SELECT RAISE(ABORT,'injected mailbox conflict'); END"
                : "DROP TRIGGER reject_owner_mail");
            return cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task MarketDeliveryRestagesMailboxAfterWaitingForEarlierDelivery()
    {
        var (bot, session) = await Login("Restagemail"); await using var client = bot;
        long role = session.Role!.Id;
        var committing = Signal(); var release = Signal(); var deferred = Signal();
        Accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "mail/deliver") return Task.CompletedTask;
            committing.TrySetResult(); return release.Task;
        };
        Owners.DeferredForTest = origin => { if (origin == "market") deferred.TrySetResult(); };
        var raw = _app.Services.GetRequiredService<MailService>().SendRawAsync(role, "first", "body");
        Task<int>? market = null;
        int captures = 0;
        string finalMail = "";
        try
        {
            await committing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            market = Loop.InvokeAsync(() => Market.RunAsync(session, tx =>
            {
                captures++;
                tx.Economy.Source(new(EconomyAssetKind.Currency, 1), 7, EconomyReason.GmGrant);
                finalMail = tx.DeliverMail(role, 1064, currency: new Dictionary<int, long> { [1] = 7 });
                return 0;
            }));
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(market.IsCompleted);
            Assert.Null(Roles.LoadMailbox(role));
        }
        finally { release.TrySetResult(); Accounts.BeforeOwnedCommitForTest = null; Owners.DeferredForTest = null; }
        string rawMail = await raw.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await market!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(captures >= 2);
        var box = JsonSerializer.Deserialize<MailService.Mailbox>(Roles.LoadMailbox(role)!)!;
        Assert.Equal(2, box.Letters.Count);
        Assert.Single(box.Letters, letter => letter.Id == rawMail);
        Assert.Single(box.Letters, letter => letter.Id == finalMail && letter.Currency[1] == 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarketPurchaseWaitsForCommittedParticipantApplyThenUsesNewBalances(bool speculativeRefusal)
    {
        var (bot, session) = await Login("Restagebuy"); await using var client = bot;
        long buyer = session.Role!.Id, before = session.Progress!.Currency[1];
        long seller = Roles.Create("restageseller", 1, "RestageSeller", 1, 1000, 1)!.Id;
        var applying = Signal(); var release = Signal(); var deferred = Signal();
        int held = 0;
        object? firstWork = null;
        Accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "market/transaction" || !ReferenceEquals(Loop.CurrentWork, firstWork)
                || Interlocked.Exchange(ref held, 1) != 0) return Task.CompletedTask;
            applying.TrySetResult(); return release.Task;
        };
        Owners.DeferredForTest = origin => { if (origin == "market") deferred.TrySetResult(); };
        var first = Loop.InvokeAsync(() =>
        {
            firstWork = Loop.CurrentWork ?? throw new InvalidOperationException("Market test operation was not admitted.");
            return Market.RunAsync(session, tx =>
            {
                if (!speculativeRefusal) return StagePurchase(tx, buyer, seller);
                tx.Role(buyer).Currency[1] += 100;
                tx.Economy.Source(new(EconomyAssetKind.Currency, 1), 100, EconomyReason.GmGrant);
                return 0;
            });
        });
        Task<int>? second = null;
        try
        {
            await applying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(before + (speculativeRefusal ? 100 : -100), Roles.LoadProgress(buyer).Currency[1]);
            Assert.Equal(before, session.Progress.Currency[1]);
            second = Loop.InvokeAsync(() => Market.RunAsync(session, tx =>
            {
                if (speculativeRefusal && session.Progress.Currency[1] < before + 100) return ErrorCodes.ItemsShort;
                return StagePurchase(tx, buyer, seller);
            }));
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(second.IsCompleted);
        }
        finally { release.TrySetResult(); Accounts.BeforeOwnedApplyForTest = null; Owners.DeferredForTest = null; }
        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await second!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(before - (speculativeRefusal ? 0 : 200), Roles.LoadProgress(buyer).Currency[1]);
        Assert.Equal(before - (speculativeRefusal ? 0 : 200), session.Progress.Currency[1]);
        Assert.Equal(speculativeRefusal ? 40 : 80, Roles.LoadProgress(seller).Currency[1]);
        Assert.Equal(speculativeRefusal ? 1 : 2, JsonSerializer.Deserialize<MailService.Mailbox>(Roles.LoadMailbox(seller)!)!.Letters.Count);
    }

    [Fact]
    public async Task LostWriterAcknowledgementResolvesReceiptWithoutRepeatingMutation()
    {
        long role = Roles.Create("ack", 1, "AckOwner", 1, 1000, 1)!.Id;
        int injected = 0, applied = 0;
        Accounts.AfterWriterCommitForTest = label =>
        {
            if (label == "test/lost-ack") { Interlocked.Increment(ref injected); throw new IOException("lost acknowledgement"); }
        };
        await Loop.InvokeAsync(() => Owners.RunAsync("ack owner", [ReservationKey.Role(role)], async () =>
        {
            var plan = Roles.NewTransaction();
            var progress = Roles.LoadProgress(role); progress.Currency[1] = 7;
            plan.Progress(role, progress);
            plan.Add(new RoleBlobWritePlan(role, "settings", [7]), _ => applied++);
            await Roles.CommitAsync(plan, "test/lost-ack");
        }));
        Assert.Equal(1, injected); Assert.Equal(1, applied);
        Assert.Equal(7, Roles.LoadProgress(role).Currency[1]);
        using var db = Accounts.Open(); using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM owner_operation_receipt WHERE family='test/lost-ack'";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public async Task FailedCommittedApplyQuarantinesParticipantsInsteadOfAdmittingStaleCaches()
    {
        long role = Roles.Create("apply", 1, "ApplyOwner", 1, 1000, 1)!.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Loop.InvokeAsync(() => Owners.RunAsync("apply owner",
            [ReservationKey.Role(role)], async () =>
            {
                var plan = Roles.NewTransaction();
                plan.Add(new RoleBlobWritePlan(role, "settings", [9]), _ => throw new InvalidOperationException("injected apply failure"));
                await Roles.CommitAsync(plan, "test/failed-apply");
            })));
        Assert.Equal(new byte[] { 9 }, Roles.LoadSettings(role));
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(RoleOwnerState.RecoveryRequired, Owners.Get(role).State);
            Assert.Equal(1, Owners.Reservations.RecoveryRequired);
            Assert.Throws<InvalidOperationException>(() => Owners.TryEnter("stale cache", ReservationKey.Role(role)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FailedExchangePublicationKeepsCommittedCreditAndQuarantinesRestoredLiveState()
    {
        var (bot, session) = await Login("PublishOwner"); await using var client = bot;
        long role = session.Role!.Id;
        long before = session.Progress!.Currency.GetValueOrDefault(1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Loop.InvokeAsync(() => Owners.RunAsync("exchange publish",
            [ReservationKey.Role(role)], async () =>
            {
                using var state = await CharacterService.Lock(session);
                await _app.Services.GetRequiredService<CharacterService>().ExchangeLockedAsync(session, [], [],
                    popup: false, sync: false, currencyGoods: new Dictionary<int, long> { [1] = 7 }, asyncCommit: true,
                    appliedBeforeNotify: () => throw new InvalidOperationException("injected publication failure"));
            })));
        Assert.Equal("injected publication failure", error.Message);
        Assert.Equal(before + 7, Roles.LoadProgress(role).Currency[1]);
        Assert.Equal(before, session.Progress.Currency.GetValueOrDefault(1));
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(RoleOwnerState.RecoveryRequired, Owners.Get(role).State);
            Assert.Equal(1, Owners.Reservations.RecoveryRequired);
            Assert.Throws<InvalidOperationException>(() => Owners.TryEnter("failed exchange", ReservationKey.Role(role)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SqlConflictRollsBackEveryParticipantAndRetryHasOneOutcome()
    {
        var (bot, buyer) = await Login("Conflictbuyer"); await using var client = bot;
        long seller = Roles.Create("seller", 1, "ConflictSeller", 1, 1000, 1)!.Id;
        long buyerId = buyer.Role!.Id, before = buyer.Progress!.Currency[1];
        await Accounts.WriteAsync<int>(new RejectMailboxPlan(true));
        Assert.Equal(ErrorCodes.Unknown, await Loop.InvokeAsync(() => Market.RunAsync(buyer,
            tx => StagePurchase(tx, buyerId, seller))));
        Assert.Equal(before, buyer.Progress.Currency[1]);
        Assert.Equal(before, Roles.LoadProgress(buyerId).Currency[1]);
        Assert.Null(Roles.LoadMailbox(buyerId)); Assert.Null(Roles.LoadMailbox(seller));
        Assert.Empty(Accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("trade_sequence")));
        await Accounts.WriteAsync<int>(new RejectMailboxPlan(false));
        Assert.Equal(0, await Loop.InvokeAsync(() => Market.RunAsync(buyer, tx => StagePurchase(tx, buyerId, seller))));
        Assert.Equal(before - 100, buyer.Progress.Currency[1]);
        Assert.Single(JsonSerializer.Deserialize<MailService.Mailbox>(Roles.LoadMailbox(seller)!)!.Letters);
    }

    [Fact]
    public async Task MailClaimKeepsLiveStateUnchangedUntilReceiptAndAllowsUnrelatedProgress()
    {
        var (bot, owner) = await Login("Claimowner"); await using var client = bot;
        long role = owner.Role!.Id;
        string mail = await _app.Services.GetRequiredService<MailService>().SendRawAsync(role, "claim", "owner", [new(1, 7)]);
        long before = owner.Progress!.Currency[1];
        long other = Roles.Create("other", 1, "ClaimOther", 1, 1000, 1)!.Id;
        var entered = Signal(); var release = Signal();
        Accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "character/exchange") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        await bot.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mail } });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(before + 7, Roles.LoadProgress(role).Currency[1]);
            await Loop.InvokeAsync(() => Owners.RunAsync("claim other", [ReservationKey.Role(other)], () =>
            {
                Assert.Equal(before, owner.Progress!.Currency[1]);
                var p = Roles.LoadProgress(other); p.Exp = 19; Roles.SaveProgress(other, p);
                return Task.CompletedTask;
            })).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        var result = ClaimMailRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(x => x.IsGame(MailService.ClaimMail), TimeSpan.FromSeconds(10))).Body);
        Assert.Equal(0, result.Result); Assert.Equal(new[] { mail }, result.MailUid);
        Assert.Equal(before + 7, owner.Progress.Currency[1]);
    }
}
