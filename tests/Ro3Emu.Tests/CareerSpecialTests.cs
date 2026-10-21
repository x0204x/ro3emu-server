using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Special Class Items over the wire: 5002 DBCareerSpecial, 32721 lease, 32722 cancel, pendants, battle mount.</summary>
public sealed class CareerSpecialTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-career-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const int Cart = 1500000, BirchCart = 1500001, Falcon = 1500100, GrandPeco = 1500203, KnightPeco = 1500201;

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); return b;
    }

    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> p, Func<T, bool>? match = null) where T : IMessage<T>
    { var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(p.ParseFrom(e.Body))), Wait); return p.ParseFrom(e.Body); }

    private async Task Gm(string account, string command)
    { using var h = new HttpClient(); (await h.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode(); }

    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    /// <summary>A career, base level and learned skill groups, persisted like a real learn (the skill tree is not driven here).</summary>
    private async Task Become(string account, int career, int level, params int[] skillGroups)
    {
        await Gm(account, $"!job {career}");
        await Gm(account, $"!lvl {level}");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(20)) // the GM commits finish on the loop
        {
            bool applied = false;
            await loop.InvokeAsync(() => { applied = Session(account).Progress is { } p && p.Career == career && p.Level == level; return Task.CompletedTask; });
            if (applied) break;
            Assert.True(DateTime.UtcNow < until, "job/level not applied");
        }
        // Level-up observers may still commit progress right after the GM reply: retry on their newer revision.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await loop.InvokeAsync(() =>
                {
                    var s = Session(account);
                    foreach (int group in skillGroups) s.Progress!.Skills[group] = 1;
                    _app.Services.GetRequiredService<RoleStore>().SaveProgress(s.Role!.Id, s.Progress!);
                    return Task.CompletedTask;
                });
                return;
            }
            catch (Ro3Emu.Server.Accounts.Persistence.OwnerRevisionConflictException) when (attempt < 50) { await Task.Delay(50); }
        }
    }

    private static async Task<int> LeaseAsync(BotClient b, int id)
    {
        await b.SendGameAsync(CareerSpecialService.Lease, new LeaseCareerSpecialItemReq { CartRemouldCfgId = id });
        return (await Next(b, CareerSpecialService.Lease, LeaseCareerSpecialItemRsp.Parser)).ErrCode;
    }

    private static async Task<int> CancelAsync(BotClient b, int id)
    {
        await b.SendGameAsync(CareerSpecialService.CancelLease, new CancelLeaseCareerSpecialItemReq { CartRemouldCfgId = id });
        return (await Next(b, CareerSpecialService.CancelLease, CancelLeaseCareerSpecialItemRsp.Parser)).ErrCode;
    }

    [ClientDataFact]
    public async Task LoginSendsBothEmptySlotsAndRefusalsFollowCanWearProfessionItem()
    {
        await using var a = await InWorld("Alice");
        var initial = await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
        Assert.True(initial.SyncAllMark);
        Assert.NotNull(initial.PendantSlot?.All); Assert.Equal(0, initial.PendantSlot!.All.CartRemouldCfg);
        Assert.NotNull(initial.MountsSlot?.All); Assert.Equal(0, initial.MountsSlot!.All.CartRemouldCfg);

        Assert.Equal(CareerSpecialService.ErrMissing, await LeaseAsync(a, 42));
        Assert.Equal(ErrorCodes.FunctionLocked, await LeaseAsync(a, Cart)); // function 16 opens at base level 10
        await Gm("alice", "!lvl 10");
        Assert.Equal(CareerSpecialService.ErrJob, await LeaseAsync(a, Cart)); // a Novice is not in _kProfessionID
        await Gm("alice", "!job 6100");
        Assert.Equal(CareerSpecialService.ErrSkill, await LeaseAsync(a, Cart)); // Cart Use (group 16003) not learned
        await Gm("alice", "!job 3200");
        Assert.Equal(CareerSpecialService.ErrLevel, await LeaseAsync(a, Falcon)); // _iUnlockBaseLv 30
        Assert.Equal(CareerSpecialService.ErrNotLeased, await CancelAsync(a, Falcon));
        Assert.Equal(0, Session("alice").CareerSpecial!.PendantSlot.CartRemouldCfg);
        var stored = _app.Services.GetRequiredService<RoleStore>().LoadCareerSpecial(Session("alice").Role!.Id);
        Assert.True(stored == null || DBCareerSpecial.Parser.ParseFrom(stored).PendantSlot?.CartRemouldCfg is null or 0);
    }

    [ClientDataFact]
    public async Task CartLeaseShowsThePendantToViewersPersistsAndCannotBeCancelledWhileTheCartHoldsItems()
    {
        await using (var a = await InWorld("Alice"))
        await using (var b = await InWorld("Bob"))
        {
            await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
            await Become("alice", 6100, 10, 16003, 16004); // Cart Use; Cart Remodeling unlocks the other carts
            Assert.Equal(0, await LeaseAsync(a, Cart));
            var lease = await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser, p => !p.SyncAllMark);
            Assert.Equal(Cart, lease.PendantSlot.Inc.CartRemouldCfg);
            foreach (var bot in new[] { a, b })
            {
                var shown = await Next(bot, WorldEffectsService.Pendant, PendantUpdate.Parser, p => p.EntityID == (long)a.Self!.Uuid);
                Assert.Equal(51001, shown.PendantInfo[51001].PendantId);
                Assert.False(shown.PendantInfo[51001].IsHide);
            }
            Assert.True(CareerSpecialService.HoldsSubType(Session("alice"), _app.Services.GetRequiredService<GameData>(), 1));

            // Another cart replaces the first one in the same slot; the old pendant goes hidden.
            Assert.Equal(0, await LeaseAsync(a, BirchCart));
            var swap = await Next(b, WorldEffectsService.Pendant, PendantUpdate.Parser, p => p.PendantInfo.ContainsKey(51002));
            Assert.True(swap.PendantInfo[51001].IsHide);
            Assert.False(swap.PendantInfo[51002].IsHide);

            // Pack 100 holds an item: the client's 23034 rule, server code 1111.
            var items = Session("alice").Items;
            long potion = items.Single(i => i.Entry == 1000000 && i.Pack != 100).Guid;
            var move = new CareerSpecialTransferItemsReq { TransType = CareerSpecialTransferItemsType.ToCart };
            move.ItemGuidCountMap[potion] = 2;
            await a.SendGameAsync(CharacterService.CareerSpecialTransfer, move);
            Assert.Equal(0, (await Next(a, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            Assert.Equal(CareerSpecialService.ErrCartNotEmpty, await CancelAsync(a, BirchCart));
            Assert.Equal(BirchCart, Session("alice").CareerSpecial!.PendantSlot.CartRemouldCfg);
            var back = new CareerSpecialTransferItemsReq { TransType = CareerSpecialTransferItemsType.ToNormal };
            back.ItemGuidCountMap[Session("alice").Items.Single(i => i.Pack == 100).Guid] = 2;
            await a.SendGameAsync(CharacterService.CareerSpecialTransfer, back);
            Assert.Equal(0, (await Next(a, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            Assert.Equal(0, await CancelAsync(a, BirchCart));
            var cancel = await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser, p => p.PendantSlot?.Inc != null && p.PendantSlot.Inc.HasCartRemouldCfg && p.PendantSlot.Inc.CartRemouldCfg == 0);
            Assert.NotNull(cancel);
            var hidden = await Next(b, WorldEffectsService.Pendant, PendantUpdate.Parser, p => p.PendantInfo.TryGetValue(51002, out var d) && d.IsHide);
            Assert.Single(hidden.PendantInfo);
            Assert.Equal(0, await LeaseAsync(a, Cart));
        }
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
        Assert.Equal(Cart, restored.PendantSlot.All.CartRemouldCfg);
        Assert.Equal(51001, again.Self!.PendantInfo[51001].PendantId);
    }

    [ClientDataFact]
    public async Task CrusaderPecoLeaseSelectsTheBattleSlotRidesAndCancelDismounts()
    {
        await using var a = await InWorld("Alice");
        await using var b = await InWorld("Bob");
        await Become("alice", 1210, 30, 11407); // Crusader, Riding
        Assert.Equal(CareerSpecialService.ErrJob, await LeaseAsync(a, KnightPeco)); // Peco Peco is the Knight line's
        Assert.Equal(0, await LeaseAsync(a, GrandPeco));
        await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser, p => p.MountsSlot?.Inc?.CartRemouldCfg == GrandPeco);
        var slot = await Next(a, MountService.Sync, DBMountsInfoIncrement.Parser, p => p.MountIds.ContainsKey(2));
        Assert.Equal(GrandPeco, Session("alice").Mounts!.MountIds[2].MountId);
        Assert.False(Session("alice").Mounts!.MountIds[2].IsRiding);
        Assert.NotNull(slot);
        Assert.Empty(Session("alice").CareerPendants); // a Peco has no pendant; it shows only when ridden

        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 }); // on-battle
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(GrandPeco, MountService.Rider(Session("alice")));
        foreach (var bot in new[] { a, b })
            await Next(bot, CharacterService.EntityBaseChange, EntityBase.Parser,
                p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == GrandPeco);

        Assert.Equal(0, await CancelAsync(a, GrandPeco));
        Assert.Equal(0, MountService.Rider(Session("alice")));
        Assert.Equal(0, Session("alice").Mounts!.MountIds[2].MountId);
        await Next(b, CharacterService.EntityBaseChange, EntityBase.Parser,
            p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 0);
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 });
        Assert.NotEqual(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task JobChangeRepairsALeaseTheNewCareerCannotHoldAndReleasesTheBattleSlot()
    {
        await using (var a = await InWorld("Alice"))
        {
            await Become("alice", 1210, 30, 11407);
            Assert.Equal(0, await LeaseAsync(a, GrandPeco));
            await Gm("alice", "!job 1310"); // Paladin: still in _kProfessionID
            Assert.Equal(GrandPeco, Session("alice").CareerSpecial!.MountsSlot.CartRemouldCfg);
            await Gm("alice", "!job 6100"); // Merchant
            await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser,
                p => p.MountsSlot?.Inc != null && p.MountsSlot.Inc.HasCartRemouldCfg && p.MountsSlot.Inc.CartRemouldCfg == 0);
            Assert.Equal(0, Session("alice").CareerSpecial!.MountsSlot.CartRemouldCfg);
            Assert.Equal(0, Session("alice").Mounts!.MountIds[2].MountId);
        }
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
        Assert.Equal(0, restored.MountsSlot.All.CartRemouldCfg);
    }

    private static string RepoRoot()
    { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName; throw new DirectoryNotFoundException(); }
}
