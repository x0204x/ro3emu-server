using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Ordinary dungeon wire and combat contract. The exported 50101/mark 1 team boss is real;
/// tests explicitly labelled synthetic change only this host's cached minimum party size to exercise the same
/// engine solo. The export does not prove a solo clear-with-waves-and-reward encounter.
/// </summary>
public sealed partial class DungeonTests : IAsyncLifetime
{
    private const int Multi = 50101, Boss = 4300001;
    private const long Scene = 3120401;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-dungeons-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(2)));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.SeasonStart = "2026-10-08";
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, _clock);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
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

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private static EnterDungeonReq Request(int multi = Multi, int mark = 1, long scene = Scene,
        long teamId = 0, long runId = 0) => new()
        {
            SceneEntry = scene,
            InitData = new DungeonInitData
            {
                MultiDungeonInitData = new MultiDungeonInitData
                { MultiDungeonId = multi, MarkId = mark, TeamID = teamId, DungeonId = runId }
            },
        };

    private static async Task<int> Enter(BotClient bot, EnterDungeonReq? req = null)
    {
        await bot.SendGameAsync(DungeonService.Enter, req ?? Request());
        return (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result;
    }

    private async Task<long> Party(BotClient leader, params BotClient[] others)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in others)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            Assert.Equal((long)leader.Self!.Uuid,
                (await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser)).InviteGuid);
            await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
            { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self.Uuid });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
        return created.TeamId;
    }

    private static bool HasSwitch(BotClient bot) => bot.Inbox.Snapshot().Any(e => e.IsGame(MsgId.SwitchSceneLine)
        && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == Scene);

    private static async Task<long> Load(BotClient bot, long scene = Scene)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == scene);
        Assert.NotEqual(sw.SceneEntry, sw.SceneID);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == scene)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        var info = await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        Assert.Equal(scene, info.DungeonEntry);
        if (scene == Scene) Assert.Equal(sw.SceneID, info.InitData.MultiDungeonInitData.DungeonId);
        return sw.SceneID;
    }

    private async Task<MonsterUnit> BossIn(long instance, int npc = Boss, long exclude = 0)
    {
        for (int i = 0; i < 100; i++)
        {
            var actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().SingleOrDefault(m => m.InstanceId == instance && m.Template.NpcId == npc && m.Uid != exclude)));
            if (actor != null) return actor;
            await Task.Delay(20);
        }
        throw new TimeoutException("The configured team boss did not spawn");
    }

    private async Task Kill(BotClient bot, MonsterUnit actor)
    {
        await OnLoop(async () =>
        {
            Assert.True(actor.Hp > 1);
            // This helper verifies dungeon settlement, not travel time. Place the role beside
            // the live actor before Engage, so the boss never chases a stale spawn position
            // while the network Walk/Cast frames are still in transit.
            await Service<SceneService>().TeleportInMapAsync(Session(bot),
                new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
            // A revive can leave this actor returning home. Engage through the owner before the
            // controlled HP setup, so the next return-completion tick cannot restore full health.
            Service<MonsterService>().Engage(actor, Session(bot));
            actor.Hp = 1;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        try
        {
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                hit => hit.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead));
        }
        catch (TimeoutException error)
        {
            string state = await Service<GameLoop>().InvokeAsync(() =>
            {
                var s = Session(bot);
                return Task.FromResult($"player dead={s.Dead} hp={s.CurrentHp} loading={s.SceneLoading} scene={s.WorldScene} "
                    + $"attack={s.Attack?.Target.Uid} distance={MonsterService.Distance(actor, s)}; "
                    + $"monster uid={actor.Uid} hp={actor.Hp} dead={actor.Dead} scene={actor.WorldScene}");
            });
            var hits = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
                .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
                .Where(hit => hit.SrcID == actor.Uid || hit.Dsts.Any(d => d.DstID == actor.Uid)).TakeLast(5);
            throw new TimeoutException($"Dungeon fixture did not observe the required lethal hit: {state}; hits={string.Join("; ", hits)}", error);
        }
    }

    private long Items(BotClient bot, int entry) => Service<ItemStore>().Load((long)bot.Self!.Uuid)
        .Where(i => i.Entry == entry).Sum(i => (long)i.Stack);
    private long Currency(BotClient bot, int entry) => Session(bot).Progress!.Currency.GetValueOrDefault(entry);

    private void ChangeRow(string tableName, string key, Action<JsonObject> change)
    {
        var table = (Dictionary<string, JsonElement>)Service<GameData>().Table(tableName);
        var row = JsonNode.Parse(table[key].GetRawText())!.AsObject();
        change(row);
        table[key] = JsonSerializer.SerializeToElement(row);
    }

    private void SyntheticSolo(bool daily = false)
    {
        ChangeRow("TenGameplayConfig", "50101", row => row["_kLimit"] = new JsonArray(1, 1));
        if (daily) ChangeRow("TenGameplayRefreshTimeConfig", "50101", row =>
        {
            row["_iRewardNumRefreshType"] = 1;
            row["_iRewardNumRefreshParam"] = new JsonArray(5);
        });
    }

    [ClientDataFact]
    public async Task AuthenticThreePlayerBossRequiresReadyAndPaysEachClearOnce()
    {
        var plan = Service<DungeonCatalog>().Resolve(Request());
        Assert.NotNull(plan);
        Assert.Equal((3, 5, Boss, Scene),
            (plan.MinPlayers, plan.MaxPlayers, (int)GameData.Int(plan.Boss!.Value, "_iBoss"), plan.SceneEntry));
        Assert.Null(Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = 1054001 })); // no encounter source
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await Gm(alice, "!lvl 35");
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Enter(alice));
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        long team = await Party(alice, bob);
        Assert.Equal(ErrorCodes.DungeonTeamSize, await Enter(alice));
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)carol.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(carol, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await carol.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = team, IsAgree = true, InviteGuid = (long)alice.Self!.Uuid });
        Assert.Equal(0, (await Next(carol, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);

        Assert.Equal(ErrorCodes.DungeonDataMissing, await Enter(alice, Request(scene: Scene + 1)));
        Assert.Equal(ErrorCodes.DungeonDataMissing, await Enter(alice, Request(multi: 50102)));
        Assert.Equal(ErrorCodes.DungeonDataMissing, await Enter(alice, Request(mark: 99)));
        Assert.Equal(ErrorCodes.Parameter, await Enter(alice, Request(runId: 123)));
        Assert.Equal(ErrorCodes.Parameter, await Enter(alice, Request(teamId: team + 1)));

        Assert.Equal(0, await Enter(alice));
        var ready = await Next(bob, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser,
            n => !n.Cancel && n.ReadyQueue.Count == 3);
        Assert.True(ready.ReadyQueue[(long)alice.Self.Uuid]);
        Assert.False(ready.ReadyQueue[(long)bob.Self!.Uuid]);
        await alice.SendGameAsync(DungeonService.ConfirmReminder, new TeamConfirmRemindersReq
        { PlayerId = (long)alice.Self.Uuid, RemindersGuid = (long)bob.Self.Uuid });
        Assert.Equal(0, (await Next(alice, DungeonService.ConfirmReminder, TeamConfirmRemindersResp.Parser)).Result);
        Assert.Equal((long)alice.Self.Uuid,
            (await Next(bob, DungeonService.ConfirmReminderNotify, TeamConfirmRemindersNotify.Parser)).MemberGuid);
        await bob.SendGameAsync(DungeonService.ConfirmReminder, new TeamConfirmRemindersReq
        { PlayerId = (long)bob.Self.Uuid, RemindersGuid = (long)carol.Self!.Uuid });
        Assert.Equal(ErrorCodes.NoPermission,
            (await Next(bob, DungeonService.ConfirmReminder, TeamConfirmRemindersResp.Parser)).Result);
        Assert.False(HasSwitch(alice));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        Assert.False(HasSwitch(alice));
        await carol.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(carol, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        long instance = await Load(alice);
        Assert.Equal(0, (await Next(alice, DungeonService.CreateResult, CreateDungeonResultNotify.Parser)).Result);
        var entryNotice = await Next(alice, DungeonService.EnterSceneResult, ToSceneEnterDungeonResult.Parser);
        Assert.Equal(Scene, entryNotice.SceneId);
        Assert.Equal(instance, entryNotice.DungeonId);
        Assert.Equal(instance, await Load(bob));
        Assert.Null(Service<World>().AllUnits().OfType<MonsterUnit>()
            .SingleOrDefault(m => m.InstanceId == instance && m.Template.NpcId == Boss));
        Assert.Equal(instance, await Load(carol));
        Assert.Equal(instance, Session(alice).InstanceId);
        Assert.Equal(instance, Session(bob).InstanceId);
        Assert.Equal(instance, Session(carol).InstanceId);
        var actor = await BossIn(instance);
        Assert.Equal(Boss, actor.Template.NpcId);
        Assert.Equal(2, GameData.Int(plan.Config, "_iReviveTrigger")); // exported NoRevive policy
        await OnLoop(async () =>
        {
            var dead = Session(bob);
            dead.Dead = true; // controlled death fixture through the same registered owner callbacks
            foreach (var handler in Service<CombatService>().PlayerDied) await handler(dead);
        });
        var noRevive = await Next(bob, DungeonService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, info => info.BDead);
        Assert.Equal(0, noRevive.ReviveTime);
        Assert.Equal(ErrorCodes.FeatureUnavailable, Service<DungeonService>().ExternalReviveError(Session(bob)));
        await bob.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bob, CombatService.Relive, EntityReliveAck.Parser)).Result);
        await OnLoop(() => { Session(bob).Dead = false; return Task.CompletedTask; });

        // A client-authored victory packet cannot settle or grant this run.
        await alice.SendGameAsync(DungeonService.Result,
            new DungeonResNotify { DungeonEntry = Scene, Res = (DungeonRes)1 });
        await alice.SendGameAsync(DungeonService.StatisticsCurrent, new GetDungeonStatisticsInDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.StatisticsCurrent,
            GetDungeonStatisticsInDungeonResp.Parser)).Result);
        Assert.Empty(Session(alice).Dungeons!.Records);
        Assert.Equal(0, Items(alice, 1250900));
        await Kill(alice, actor);
        foreach (var bot in new[] { alice, bob, carol })
        {
            var result = await Next(bot, DungeonService.Result, DungeonResNotify.Parser);
            Assert.Equal((DungeonRes)1, result.Res);
            Assert.Equal(90000, result.Rewards[2]);
            Assert.Equal(100, result.Rewards[1250900]);
            Assert.True(result.Rewards.Count >= 2); // random pool 73131 is variable, never asserted exactly
            Assert.Equal(90000, Currency(bot, 2));
            Assert.Equal(100, Items(bot, 1250900));
            await Next(bot, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser);
        }
        await alice.SendGameAsync(DungeonService.StatisticsHistory,
            new DungeonStatisticsInfoWithDungeonReq { StaisticsIds = { instance } });
        var history = await Next(alice, DungeonService.StatisticsHistory, DungeonStatisticsInfoWithDungeonResp.Parser);
        Assert.Equal(0, history.Result);
        Assert.Equal(3, Assert.Single(history.DungeonStatistics).SceneStatistics[instance].RoleStatistics.Count);
        await alice.SendGameAsync(DungeonService.QueryResult,
            new QueryDungeonResultReq { ResultIds = { instance.ToString() } });
        Assert.Equal(instance.ToString(), Assert.Single((await Next(alice, DungeonService.QueryResult,
            QueryDungeonResultResp.Parser)).Results).ResultId);
        await using (var outsider = await InWorld("Outsider"))
        {
            await outsider.SendGameAsync(DungeonService.StatisticsHistory,
                new DungeonStatisticsInfoWithDungeonReq { StaisticsIds = { instance } });
            var denied = await Next(outsider, DungeonService.StatisticsHistory,
                DungeonStatisticsInfoWithDungeonResp.Parser);
            Assert.Equal(ErrorCodes.NoPermission, denied.Result);
            Assert.Empty(denied.DungeonStatistics);
            await outsider.SendGameAsync(DungeonService.QueryResult,
                new QueryDungeonResultReq { ResultIds = { instance.ToString() } });
            Assert.Empty((await Next(outsider, DungeonService.QueryResult, QueryDungeonResultResp.Parser)).Results);
        }

        foreach (var bot in new[] { alice, bob, carol })
        {
            await bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(bot, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == RoleStore.SpawnSceneEntry);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == sw.SceneEntry);
            Assert.Equal(bot == carol, Service<InstanceService>().Find(instance) == null);
        }
        Assert.DoesNotContain(Service<World>().AllUnits(), m => m.InstanceId == instance);
    }

    [ClientDataFact]
    public async Task ReadyTimeoutDeclineAndRosterChangeCancelWithoutTeleport()
    {
        ((Dictionary<string, JsonElement>)Service<GameData>().Table("PublicParamConfig"))
            ["GetInPositionMaxTime"] = JsonSerializer.SerializeToElement(1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        await Party(alice, bob, carol);
        Assert.Equal(0, await Enter(alice));
        await Next(alice, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser, n => n.Cancel);
        Assert.False(HasSwitch(alice));
        Assert.Equal(0, await Enter(alice));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = false });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        var declined = await Next(alice, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser,
            n => n.Cancel && n.CancelRoleId == (long)bob.Self!.Uuid);
        Assert.Equal(ErrorCodes.DungeonDeclined, declined.CancelErrorCode);
        Assert.Equal(0, await Enter(alice));
        await carol.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(carol, TeamService.TeamLeave, LeaveTeamResp.Parser)).Result);
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(ErrorCodes.MemberLeft,
            (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        Assert.True((await Next(alice, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser,
            n => n.Cancel && n.CancelErrorCode == ErrorCodes.MemberLeft)).Cancel);
        Assert.False(HasSwitch(alice));
    }

    [ClientDataFact]
    public async Task ReadyConfirmationRevalidatesSeasonBeforeCreatingRoom()
    {
        // The cached team row gets the same season restriction as the exported raid rows, so three bots suffice.
        ChangeRow("TenGameplayConfig", "50101", row => row["_kMultiverseArray"] = new JsonArray(101));
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        await Party(alice, bob, carol);
        Assert.Equal(0, await Enter(alice));
        await Next(bob, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser, n => !n.Cancel);
        await OnLoop(() => { Service<SeasonService>().SetPlayBook(102); return Task.CompletedTask; });
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        foreach (var bot in new[] { alice, bob, carol })
        {
            await Next(bot, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser,
                n => n.Cancel && n.CancelErrorCode == ErrorCodes.FeatureUnavailable);
            Assert.False(HasSwitch(bot));
            Assert.Equal(0, Session(bot).InstanceId);
            Assert.Null(Session(bot).DungeonAttempt);
        }
        await OnLoop(() => { Service<SeasonService>().SetPlayBook(101); return Task.CompletedTask; });
        Assert.Equal(0, await Enter(alice)); // cancellation released every pending membership
    }

    [ClientDataFact]
    public async Task SyntheticSoloDisconnectDuringTransferRestoresPublicWorld()
    {
        SyntheticSolo(); // exported 50101 requires three; only this host's cached admission is relaxed
        long role, instance;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Gm(alice, "!lvl 99");
            Assert.Equal(0, await Enter(alice));
            instance = (await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
                m => m.SceneEntry == Scene)).SceneID;
            Assert.NotNull(Service<InstanceService>().Find(instance));
        }
        for (int i = 0; i < 100 && Service<SessionRegistry>().All().Any(s => s.Role?.Id == role); i++)
            await Task.Delay(20);
        Assert.DoesNotContain(Service<SessionRegistry>().All(), s => s.Role?.Id == role);
        await using var relog = await InWorld("Alice", create: false);
        Assert.Equal(RoleStore.SpawnSceneEntry, Session(relog).SceneEntry);
        Assert.Equal(0, Session(relog).InstanceId);
        Assert.Null(Session(relog).DungeonAttempt);
        Assert.Equal(0, Items(relog, 1250900));
        Assert.Null(Service<InstanceService>().Find(instance));
        Assert.DoesNotContain(Service<World>().AllUnits(), m => m.InstanceId == instance);
    }

    [ClientDataFact]
    public async Task ExportedPointRespawnRunsSoloAndRegionGroupResolvesConfiguredMonsters()
    {
        const long pointScene = 1022001, regionScene = 4400001;
        var taggedBoss = Service<DungeonCatalog>().Resolve(Request(100101, 1, 3020101));
        Assert.NotNull(taggedBoss);
        var bossLocation = Assert.Single(Assert.Single(taggedBoss.Waves).Spawns);
        Assert.Equal(14720026, bossLocation.NpcId);
        Assert.Equal((100f, 100f), (bossLocation.Point.X, bossLocation.Point.Z)); // editor point 93, named BOSS
        var pointPlan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = pointScene });
        Assert.NotNull(pointPlan);
        var point = Assert.Single(Assert.Single(pointPlan.Waves).Spawns);
        Assert.Equal(5300001, point.NpcId);
        Assert.Equal((271.16f, 138.13f), (point.Point.X, point.Point.Z));

        var regionPlan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = regionScene });
        Assert.NotNull(regionPlan);
        var region = Assert.Single(regionPlan.Waves);
        Assert.Equal(50, region.Spawns.Count);
        Assert.All(region.Spawns, spawn => Assert.Equal(4400001, spawn.NpcId));
        Assert.True(region.Spawns.Select(spawn => (spawn.Point.X, spawn.Point.Z)).Distinct().Count() > 20);

        await using var alice = await InWorld("Alice");
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Enter(alice, new EnterDungeonReq { SceneEntry = pointScene }));
        await Gm(alice, "!quest accept 10102200"); // exported condition 1000000 requires this task Running
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = pointScene }));
        long instance = await Load(alice, pointScene);
        var boss = await BossIn(instance, 5300001);
        Assert.Equal((271.16f, 138.13f), (boss.Home.X, boss.Home.Z));
        long reviveAt = _clock.GetUtcNow().ToUnixTimeSeconds() + 5;
        await OnLoop(async () =>
        {
            var dead = Session(alice);
            dead.Dead = true;
            foreach (var handler in Service<CombatService>().PlayerDied) await handler(dead);
        });
        var autoRevive = await Next(alice, DungeonService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser,
            info => info.BDead);
        Assert.Equal(reviveAt, autoRevive.ReviveTime);
        Assert.False(Session(alice).DungeonAttempt!.Settled); // a timed option prevents instant party-wipe settlement
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(0, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
        Assert.False(Session(alice).Dead);
        // The map resurrection point is 33m from this actor. Move the controlled fixture by the server's
        // authoritative in-map teleport so Kill's immediate wire attack does not trip walk-speed validation.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice),
            new MapPoint(boss.X + 1, boss.Y, boss.Z, boss.Face)));
        await Kill(alice, boss);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);

        await using var bob = await InWorld("Bob");
        Assert.Equal(0, await Enter(bob, new EnterDungeonReq { SceneEntry = regionScene }));
        long regionInstance = await Load(bob, regionScene);
        await OnLoop(() =>
        {
            var actors = Service<World>().AllUnits().OfType<MonsterUnit>()
                .Where(m => m.InstanceId == regionInstance).ToArray();
            Assert.Equal(50, actors.Length);
            Assert.All(actors, m => Assert.Equal(4400001, m.Template.NpcId));
            return Task.CompletedTask;
        });
        await bob.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bob, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task ConfiguredImmediateOnClickReviveAdmitsTheNormalRequest()
    {
        // Thirty-three other ordinary scenes export [[0]]; reuse a source-backed room with that exact option
        // host-locally so the request and settlement path can be exercised without inventing their monsters.
        ChangeRow("DungeonConfig", "1022001", row => row["_kTriggerParm"] = JsonNode.Parse("[[0]]"));
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1022001 }));
        await Load(alice, 1022001);
        await OnLoop(async () =>
        {
            var dead = Session(alice);
            dead.Dead = true;
            foreach (var handler in Service<CombatService>().PlayerDied) await handler(dead);
        });
        var revive = await Next(alice, DungeonService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser,
            info => info.BDead);
        Assert.Equal(0, revive.ReviveTime);
        Assert.False(Session(alice).DungeonAttempt!.Settled);
        var combat = Service<CombatService>();
        Assert.True(combat.TryClaimRevive(Session(alice)));
        try
        {
            await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
            Assert.Equal(ErrorCodes.FeatureUnavailable,
                (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
            Assert.True(Session(alice).Dead);
            Assert.Empty(Session(alice).DungeonAttempt!.RevivesUsed);
        }
        finally { combat.ReleaseReviveClaim(Session(alice)); }
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(0, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
        Assert.False(Session(alice).Dead);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task SamePlaybookRolloverCannotPayAnOldDungeonAttempt()
    {
        SyntheticSolo();
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99"); // real multi-boss unlock remains in force for this host-local one-player fixture
        Assert.Equal(0, await Enter(alice));
        long instance = await Load(alice);
        var boss = await BossIn(instance);
        _clock.Advance(TimeSpan.FromDays(300));
        string advanced = Service<SeasonService>().AdvanceCycle(101, _clock.GetLocalNow().Date);
        Assert.StartsWith("Season 2", advanced);
        Assert.Equal(1, Session(alice).SeasonState!.SeasonId); // old login remains on its captured cycle
        await Kill(alice, boss);
        Assert.Equal((DungeonRes)2, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.Equal(0, Items(alice, 1250900));
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Enter(alice));
    }

    [ClientDataFact]
    public async Task SyntheticStoryEditorRunsTwoPrivateWavesButBareExportHasNoServerEncounter()
    {
        // 1054001 (an Ant Hell story scene) has neither a resolved spawn source nor a reconstruction. (1000201, used
        // here before, is now a reconstructed scripted scene: DungeonTests.Scripted.)
        const long story = 1054001;
        var bare = new EnterDungeonReq { SceneEntry = story };
        Assert.Null(Service<DungeonCatalog>().Resolve(bare));
        await using var alice = await InWorld("Alice");
        Assert.Equal(ErrorCodes.DungeonDataMissing, await Enter(alice, bare));

        // This private copy adds two explicit server-owned points. The exported editor has no server point, so
        // these waves are a synthetic engine fixture, not recovered content.
        var born = Service<WorldData>().Map(story)!.Born!.Value;
        string original = Service<GameData>().Row("DungeonConfig", story)!.Value
            .GetProperty("_kResSceneEditorName").GetString()!;
        string source = Path.Combine(RepoRoot(), "data", "sceneeditor", original + ".json");
        string directory = Path.Combine(_dataDir, "sceneeditor");
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, "synthetic_1054001");
        var editor = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        JsonObject Point(int id, int group, int dz) => new()
        {
            ["Id"] = id, ["GroupId"] = group, ["monsterType"] = 0, ["npcId"] = 5300036,
            ["direction"] = 0, ["position"] = new JsonObject { ["x"] = born.X, ["y"] = born.Y, ["z"] = born.Z + dz },
        };
        editor["m_kMonsterSpawnPoints"] = new JsonObject { ["1001"] = Point(1001, 1, 3), ["1002"] = Point(1002, 2, 6) };
        editor["m_kGroups"] = new JsonObject();
        File.WriteAllText(stem + ".json", editor.ToJsonString());
        ChangeRow("DungeonConfig", story.ToString(), row => row["_kResSceneEditorName"] = stem);
        var plan = Service<DungeonCatalog>().Resolve(bare);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Waves.Count);
        Assert.All(plan.Waves, wave => Assert.Equal(5300036, Assert.Single(wave.Spawns).NpcId));
        Assert.Equal(0, await Enter(alice, bare));
        long instance = await Load(alice, story);
        var first = await BossIn(instance, 5300036);
        Assert.Equal(1, Session(alice).DungeonAttempt!.Wave + 1);
        await Kill(alice, first);
        var second = await BossIn(instance, 5300036, first.Uid);
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1);
        Assert.False(Session(alice).DungeonAttempt!.Settled); // one wave cannot settle the room
        await Kill(alice, second);
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        Assert.False(result.Rewards.ContainsKey(1250900)); // story NPC/task reward mapping was not recovered
        Assert.DoesNotContain(Service<World>().AllUnits(), m => m.InstanceId == instance && m is MonsterUnit);
    }

    [ClientDataFact]
    public async Task SyntheticSoloCombatChecksDailyAllowanceCarryAndRollback()
    {
        static bool ClearPublished(DBMultiDungeonInfoIncrement update, int used)
            => update.DungeonInfo.TryGetValue(Multi, out var dungeon) &&
                (dungeon.All?.AwardInfo.FirstOrDefault() is { IsFirst: 2 } full && full.UseNumber == used ||
                 dungeon.Inc?.AwardInfo?.All?.Value.FirstOrDefault() is { IsFirst: 2 } all && all.UseNumber == used ||
                 dungeon.Inc?.AwardInfo?.Inc?.Value.FirstOrDefault() is { } counter &&
                    (used == 1 ? counter.HasIsFirst && counter.IsFirst == 2 : counter.HasUseNumber && counter.UseNumber == used));
        SyntheticSolo(daily: true); // test-only admission and reset fixture; original team minimum is three
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        long first = await Load(alice);
        await Kill(alice, await BossIn(first));
        Assert.Equal((DungeonRes)1,
            (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        // Settlement publishes 30403 before its 5062 sync, which may await owner admission.
        await Next(alice, DungeonService.Sync, DBMultiDungeonInfoIncrement.Parser, update => ClearPublished(update, 1));
        Assert.Equal(90000, Currency(alice, 2));
        Assert.Equal(100, Items(alice, 1250900));
        Assert.Equal(1, Session(alice).Dungeons!.Allowances["50101:101:1"].Remaining);
        var firstCounter = ((DBMultiDungeonInfo)Session(alice).Synced[DungeonService.Sync]).DungeonInfo[Multi].AwardInfo[0];
        Assert.Equal(2, firstCounter.TotalNumber);
        Assert.Equal(1, firstCounter.UseNumber);
        Assert.Equal(2, firstCounter.IsFirst);

        // The exported panel adds a free first clear. The next clear spends the one recurring charge.
        await alice.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "DungeonReset" });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await Next(alice, DungeonService.ClientEvent, NullArg.Parser);
        long second = await Load(alice);
        Assert.NotEqual(first, second);
        await Kill(alice, await BossIn(second));
        var secondResult = await Next(alice, DungeonService.Result, DungeonResNotify.Parser, r => r.DungeonEntry == Scene);
        await Next(alice, DungeonService.Sync, DBMultiDungeonInfoIncrement.Parser, update => ClearPublished(update, 2));
        Assert.Equal((DungeonRes)1, secondResult.Res);
        Assert.Equal(90000, secondResult.Rewards[2]);
        Assert.Equal(100, secondResult.Rewards[1250900]);
        Assert.Equal(180000, Currency(alice, 2));
        Assert.Equal(200, Items(alice, 1250900));
        Assert.Equal(0, Session(alice).Dungeons!.Allowances["50101:101:1"].Remaining);
        var spentCounter = ((DBMultiDungeonInfo)Session(alice).Synced[DungeonService.Sync]).DungeonInfo[Multi].AwardInfo[0];
        Assert.Equal(2, spentCounter.UseNumber);

        // Entry is not capped by reward allowance: a third win has no personal award.
        Assert.Equal(0, await Enter(alice));
        long third = await Load(alice);
        await Kill(alice, await BossIn(third));
        var thirdResult = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, thirdResult.Res);
        Assert.False(thirdResult.Rewards.ContainsKey(2));
        Assert.False(thirdResult.Rewards.ContainsKey(1250900));
        Assert.Equal(180000, Currency(alice, 2));
        Assert.Equal(200, Items(alice, 1250900));

        _clock.Advance(TimeSpan.FromDays(1));
        await OnLoop(() => Service<DungeonService>().SyncAsync(Session(alice)));
        Assert.Equal(1, Session(alice).Dungeons!.Allowances["50101:101:1"].Remaining);
        _clock.Advance(TimeSpan.FromDays(10));
        await OnLoop(() => Service<DungeonService>().SyncAsync(Session(alice)));
        Assert.Equal(2, Session(alice).Dungeons!.Allowances["50101:101:1"].Remaining);
        var saved = JsonSerializer.Deserialize<DungeonService.State>(Service<RoleStore>().LoadDungeons((long)alice.Self!.Uuid)!)!;
        Assert.Equal(2, saved.Allowances["50101:101:1"].Remaining);

        // A storage failure rolls back both the earned item/currency and the consumed allowance.
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER fail_dungeons BEFORE UPDATE OF dungeons ON role BEGIN SELECT RAISE(ABORT, 'dungeon test failure'); END");
        long fourth;
        long roleId = (long)alice.Self!.Uuid;
        string source;
        try
        {
            int priorResults = alice.Inbox.Snapshot().Count(packet => packet.IsGame(DungeonService.Result));
            Assert.Equal(0, await Enter(alice));
            fourth = await Load(alice);
            var fourthBoss = await BossIn(fourth);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<GameSession, MonsterUnit, Task> observeSettlement = (_, monster) =>
            {
                if (ReferenceEquals(monster, fourthBoss)) settled.TrySetResult();
                return Task.CompletedTask;
            };
            await OnLoop(() => { Service<CombatService>().Killed.Add(observeSettlement); return Task.CompletedTask; });
            try
            {
                await Kill(alice, fourthBoss);
                // SkillHit precedes Killed. Observe the original dungeon callback's completion,
                // so a manual retry cannot race it or expose its synchronous staging snapshot.
                await settled.Task.WaitAsync(Wait);
            }
            finally
            {
                await OnLoop(() => { Service<CombatService>().Killed.Remove(observeSettlement); return Task.CompletedTask; });
            }
            // A repeated failed delivery must keep the same entitlement and publish no result.
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(alice))));
            Assert.Equal(priorResults, alice.Inbox.Snapshot().Count(packet => packet.IsGame(DungeonService.Result)));
            Assert.True(Service<DungeonObligationStore>().IsActivated(fourth));
            var pending = Assert.Single(Service<DungeonObligationStore>().Pending(roleId));
            Assert.Equal(fourth, pending.RoomId);
            Assert.True(pending.Success);
            Assert.Contains(pending.Goods, good => good.Entry == 1250900 && good.Count == 100);
            Assert.Contains(pending.Goods, good => good.Entry == 2 && good.Count == 90000);
            source = pending.Key;
            Assert.False(await Service<CharacterService>().RewardSourceCommittedAsync(source));
            Assert.Equal(180000, Currency(alice, 2));
            Assert.Equal(180000, Service<RoleStore>().LoadProgress(roleId).Currency.GetValueOrDefault(2));
            Assert.Equal(200, Items(alice, 1250900));
            Assert.Equal(2, Session(alice).Dungeons!.Allowances["50101:101:1"].Remaining);
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_dungeons"); }
        await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(alice)));
        Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(source));
        Assert.Empty(Service<DungeonObligationStore>().Pending(roleId));
        Assert.Equal(270000, Currency(alice, 2));
        Assert.Equal(300, Items(alice, 1250900));
        Assert.Equal(1, Session(alice).Dungeons!.Allowances["50101:101:1"].Remaining);
        var recovered = JsonSerializer.Deserialize<DungeonService.State>(Service<RoleStore>().LoadDungeons(roleId)!)!;
        Assert.Equal(1, recovered.Allowances["50101:101:1"].Remaining);
        await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(alice)));
        Assert.Equal(270000, Currency(alice, 2));
        Assert.Equal(300, Items(alice, 1250900));
    }

    [Fact]
    public void RewardPeriodBoundaryIsExplicitlyInferred()
    {
        using var weekly = JsonDocument.Parse("{\"_iRewardNumRefreshType\":2,\"_iRewardNumRefreshParam\":[1,5]}");
        using var daily = JsonDocument.Parse("{\"_iRewardNumRefreshType\":1,\"_iRewardNumRefreshParam\":[5]}");
        var mondayBefore = new DateTime(2026, 10, 5, 4, 59, 0);
        Assert.Equal(7, DungeonService.RewardPeriod(weekly.RootElement, mondayBefore).PeriodDays);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 9, 28)).DayNumber,
            DungeonService.RewardPeriod(weekly.RootElement, mondayBefore).Epoch);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 10, 5)).DayNumber,
            DungeonService.RewardPeriod(weekly.RootElement, mondayBefore.AddMinutes(1)).Epoch);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 10, 7)).DayNumber,
            DungeonService.RewardPeriod(daily.RootElement, new DateTime(2026, 10, 8, 4, 59, 0)).Epoch);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 10, 8)).DayNumber,
            DungeonService.RewardPeriod(daily.RootElement, new DateTime(2026, 10, 8, 5, 0, 0)).Epoch);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
