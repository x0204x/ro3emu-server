using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>5034 ownership, wardrobe RPCs and AOI projection.</summary>
public sealed class WardrobeTests : IAsyncLifetime
{
    private const int MaleHair = 70000000, OtherMaleHair = 70000001, FemaleHair = 72000000, Head = 10030417;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-wardrobe-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        var response = await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line));
        response.EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task DefaultsPlansLikesAndAttributeSourceSurviveRelog()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var initial = await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
            Assert.True(initial.SyncAllMark);
            Assert.True(initial.IsInit);
            Assert.Equal(15, initial.AllLightUpAppearances.Count); // all 15 male defaults from WardRobeConfig.
            Assert.Equal(MaleHair, initial.CurUseAppearances[7].All);
            Assert.Equal(5, initial.AppearancePlans.Count); // PublicParamConfig.FashionMatchNum.
            Assert.Equal(MaleHair, initial.AppearancePlans[0].All.Appearances[7]);

            await alice.SendGameAsync(WardrobeService.SetLike, new WardRobeSetLikeReq { AppreanceId = Head, OpType = 0 });
            Assert.Equal(0, (await Next(alice, WardrobeService.SetLike, WardRobeSetLikeResp.Parser)).Result);
            var liked = await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
                i => i.LikeAppearance.ContainsKey(Head));
            Assert.Equal(0, liked.LikeAppearance[Head].All);

            var source = new WardRobeUseAppearanceAttrReq { AppearanceId = MaleHair };
            await alice.SendGameAsync(WardrobeService.UseAttr, source);
            var sourceReply = await Next(alice, WardrobeService.UseAttr, WardRobeUseAppearanceAttrRes.Parser);
            Assert.Equal((0, MaleHair), (sourceReply.Result, sourceReply.Req.AppearanceId));
            var attr = await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
                i => i.CurUseAppearanceAttrs.ContainsKey(7));
            Assert.Equal(MaleHair, attr.CurUseAppearanceAttrs[7].All);

            var save = new WardRobeSaveAppearancePlanReq { Index = 1 };
            save.Appearances[7] = OtherMaleHair;
            await alice.SendGameAsync(WardrobeService.SavePlan, save);
            var saved = await Next(alice, WardrobeService.SavePlan, WardRobeSaveAppearancePlanRes.Parser);
            Assert.Equal((0, 1), (saved.Result, saved.Req.Index));
            await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
                i => i.AppearancePlans.ContainsKey(1));

            await alice.SendGameAsync(WardrobeService.ApplyPlan, new WardRobeApplyAppearancePlanReq { Index = 1 });
            var applied = await Next(alice, WardrobeService.ApplyPlan, WardRobeApplyAppearancePlanRes.Parser);
            Assert.Equal((0, 1), (applied.Result, applied.Req.Index));
            await Next(alice, CharacterService.EntityBaseChange, EntityBase.Parser,
                e => e.NotifyType == EntityBase.Types.NotifyType.WardRobe && e.CurUseAppearances[7] == OtherMaleHair);
        }
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        Assert.Equal(0, restored.LikeAppearance[Head].All);
        Assert.Equal(MaleHair, restored.CurUseAppearanceAttrs[7].All);
        Assert.Equal(OtherMaleHair, restored.AppearancePlans[1].All.Appearances[7]);
        Assert.Equal(OtherMaleHair, restored.CurUseAppearances[7].All);
    }

    [ClientDataFact]
    public async Task RedeemCostsAreAtomicAndWearAndPlanChangesReachViewers()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        await alice.SendGameAsync(WardrobeService.Redeem, new WardRobeRedeemUnlockAppearanceReq { AppearanceId = Head });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, WardrobeService.Redeem, WardRobeRedeemUnlockAppearanceResp.Parser)).Result);

        await Gm("alice", "!item 1243001 25");
        await Gm("alice", "!item 1243003 25");
        // GM item grants cap a single non-currency command at 999.
        await Gm("alice", "!item 1242001 999");
        await Gm("alice", "!item 1242001 1");
        await Gm("alice", "!item 1242005 999");
        await Gm("alice", "!item 1242005 1");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var character = _app.Services.GetRequiredService<CharacterService>();
        Assert.Equal(25, character.CountOf(session, 1243001));
        Assert.Equal(25, character.CountOf(session, 1243003));
        Assert.Equal(1000, character.CountOf(session, 1242001));
        Assert.Equal(1000, character.CountOf(session, 1242005));
        await alice.SendGameAsync(WardrobeService.Redeem, new WardRobeRedeemUnlockAppearanceReq { AppearanceId = Head });
        var owned = await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            i => i.AllLightUpAppearances.ContainsKey(Head));
        Assert.Equal(Head, owned.AllLightUpAppearances[Head].All.AppearanceId);
        Assert.Equal(Head, (await Next(alice, WardrobeService.UnlockNotice, WardRobeUnLockNtf.Parser)).AppearanceId);
        Assert.Equal(0, (await Next(alice, WardrobeService.Redeem, WardRobeRedeemUnlockAppearanceResp.Parser)).Result);
        await alice.SendGameAsync(WardrobeService.Redeem, new WardRobeRedeemUnlockAppearanceReq { AppearanceId = Head });
        Assert.NotEqual(0, (await Next(alice, WardrobeService.Redeem, WardRobeRedeemUnlockAppearanceResp.Parser)).Result);

        var use = new WardRobeUseAppearanceReq();
        use.AppearanceIds.Add([MaleHair, Head]);
        await alice.SendGameAsync(WardrobeService.Use, use);
        Assert.Equal(0, (await Next(alice, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        var visible = await Next(bob, CharacterService.EntityBaseChange, EntityBase.Parser,
            e => e.Uuid == alice.Self!.Uuid && e.NotifyType == EntityBase.Types.NotifyType.WardRobe);
        Assert.Equal((MaleHair, Head), (visible.CurUseAppearances[7], visible.CurUseAppearances[1]));

        var save = new WardRobeSaveAppearancePlanReq { Index = 1 };
        save.Appearances[7] = OtherMaleHair;
        await alice.SendGameAsync(WardrobeService.SavePlan, save);
        Assert.Equal(0, (await Next(alice, WardrobeService.SavePlan, WardRobeSaveAppearancePlanRes.Parser)).Result);
        await alice.SendGameAsync(WardrobeService.ApplyPlan, new WardRobeApplyAppearancePlanReq { Index = 1 });
        Assert.Equal(0, (await Next(alice, WardrobeService.ApplyPlan, WardRobeApplyAppearancePlanRes.Parser)).Result);
        var bare = await Next(bob, CharacterService.EntityBaseChange, EntityBase.Parser,
            e => e.Uuid == alice.Self!.Uuid && e.NotifyType == EntityBase.Types.NotifyType.WardRobe && !e.CurUseAppearances.ContainsKey(1));
        Assert.Equal(OtherMaleHair, bare.CurUseAppearances[7]);
    }

    [ClientDataFact]
    public async Task InvalidOrUnownedLooksAndPlansAreRefusedWithoutChangingTheHair()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        foreach (int id in new[] { FemaleHair, Head, 99999999 })
        {
            var req = new WardRobeUseAppearanceReq();
            req.AppearanceIds.Add(id);
            await alice.SendGameAsync(WardrobeService.Use, req);
            Assert.NotEqual(0, (await Next(alice, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        }
        var duplicate = new WardRobeUseAppearanceReq();
        duplicate.AppearanceIds.Add([MaleHair, MaleHair]);
        await alice.SendGameAsync(WardrobeService.Use, duplicate);
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);

        await alice.SendGameAsync(WardrobeService.ApplyPlan, new WardRobeApplyAppearancePlanReq { Index = 5 });
        var badPlan = await Next(alice, WardrobeService.ApplyPlan, WardRobeApplyAppearancePlanRes.Parser);
        Assert.Equal((ErrorCodes.Parameter, 5), (badPlan.Result, badPlan.Req.Index));
        var invalid = new WardRobeSaveAppearancePlanReq { Index = 1 };
        invalid.Appearances[1] = FemaleHair; // type 7 in head slot.
        await alice.SendGameAsync(WardrobeService.SavePlan, invalid);
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, WardrobeService.SavePlan, WardRobeSaveAppearancePlanRes.Parser)).Result);
        Assert.Equal(MaleHair, alice.Self!.CurUseAppearances[7]);
    }

    [ClientDataFact]
    public async Task ActivatedAppearanceItemConsumesOneCopyAndUnlocksItsConfiguredLook()
    {
        const int HairVoucher = 3025701, NewHair = 70050010; // ItemConfig action 200 ActivatedAppearance.
        await using var alice = await InWorld("Alice");
        await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        await Gm("alice", $"!item {HairVoucher} 2");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.Values.Any(p => p.Inc?.Items.Values.Any(i => i.All?.ItemEntry == HairVoucher) == true));
        var voucher = bag.PackInfo.Values.SelectMany(p => p.Inc?.Items.Values ?? []).Single(i => i.All?.ItemEntry == HairVoucher).All;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = voucher.ItemGuid, ItemCount = 1 });
        var unlocked = await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            i => i.AllLightUpAppearances.ContainsKey(NewHair));
        Assert.Equal(NewHair, unlocked.AllLightUpAppearances[NewHair].All.AppearanceId);
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        var remaining = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            i => i.PackInfo.Values.Any(p => p.Inc?.Items.ContainsKey(voucher.ItemGuid) == true));
        Assert.Equal(1, remaining.PackInfo.Values.SelectMany(p => p.Inc?.Items.Values ?? [])
            .Single(i => i.Inc?.Stack == 1).Inc.Stack);
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = voucher.ItemGuid, ItemCount = 1 });
        Assert.NotEqual(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task LateDeadlineRemovesWornTimedAppearanceAndPersistsTheReconciledState()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var wardrobe = _app.Services.GetRequiredService<WardrobeService>();
        var deadlines = _app.Services.GetRequiredService<Deadlines>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var due = DateTimeOffset.FromUnixTimeMilliseconds(_clock.GetUtcNow().ToUnixTimeMilliseconds() + 1250);
        await loop.InvokeAsync(async () =>
        {
            var info = session.Wardrobe!.Clone();
            info.AllLightUpAppearances[Head] = new DBAppearanceInfo
            {
                AppearanceId = Head, OverdueTimeMs = due.ToUnixTimeMilliseconds()
            };
            info.CurUseAppearanceAttrs[1] = Head;
            info.AppearancePlans[1].Appearances[1] = Head;
            roles.SaveWardrobe(session.Role!.Id, info.ToByteArray());
            await wardrobe.LoginAsync(session); // the normal role-load path arms the next expiry
        });
        var wear = new WardRobeUseAppearanceReq();
        wear.AppearanceIds.Add([MaleHair, Head]);
        await alice.SendGameAsync(WardrobeService.Use, wear);
        Assert.Equal(0, (await Next(alice, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        await Next(bob, CharacterService.EntityBaseChange, EntityBase.Parser,
            p => p.Uuid == alice.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.WardRobe &&
                 p.CurUseAppearances.TryGetValue(1, out var appearance) && appearance == Head);

        await loop.InvokeAsync(() => { _clock.Set(due.AddTicks(-1)); deadlines.Tick(default); return Task.CompletedTask; });
        await loop.InvokeAsync(() => wardrobe.ReconcileAsync(session)); // former polling oracle, before due
        Assert.True(session.Wardrobe!.AllLightUpAppearances.ContainsKey(Head));
        Assert.Equal(Head, session.Wardrobe.CurUseAppearances[1]);

        await loop.InvokeAsync(() => { _clock.Set(due.AddSeconds(7)); deadlines.Tick(default); return Task.CompletedTask; });
        await Next(alice, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            p => p.AllLightUpAppearances.TryGetValue(Head, out var appearance) && appearance.Del);
        await Next(bob, CharacterService.EntityBaseChange, EntityBase.Parser,
            p => p.Uuid == alice.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.WardRobe &&
                 !p.CurUseAppearances.ContainsKey(1));
        Assert.False(session.Wardrobe!.AllLightUpAppearances.ContainsKey(Head));
        Assert.False(session.Wardrobe.CurUseAppearances.ContainsKey(1));
        Assert.False(session.Wardrobe.CurUseAppearanceAttrs.ContainsKey(1));
        Assert.False(session.Wardrobe.AppearancePlans[1].Appearances.ContainsKey(1));
        var saved = roles.LoadWardrobe(session.Role!.Id)!;
        Assert.Equal(session.Wardrobe.ToByteArray(), saved);
        await loop.InvokeAsync(() => wardrobe.ReconcileAsync(session));
        Assert.Equal(saved, roles.LoadWardrobe(session.Role.Id));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }
}
