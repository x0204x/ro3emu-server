using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Adventure Group / Adv. Guild (AdventureGroupService) over the wire: the support recruitment and the creation ritual in
/// dungeon 1010001 with three members, support and join limits, leave / kick / owner transfer, the daily watering of the
/// tree and its group quest, the once-only level reward, persistence across a restart.
/// </summary>
public sealed class AdventureGroupTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-advguild-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private const long Ritual = 1010001, Prontera = 10100;
    private const int Sapling = 200033;

    /// <summary>Thursday 2026-10-08 12:00 UTC.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(long count) => Interlocked.Add(ref _seconds, count);
    }

    public Task InitializeAsync() => StartServerAsync();

    private async Task StartServerAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
            o.OpenContent.Enabled = true; // function 1000000 is base level 9999 in the export
            o.GuildBlockedWords = ["ban"];
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    private async Task RestartAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        ClearOwnPool();
        await StartServerAsync();
    }

    private void ClearOwnPool()
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "ro3emu.db")}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(db);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        ClearOwnPool();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Ro3Emu.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private AdventureGroupStore Store => Service<AdventureGroupStore>();

    private async Task<BotClient> InWorld(string name, bool create = true, int level = 40)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        if (create && level > 0)
        {
            await Gm(bot, $"!lvl {level}");
            await Until(() => Session(bot).Progress!.Level >= level, "level " + level);
        }
        return bot;
    }

    private static long Id(BotClient bot) => (long)bot.Self!.Uuid;

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>()
        .All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            bool done = false;
            await OnLoop(() => { done = condition(); return Task.CompletedTask; });
            if (done) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    private static async Task<NormalAdventureGroupOpeRes> Ope(BotClient bot, OpeType op, NormalOpeData data)
    {
        await bot.SendGameAsync(AdventureGroupService.NormalOpe, new NormalAdventureGroupOpeReq { Tp = op, Opedata = data });
        return await Next(bot, AdventureGroupService.NormalOpe, NormalAdventureGroupOpeRes.Parser);
    }

    private static async Task<HelperAdventureGroupApplyerOpeRes> Helper(BotClient bot, OpeType op, long target, long group)
    {
        await bot.SendGameAsync(AdventureGroupService.HelperOpe,
            new HelperAdventureGroupApplyerOpeReq { Op = op, TargetPlayerid = target, AdventureGroupId = group });
        return await Next(bot, AdventureGroupService.HelperOpe, HelperAdventureGroupApplyerOpeRes.Parser);
    }

    private static async Task<int> Result<TReq>(BotClient bot, uint id, TReq request, Func<ByteString, int> result) where TReq : IMessage
    {
        await bot.SendGameAsync(id, request);
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait);
        return result(ByteString.CopyFrom(packet.Body));
    }

    private static Task<AdventureGroupDataNotify> Full(BotClient bot, Func<AdventureGroupDataNotify, bool> match) =>
        Next(bot, AdventureGroupService.FullData, AdventureGroupDataNotify.Parser, match);

    private static async Task Load(BotClient bot, long scene)
    {
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == scene)).Result);
    }

    /// <summary>A group written straight to the store (owners need not exist): list / limit fixtures.</summary>
    private AdventureGroupStore.State Seed(string name, int status, long owner, int members = 1, int applications = 0)
    {
        var g = new AdventureGroupStore.State
        {
            Name = name, Status = status, Owner = owner, CreateTime = _clock.GetUtcNow().ToUnixTimeSeconds(), Level = 1,
            CreateType = status == AdventureGroupService.StatusHelper ? 1 : 2,
        };
        for (int i = 0; i < members; i++) g.Members[owner + i] = new AdventureGroupStore.Member { RoleId = owner + i, Name = $"{name}{i}" };
        for (int i = 0; i < applications; i++)
            g.Applications[owner + 500 + i] = new AdventureGroupStore.Applicant { RoleId = owner + 500 + i, Name = $"{name}a{i}" };
        var roles = Service<RoleStore>();
        var tx = roles.NewTransaction();
        Store.EnlistSave(g, tx);
        roles.Commit(tx, "test/advguild-seed");
        return Store.Get(g.Id)!;
    }

    private async Task<long> GmCreate(BotClient bot, string name)
    {
        await Gm(bot, $"!advguild create {name}");
        var full = await Full(bot, n => n.Data?.Data?.Name == name);
        Assert.Equal(AdventureGroupService.StatusNormal, full.Data.Data.Status);
        return full.Data.Uuid;
    }

    // ------------------------------------------------------------------------------------------------ tests

    [ClientDataFact]
    public async Task ThreeMembersRecruitPartyAndFoundTheGroupThroughTheRitual()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long a = Id(alice), b = Id(bob), c = Id(carol);

        // 5068 is always synced; nobody is in a group yet.
        var empty = await Next(bob, AdventureGroupService.RelationSync, DBAdventureGroupRelationDataIncrement.Parser);
        Assert.True(empty.SyncAllMark);

        Assert.Equal(AdventureGroupService.ErrBanned, await Result(alice, AdventureGroupService.Create,
            new CreateAdventureGroupReq { Name = "Banana", CreateType = AdventureGroupCreateType.CreateTypeRecruit },
            body => CreateAdventureGroupRes.Parser.ParseFrom(body).Result));
        Assert.Equal(AdventureGroupService.ErrNameInvalid, await Result(alice, AdventureGroupService.Create,
            new CreateAdventureGroupReq { Name = "A", CreateType = AdventureGroupCreateType.CreateTypeRecruit },
            body => CreateAdventureGroupRes.Parser.ParseFrom(body).Result));
        await alice.SendGameAsync(AdventureGroupService.Create, new CreateAdventureGroupReq
        {
            Name = "Astra", Announce = "Join us", Label = { 1, 2 }, Background = 2, CreateType = AdventureGroupCreateType.CreateTypeRecruit,
        });
        var created = await Next(alice, AdventureGroupService.Create, CreateAdventureGroupRes.Parser);
        Assert.Equal(0, created.Result);
        Assert.Equal("Astra", created.Req.Name); // the DAL reads res.req.name unguarded (DAL:256)
        var mine = await Full(alice, n => n.Data?.Data?.Name == "Astra");
        long group = mine.Data.Uuid;
        Assert.Equal(AdventureGroupService.StatusHelper, mine.Data.Data.Status);
        Assert.Equal(a, Assert.Single(mine.Data.MemberData).PlayerID);

        // Support list and responses: bob and carol respond, alice accepts both.
        await bob.SendGameAsync(AdventureGroupService.HelperList, new PullHelperAdventureGroupListReq { Count = 200, ConditionJoinData = new ConditionJoin() });
        var list = await Next(bob, AdventureGroupService.HelperList, PullHelperAdventureGroupListRes.Parser);
        Assert.Equal(group, Assert.Single(list.Data).Uuid);
        foreach (var bot in new[] { bob, carol })
        {
            Assert.Equal(0, (await Helper(bot, OpeType.OpeHelperApply, Id(bot), group)).Result);
            await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
                n => n.Tp == ChangeType.ChangeApplyAdd && n.ApplyData.PlayerID == Id(bot));
            Assert.Equal(0, (await Helper(alice, OpeType.OpeAgree, Id(bot), 0)).Result);
            await Full(bot, n => n.Data?.Uuid == group);
        }
        Assert.Equal(AdventureGroupService.ErrJoined, (await Helper(bob, OpeType.OpeHelperApply, b, group)).Result); // already a member
        Assert.Equal(3, Store.Get(group)!.Members.Count);

        // Only the owner starts; 34006 invites the responders into alice's party.
        Assert.Equal(AdventureGroupService.ErrPermission, await Result(bob, AdventureGroupService.CheckBrothers,
            new CheckCanStartBecomeSwornBrothersReq(), body => CheckCanStartBecomeSwornBrothersRes.Parser.ParseFrom(body).Result));
        Assert.Equal(0, await Result(alice, AdventureGroupService.CheckBrothers,
            new CheckCanStartBecomeSwornBrothersReq(), body => CheckCanStartBecomeSwornBrothersRes.Parser.ParseFrom(body).Result));
        Assert.Equal(0, await Result(alice, AdventureGroupService.StartBrothers,
            new StartBecomeSwornBrothersReq(), body => StartBecomeSwornBrothersRes.Parser.ParseFrom(body).Result));
        foreach (var bot in new[] { bob, carol })
        {
            var invite = await Next(bot, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            Assert.Equal(a, invite.InviteGuid);
            await bot.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
            { TeamId = invite.TeamTotalSimpleInfo.Info.TeamId, IsAgree = true, InviteGuid = a });
            Assert.Equal(0, (await Next(bot, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }

        // 34014: the party enters the creation instance.
        Assert.Equal(0, await Result(alice, AdventureGroupService.EnterRitual,
            new EnterSwornBrothersDungeonReq(), body => EnterSwornBrothersDungeonRes.Parser.ParseFrom(body).Result));
        foreach (var bot in new[] { alice, bob, carol })
        {
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Ritual);
            await Load(bot, Ritual);
        }
        var snapshot = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            n => n.Vars.Any(v => v.VarId == 127 && v.Vi64.SequenceEqual([3L])));
        Assert.Equal(group, snapshot.InitData.AdventureGroupDungeonInitData.AdventureGroupId);
        Assert.Equal(AdventureGroupService.StatusTemp, Store.Get(group)!.Status);
        await Until(() => Session(carol).Tasks!.Infos.ContainsKey(2000500), "ritual quest 2000500");

        // Step 1: only the leader hangs the declaration.
        Assert.Equal(AdventureGroupService.ErrNoSaplingMember, await Result(bob, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = Sapling }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        Assert.Equal(AdventureGroupService.ErrPermission, await Result(bob, AdventureGroupService.SelectSapling,
            new SelectSaplingReq { SaplingId = Sapling }, body => SelectSaplingRes.Parser.ParseFrom(body).Result));
        Assert.Equal(0, await Result(alice, AdventureGroupService.SelectSapling,
            new SelectSaplingReq { SaplingId = Sapling }, body => SelectSaplingRes.Parser.ParseFrom(body).Result));
        await Next(carol, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangeBaseData && n.ChangeBaseData.SaplingData.SaplingId == Sapling);
        await Until(() => Session(carol).Tasks!.Infos.TryGetValue(2000501, out var t) && t.Status == TaskState.Running, "ritual quest 2000501");

        // Step 2: everyone waters, once.
        Assert.Equal(AdventureGroupService.ErrWrongSapling, await Result(bob, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = Sapling + 1 }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        foreach (var bot in new[] { alice, bob, carol })
            Assert.Equal(0, await Result(bot, AdventureGroupService.Water,
                new WateringSaplingReq { SaplingId = Sapling }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        Assert.Equal(AdventureGroupService.ErrWatered, await Result(alice, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = Sapling }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        await Until(() => Session(bob).Tasks!.Infos.TryGetValue(2000502, out var t) && t.Status == TaskState.Running, "ritual quest 2000502");

        // Step 3: everyone declares at the mentor; the last declaration founds the group.
        Assert.Equal(AdventureGroupService.ErrDeclaration, await Result(alice, AdventureGroupService.Declare,
            new AdventureGroupDeclarationReq { DeclarationData = new DeclarationInfo { DeclarationType = DeclarationType.TextType, Content = "" } },
            body => AdventureGroupDeclarationRes.Parser.ParseFrom(body).Result));
        foreach (var bot in new[] { alice, bob, carol })
            Assert.Equal(0, await Result(bot, AdventureGroupService.Declare,
                new AdventureGroupDeclarationReq { DeclarationData = new DeclarationInfo { DeclarationType = DeclarationType.TextType, Content = "Onward!" } },
                body => AdventureGroupDeclarationRes.Parser.ParseFrom(body).Result));
        foreach (var bot in new[] { alice, bob, carol })
        {
            await Next(bot, AdventureGroupService.CreatedNotify, AdventureGroupCreateNotify.Parser);
            var founded = await Full(bot, n => n.Data?.Data?.Status == AdventureGroupService.StatusNormal);
            Assert.Equal(a, founded.Data.MemberData[0].PlayerID); // memberData[1] is the owner
            Assert.Equal(3, founded.Data.MemberData.Count);
            Assert.All(founded.Data.MemberData, m => Assert.Equal("Onward!", m.DeclarationData.Content));
        }
        var stored = Store.Get(group)!;
        Assert.Equal(AdventureGroupService.StatusNormal, stored.Status);
        Assert.Equal(Sapling, stored.SaplingId);
        Assert.Equal(3, stored.SaplingWatering);
        Assert.Equal(1, stored.Level);
        Assert.Contains(stored.Events, e => e.Type == (int)AdventureGroupEventType.EventTypeCreate);
        Assert.Equal(new[] { a, b, c }.Order(), stored.Members.Keys.Order());
        await Until(() => TaskService.IsDelivered(Session(bob), 2000502), "ritual quests delivered");
    }

    [Fact]
    public async Task SupportAndJoinApplicationsAreLimited()
    {
        await using var bob = await InWorld("Bob");
        long b = Id(bob);
        // Support: at most AdventureGroupApplyHelperLimit (5) outstanding responses per player.
        var helpers = Enumerable.Range(0, 6).Select(i => Seed($"Help{i}", AdventureGroupService.StatusHelper, 9_000_000 + i * 1000)).ToList();
        for (int i = 0; i < 5; i++) Assert.Equal(0, (await Helper(bob, OpeType.OpeHelperApply, b, helpers[i].Id)).Result);
        Assert.Equal(AdventureGroupService.ErrApplyLimit, (await Helper(bob, OpeType.OpeHelperApply, b, helpers[5].Id)).Result);
        Assert.Equal(AdventureGroupService.ErrApplied, (await Helper(bob, OpeType.OpeHelperApply, b, helpers[0].Id)).Result);
        Assert.Equal(0, (await Helper(bob, OpeType.OpeHelperCancel, b, helpers[0].Id)).Result);
        Assert.Equal(0, (await Helper(bob, OpeType.OpeHelperApply, b, helpers[5].Id)).Result);
        // An expired support post (AdventureGroupHelperCreateTimeLimit 12 h) refuses responses.
        _clock.Advance(-13 * 3600);
        var late = Seed("Late", AdventureGroupService.StatusHelper, 9_100_000);
        _clock.Advance(13 * 3600);
        Assert.Equal(AdventureGroupService.ErrRecruitExpired, (await Helper(bob, OpeType.OpeHelperApply, b, late.Id)).Result);

        // Joining: a group's application list holds AdventureGroupNormalApplyLimit (20), a full group refuses.
        var crowded = Seed("Crowded", AdventureGroupService.StatusNormal, 9_200_000, applications: 20);
        Assert.Equal(AdventureGroupService.ErrAcceptLimit, (await Ope(bob, OpeType.OpeApply, new NormalOpeData { AdventureId = crowded.Id })).Result);
        var full = Seed("Full", AdventureGroupService.StatusNormal, 9_300_000, members: 10);
        Assert.Equal(AdventureGroupService.ErrFull, (await Ope(bob, OpeType.OpeApply, new NormalOpeData { AdventureId = full.Id })).Result);
        // At most AdventureGroupApplyNormalLimit (10) outstanding join applications per player.
        var open = Enumerable.Range(0, 11).Select(i => Seed($"Open{i}", AdventureGroupService.StatusNormal, 9_400_000 + i * 1000)).ToList();
        for (int i = 0; i < 10; i++) Assert.Equal(0, (await Ope(bob, OpeType.OpeApply, new NormalOpeData { AdventureId = open[i].Id })).Result);
        Assert.Equal(AdventureGroupService.ErrApplyLimit, (await Ope(bob, OpeType.OpeApply, new NormalOpeData { AdventureId = open[10].Id })).Result);
        Assert.Equal(0, (await Ope(bob, OpeType.OpeCancelApply, new NormalOpeData { AdventureId = open[0].Id })).Result);
        Assert.Equal(AdventureGroupService.ErrNoApplication, (await Ope(bob, OpeType.OpeCancelApply, new NormalOpeData { AdventureId = open[0].Id })).Result);

        // The list shows normal groups with room, with this player's applications marked.
        await bob.SendGameAsync(AdventureGroupService.NormalList, new PullAdventureGroupReq { Count = 200, ConditionJoinData = new ConditionJoin() });
        var listed = await Next(bob, AdventureGroupService.NormalList, PullAdventureGroupRes.Parser);
        Assert.DoesNotContain(listed.Data, g => g.Uuid == full.Id);
        Assert.True(listed.Data.Single(g => g.Uuid == open[1].Id).IsApply);
        Assert.False(listed.Data.Single(g => g.Uuid == open[0].Id).IsApply);

        // An accepted application: the owner agrees, bob joins, his other applications are withdrawn.
        await using var alice = await InWorld("Alice");
        long group = await GmCreate(alice, "Bravo");
        Assert.Equal(0, (await Ope(bob, OpeType.OpeApply, new NormalOpeData { AdventureId = group })).Result);
        await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangeApplyAdd && n.ApplyData.PlayerID == b);
        Assert.Equal(0, (await Ope(alice, OpeType.OpeAgreeApply, new NormalOpeData { InvitedPlayerId = b, IsAgree = true })).Result);
        var joined = await Full(bob, n => n.Data?.Uuid == group);
        Assert.Equal(2, joined.Data.MemberData.Count);
        await Until(() => Store.List().All(g => !g.Applications.ContainsKey(b)), "applications withdrawn");
        Assert.Equal(AdventureGroupService.ErrJoined, (await Ope(bob, OpeType.OpeApply, new NormalOpeData { AdventureId = open[2].Id })).Result);
        var relation = await Next(bob, AdventureGroupService.RelationSync, DBAdventureGroupRelationDataIncrement.Parser,
            n => n.HasAdventureGroupId && n.AdventureGroupId == group);
        Assert.Equal(group, relation.AdventureGroupId);
    }

    [Fact]
    public async Task LeaveKickAndTransferFollowTheOwnerRules()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long a = Id(alice), b = Id(bob), c = Id(carol);
        long group = await GmCreate(alice, "Crew");
        await Gm(alice, "!advguild add Bob");
        await Full(bob, n => n.Data?.Uuid == group);
        await Gm(alice, "!advguild add Carol");
        await Full(carol, n => n.Data?.Uuid == group);

        // Edit: one field per request, the response echoes every field.
        await alice.SendGameAsync(AdventureGroupService.Edit, new AdventureGroupEditReq { Tp = EditType.EditName, Data = new editData { Name = "Crew2" } });
        var edited = await Next(alice, AdventureGroupService.Edit, AdventureGroupEditRes.Parser);
        Assert.Equal(0, edited.Result);
        Assert.Equal("Crew2", edited.Data.Name);
        Assert.Equal(1, edited.Data.Background);
        await Next(bob, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangeBaseData && n.ChangeBaseData.Name == "Crew2");
        await bob.SendGameAsync(AdventureGroupService.Edit, new AdventureGroupEditReq { Tp = EditType.EditName, Data = new editData { Name = "Mine" } });
        Assert.Equal(AdventureGroupService.ErrPermission, (await Next(bob, AdventureGroupService.Edit, AdventureGroupEditRes.Parser)).Result);
        Assert.Equal(0, (await Ope(carol, OpeType.OpeEditNickName, new NormalOpeData { Nickname = "Cee" })).Result);
        await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangePlayerChange && n.ApplyData.Nickname == "Cee");

        // Transfer: the owner hands over, then the cooldown (AdventureGroupChangeOwnerCD) holds the next one.
        Assert.Equal(AdventureGroupService.ErrPermission, (await Ope(bob, OpeType.OpeChangeOwner, new NormalOpeData { InvitedPlayerId = c })).Result);
        Assert.Equal(0, (await Ope(alice, OpeType.OpeChangeOwner, new NormalOpeData { InvitedPlayerId = b })).Result);
        var reordered = await Full(carol, n => n.Data?.Data?.Owner == b);
        Assert.Equal(b, reordered.Data.MemberData[0].PlayerID);
        Assert.Equal(AdventureGroupService.ErrOwnerCooldown, (await Ope(bob, OpeType.OpeChangeOwner, new NormalOpeData { InvitedPlayerId = a })).Result);
        Assert.Equal(b, Store.Get(group)!.Owner);

        // The owner of a group with members cannot leave; the owner kicks; a member leaves.
        Assert.Equal(AdventureGroupService.ErrPermission, (await Ope(bob, OpeType.OpeLeave, new NormalOpeData { AdventureId = group })).Result);
        Assert.Equal(AdventureGroupService.ErrPermission, (await Ope(alice, OpeType.OpeKick, new NormalOpeData { InvitedPlayerId = c })).Result);
        Assert.Equal(0, (await Ope(bob, OpeType.OpeKick, new NormalOpeData { InvitedPlayerId = c })).Result);
        await Full(carol, n => n.Data == null);
        await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangeMemberDel && n.ApplyData.PlayerID == c);
        Assert.Equal(0, (await Ope(alice, OpeType.OpeLeave, new NormalOpeData { AdventureId = group })).Result);
        await Full(alice, n => n.Data == null);
        var events = Store.Get(group)!.Events;
        Assert.Contains(events, e => e.Type == (int)AdventureGroupEventType.EventTypeKick && e.First == c && e.Second == b);
        Assert.Contains(events, e => e.Type == (int)AdventureGroupEventType.EventTypeLeave && e.First == a);
        Assert.Equal([b], Store.Get(group)!.Members.Keys);

        // Leaving starts the 30-minute cooldown (AdventureGroupLeavePunish): 10226 with the remaining time.
        var refused = await Ope(alice, OpeType.OpeApply, new NormalOpeData { AdventureId = group });
        Assert.Equal(AdventureGroupService.ErrCooldown, refused.Result);
        Assert.InRange(refused.PunishTime, 1700, 1800);
        _clock.Advance(1801);
        Assert.Equal(0, (await Ope(alice, OpeType.OpeApply, new NormalOpeData { AdventureId = group })).Result);
        // A kicked member has no cooldown (inferred).
        Assert.Equal(0, (await Ope(carol, OpeType.OpeApply, new NormalOpeData { AdventureId = group })).Result);

        // The last member's leave dissolves the group.
        Assert.Equal(0, (await Ope(bob, OpeType.OpeLeave, new NormalOpeData { AdventureId = group })).Result);
        Assert.Null(Store.Get(group));
    }

    [ClientDataFact]
    public async Task DailyWateringGrowsTheTreeAndItsQuestRaisesTheGroupLevel()
    {
        await using var alice = await InWorld("Alice");
        long a = Id(alice);
        // The group quests are To-Do (function 25) tracking quests; To-Do opens with story quest 10105090.
        await Gm(alice, "!quest done 10105090");
        await Until(() => Service<FunctionService>().IsOpen(Session(alice), 25), "To-Do open");
        long group = await GmCreate(alice, "Grove");
        int sapling = AdventureGroupService.SaplingNpcs[0];
        // The watering quest 2000000 (AdventureGroupTaskConfig 1000, daily activity 3200) is given to members.
        await Until(() => Session(alice).Tasks!.Infos.TryGetValue(2000000, out var t) && t.Status == TaskState.Running, "watering quest");

        // Away from the tree's map the watering is refused.
        Assert.Equal(AdventureGroupService.ErrWrongSapling, await Result(alice, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = sapling }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session(alice), Prontera)));
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Prontera);
        await Load(alice, Prontera);
        Assert.Equal(AdventureGroupService.ErrWrongSapling, await Result(alice, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = sapling + 1 }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        await alice.SendGameAsync(AdventureGroupService.Water, new WateringSaplingReq { SaplingId = sapling });
        var watered = await Next(alice, AdventureGroupService.Water, WateringSaplingRes.Parser);
        Assert.Equal(0, watered.Result);
        Assert.Equal(1, watered.UpdateBaseData.WateringCount);
        await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangeBaseData && n.ChangeBaseData.SaplingData.WateringCount == 1);
        Assert.Equal(AdventureGroupService.ErrWatered, await Result(alice, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = sapling }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));

        // The quest delivers its award (191100: 50 Adventure Bond Points); the points are group EXP: 50 reaches level 2.
        await Until(() => TaskService.IsDelivered(Session(alice), 2000000), "watering quest delivered");
        await Until(() => Store.Get(group) is { Money: 50, Level: 2, WeekMoney: 50 }, "group exp and level");
        var g = Store.Get(group)!;
        Assert.Contains(g.Events, e => e.Type == (int)AdventureGroupEventType.EventTypeWatering && e.First == a);
        Assert.Contains(g.Events, e => e.Type == (int)AdventureGroupEventType.EventTypeLevelUp && e.First == 2);
        await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            n => n.Tp == ChangeType.ChangeBaseData && n.ChangeBaseData.Level == 2 && n.ChangeBaseData.ResourceData.AdventGroupMoney == 50);

        // The sapling list of that tree shows the group.
        await alice.SendGameAsync(AdventureGroupService.SaplingData, new GetSaplingDataReq { SaplingId = sapling });
        var tree = await Next(alice, AdventureGroupService.SaplingData, GetSaplingDataRes.Parser);
        Assert.Equal(1, Assert.Single(tree.SaplingDataList, x => x.AdventureId == group).WateringCount);

        // Next day (05:00 boundary) the member may water again.
        _clock.Advance(24 * 3600);
        Assert.Equal(0, await Result(alice, AdventureGroupService.Water,
            new WateringSaplingReq { SaplingId = sapling }, body => WateringSaplingRes.Parser.ParseFrom(body).Result));
        Assert.Equal(2, Store.Get(group)!.SaplingWatering);

        // The weekly cap (AdventureGroupMoneyWeeklyLimit 1000) bounds what quests add.
        long gained = 0;
        await OnLoop(async () =>
        {
            using var scope = await Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().EnterAsync("test/advguild-exp",
                Ro3Emu.Server.Game.Ownership.ReservationKey.Realm);
            gained = await Service<AdventureGroupService>().AddExpAsync(a, 5000);
        });
        Assert.Equal(950, gained);
        Assert.Equal(5, Store.Get(group)!.Level);
    }

    [ClientDataFact]
    public async Task ALevelRewardIsClaimedOncePerRole()
    {
        await using var alice = await InWorld("Alice");
        long a = Id(alice);
        long group = await GmCreate(alice, "Prize");
        Assert.Equal(AdventureGroupService.ErrLevel, (await Ope(alice, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 2 })).Result);
        long coins = Session(alice).Progress!.Currency.GetValueOrDefault(2);
        Assert.Equal(0, (await Ope(alice, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 1 })).Result);
        var taken = await Next(alice, AdventureGroupService.RelationSync, DBAdventureGroupRelationDataIncrement.Parser,
            n => n.TakenGroupRewardLevel?.All?.Value.Contains(1) == true);
        Assert.NotNull(taken);
        await Until(() => Session(alice).Progress!.Currency.GetValueOrDefault(2) == coins + 200000, "level 1 reward (200000 coins)");
        Assert.Equal(AdventureGroupService.ErrClaimed, (await Ope(alice, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 1 })).Result);
        Assert.Equal(1, OwnedSourceCount($"advguild-level/{a}/1", "cd13e/advguild-award"));

        await Gm(alice, "!advguild exp 60");
        await Until(() => Store.Get(group)!.Level == 2, "level 2");
        Assert.Equal(0, (await Ope(alice, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 2 })).Result);
        Assert.Equal(new[] { 1, 2 }, Session(alice).AdventureGroupRole!.TakenGroupRewardLevel.Order());

        // The claim belongs to the role: in another group the same level is not paid twice.
        Assert.Equal(0, (await Ope(alice, OpeType.OpeLeave, new NormalOpeData { AdventureId = group })).Result);
        _clock.Advance(1801);
        await GmCreate(alice, "Again");
        Assert.Equal(AdventureGroupService.ErrClaimed, (await Ope(alice, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 1 })).Result);
    }

    [ClientDataFact]
    public async Task TheGroupAndTheRolesClaimsSurviveARestart()
    {
        long group, a;
        await using (var alice = await InWorld("Alice"))
        await using (var bob = await InWorld("Bob"))
        {
            a = Id(alice);
            group = await GmCreate(alice, "Keep");
            await Gm(alice, "!advguild add Bob");
            await Full(bob, n => n.Data?.Uuid == group);
            Assert.Equal(0, (await Ope(bob, OpeType.OpeEditNickName, new NormalOpeData { Nickname = "Bee" })).Result);
            Assert.Equal(0, (await Ope(alice, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 1 })).Result);
            Assert.Equal(0, (await Ope(alice, OpeType.OpeChangeRecruit, new NormalOpeData { IsRecruit = true })).Result);
        }
        await RestartAsync();
        await using var again = await InWorld("Alice", create: false);
        var login = await Full(again, n => n.Data?.Uuid == group);
        Assert.Equal("Keep", login.Data.Data.Name);
        Assert.Equal(a, login.Data.Data.Owner);
        Assert.NotEqual(0, login.Data.Data.RecruitTime);
        Assert.Equal("Bee", login.Data.MemberData.Single(m => m.UserName == "Bob").Nickname);
        var relation = await Next(again, AdventureGroupService.RelationSync, DBAdventureGroupRelationDataIncrement.Parser, n => n.SyncAllMark);
        Assert.Equal(group, relation.AdventureGroupId);
        Assert.Contains(1, relation.TakenGroupRewardLevel.All.Value);
        Assert.Equal(AdventureGroupService.ErrClaimed, (await Ope(again, OpeType.OpeTakenLevelUpReward, new NormalOpeData { LevelReward = 1 })).Result);
    }

    [ClientDataFact]
    public async Task ARallyCallsMembersToTheCaller()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long group = await GmCreate(alice, "Rally");
        await Gm(alice, "!advguild add Bob");
        await Full(bob, n => n.Data?.Uuid == group);
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session(alice), Prontera)));
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Prontera);
        await Load(alice, Prontera);

        await alice.SendGameAsync(AdventureGroupService.Convene, new AdventureGroupConveneReq());
        var called = await Next(alice, AdventureGroupService.Convene, AdventureGroupConveneRes.Parser);
        Assert.Equal(0, called.Result);
        Assert.Equal(1, called.UseConveneTimes);
        var notice = await Next(bob, AdventureGroupService.ConveneNotify, AdventureGroupConveneNtf.Parser);
        Assert.Equal(Id(alice), notice.PlayerId);
        // Level 1 allows one rally a day (AdventureGroupLevelAwardConfig._iConveneNum), level 2 two, AdventureGroupConveneCD apart.
        Assert.Equal(AdventureGroupService.ErrNoRally, await Result(alice, AdventureGroupService.Convene,
            new AdventureGroupConveneReq(), body => AdventureGroupConveneRes.Parser.ParseFrom(body).Result));
        await Gm(alice, "!advguild exp 60");
        await Until(() => Store.Get(group)!.Level == 2, "level 2");
        Assert.Equal(AdventureGroupService.ErrRallyCooldown, await Result(alice, AdventureGroupService.Convene,
            new AdventureGroupConveneReq(), body => AdventureGroupConveneRes.Parser.ParseFrom(body).Result));
        _clock.Advance(11);
        Assert.Equal(0, await Result(alice, AdventureGroupService.Convene,
            new AdventureGroupConveneReq(), body => AdventureGroupConveneRes.Parser.ParseFrom(body).Result));
        await Next(bob, AdventureGroupService.ConveneNotify, AdventureGroupConveneNtf.Parser, n => n.Token != notice.Token);
        Assert.Equal(AdventureGroupService.ErrNoInvite, await Result(bob, AdventureGroupService.ConfirmConvene,
            new AdventureGroupConfirmConveneReq { Token = notice.Token + 1, IsAgree = true },
            body => AdventureGroupConfirmConveneRes.Parser.ParseFrom(body).Result));
        Assert.Equal(0, await Result(bob, AdventureGroupService.ConfirmConvene,
            new AdventureGroupConfirmConveneReq { Token = notice.Token, IsAgree = true },
            body => AdventureGroupConfirmConveneRes.Parser.ParseFrom(body).Result));
        await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Prontera);
        // The rally count resets with the day.
        _clock.Advance(24 * 3600);
        Assert.Equal(0, await Result(alice, AdventureGroupService.Convene,
            new AdventureGroupConveneReq(), body => AdventureGroupConveneRes.Parser.ParseFrom(body).Result));
    }

    private long OwnedSourceCount(string source, string kind)
    {
        using var db = Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key=$source AND r.command_kind=$kind
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }
}
