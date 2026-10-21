using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Batch F group content on the real exported data: the Realm of the Gods extras (hall of fame 82583, Sacred Blessings
/// 82588, Final Trial honors 82589, the raids' Start Battle button 30125), the Hidden Realm (82591) and MVP likes
/// (82585/82586). All content open, as the realm runs (Ro3Emu:OpenContent), so the raids' server-day gates are met.
/// </summary>
public sealed partial class GroupContentTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-group-" + Guid.NewGuid().ToString("N"));
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
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.SeasonStart = "2026-10-08";
            // These tests finish bosses at 1 HP; a boss opening its last phase there (BossSkills) could down the bot
            // first. Boss specials have their own tests.
            o.BossSkills.Enabled = false;
            o.OpenContent.Enabled = true;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, _clock);
        SureHitRolls.Install(_app);
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
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

    private static EnterDungeonReq Request(int multi, int mark, long scene) => new()
    {
        SceneEntry = scene,
        InitData = new DungeonInitData { MultiDungeonInitData = new MultiDungeonInitData { MultiDungeonId = multi, MarkId = mark } },
    };

    private static async Task<int> Enter(BotClient bot, EnterDungeonReq req)
    {
        await bot.SendGameAsync(DungeonService.Enter, req);
        return (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result;
    }

    private static async Task<(long Instance, DungeonBaseInfoNotify Info)> Load(BotClient bot, long scene)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == scene)).Result);
        var info = await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        return (sw.SceneID, info);
    }

    private async Task<MonsterUnit?> MonsterIn(long instance, int npc) =>
        await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
            .OfType<MonsterUnit>().SingleOrDefault(m => m.InstanceId == instance && m.Template.NpcId == npc && !m.Dead)));

    private async Task<MonsterUnit> BossIn(long instance, int npc)
    {
        for (int i = 0; i < 150; i++)
        {
            if (await MonsterIn(instance, npc) is { } actor) return actor;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Boss {npc} did not spawn");
    }

    private async Task Kill(BotClient bot, MonsterUnit actor)
    {
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
            Service<MonsterService>().Engage(actor, Session(bot));
            actor.Hp = 1;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead));
    }

    private async Task Leave(BotClient bot)
    {
        await bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == Server.Accounts.RoleStore.SpawnSceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == sw.SceneEntry);
    }

    private long Currency(BotClient bot, int entry) => Session(bot).Progress!.Currency.GetValueOrDefault(entry);

    /// <summary>The latest 5062 full or incremental state of one boss, read from the session's synced snapshot.</summary>
    private DBMultiDungeonAwardInfo AwardInfo(BotClient bot, int multi, int mark) =>
        ((DBMultiDungeonInfo)Session(bot).Synced[DungeonService.Sync]).DungeonInfo[multi].AwardInfo[mark - 1];

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    // ---- Mock Battle (Leisure Forty) ----

    private async Task<CommonDailyActivityRes> SignUp(BotClient bot)
    {
        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = CasualBattleService.ActivityId, RequestId = 7, CasualBattleSignUp = new CasualBattleSignUpReq() });
        return await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser);
    }

    private async Task Tick()
    {
        await OnLoop(() => { Service<CasualBattleService>().Tick(default); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task MockBattleSignsUpTwoCampsScoresKillsAndSettlesTheHigherCamp()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        var refused = await SignUp(alice);
        Assert.Equal(ErrorCodes.ActivityPeriod, refused.Result); // a Thursday noon is not Sunday's Matching window
        Assert.NotNull(refused.CasualBattleSignUp);
        await Gm(alice, "!casual open"); // test aid
        Assert.Equal(0, (await SignUp(alice)).Result);
        Assert.Equal(ErrorCodes.DungeonBusy, (await SignUp(alice)).Result);
        Assert.Equal(0, (await SignUp(bob)).Result);
        await Gm(alice, "!casual start");
        DungeonBaseInfoNotify info = null!;
        long instance = 0;
        foreach (var bot in new[] { alice, bob })
            (instance, info) = await Load(bot, CasualBattleService.Scene);
        Assert.Equal(1, Session(alice).EventFaction);
        Assert.Equal(2, Session(bob).EventFaction);
        Assert.Equal(0, info.Stage);
        Assert.Equal([1L, 0, 2, 0], info.Vars.Single(v => v.VarId == 304).Vi64);
        long open = info.StageModuleData.StageStartTime / 1000;
        Assert.Equal((open + 60) * 1000, info.Vars.Single(v => v.VarId == 921).Vi64[0]);
        await OnLoop(() =>
        {
            Assert.False(Service<CasualBattleService>().CanFight(Session(alice), Session(bob))); // preparation
            return Task.CompletedTask;
        });

        _clock.Advance(TimeSpan.FromSeconds(61));
        await Tick();
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.Stage == 1);
        await OnLoop(async () =>
        {
            Assert.True(Service<CasualBattleService>().CanFight(Session(alice), Session(bob)));
            // Two confirmed kills (CombatService's PvP death callback): 41006 camp +20, 41003 personal +20 each.
            await Service<CasualBattleService>().PlayerKilledAsync(Session(alice), Session(bob));
            await Service<CasualBattleService>().PlayerKilledAsync(Session(alice), Session(bob));
        });
        Assert.Equal([1L, 40, 2, 0], (await Next(bob, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            m => m.Vars.Any(v => v.VarId == 304 && v.Vi64[1] == 40))).Vars.Single(v => v.VarId == 304).Vi64);

        // The live report (30117): both camps' roles in sceneStatistics[0].
        await alice.SendGameAsync(DungeonService.StatisticsCurrent, new GetDungeonStatisticsInDungeonReq());
        var report = await Next(alice, DungeonService.StatisticsCurrent, GetDungeonStatisticsInDungeonResp.Parser);
        Assert.Equal(0, report.Result);
        var roles = report.DungeonStatistics.SceneStatistics[0].RoleStatistics;
        Assert.Equal(2, roles[(long)alice.Self!.Uuid].ExtraData[1001004].LongValue);
        Assert.Equal(40, roles[(long)alice.Self.Uuid].ExtraData[1021005].LongValue);
        Assert.Equal(2, roles[(long)bob.Self!.Uuid].BaseInfo.CampId);

        _clock.Advance(TimeSpan.FromSeconds(600));
        await Tick();
        var won = await Next(alice, CasualBattleService.ResultNotify, DungeonResultNotify.Parser);
        Assert.Equal(1, won.ResultInfo.Result);
        Assert.Equal(1, won.ResultInfo.WinCamp);
        Assert.Equal(alice.Self.Uuid.ToString(), won.ResultInfo.MetaData["mvpPlayerId"]);
        Assert.Equal(40, won.ResultInfo.SceneStatistics[0].CampStatistics[1].ExtraData[1041003].LongValue);
        Assert.Equal(2, (await Next(bob, CasualBattleService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.Result);
        // 5070: RelaxGVGStartMMR 1000 +5 for the win, -3 for the loss; the match is in the recent records.
        var mine = (DBCasualBattleInfo)Session(alice).Synced[CasualBattleService.Sync];
        Assert.Equal(1005, mine.Mmr);
        Assert.Equal(1, Assert.Single(mine.RecentRecords).Outcome);
        Assert.Equal(40, mine.BestRecord.PersonalScore);
        Assert.Equal(997, ((DBCasualBattleInfo)Session(bob).Synced[CasualBattleService.Sync]).Mmr);
        var saved = JsonSerializer.Deserialize<TimedBattleService.State>(Service<Server.Accounts.RoleStore>().LoadTimedBattle((long)alice.Self.Uuid)!)!;
        Assert.Equal(1005, saved.CasualMmr);
    }

    // ---- MVP likes ----

    private async Task Party(BotClient leader, params BotClient[] others)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in others)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
            { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
    }

    [ClientDataFact]
    public async Task RunMembersLikeEachOtherOncePerRunAndSeeCounts()
    {
        await OnLoop(() =>
        {
            // This host's cached 50101 admits two (the export asks for three); the like rules do not depend on it.
            var table = (Dictionary<string, JsonElement>)Service<GameData>().Table("TenGameplayConfig");
            var row = System.Text.Json.Nodes.JsonNode.Parse(table["50101"].GetRawText())!.AsObject();
            row["_kLimit"] = new System.Text.Json.Nodes.JsonArray(2, 5);
            table["50101"] = JsonSerializer.SerializeToElement(row);
            return Task.CompletedTask;
        });
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        long a = (long)alice.Self!.Uuid, b = (long)bob.Self!.Uuid;
        await alice.SendGameAsync(PraiseService.Praise, new MultiDungeonPraiseReq { TargetPlayerIds = { b } });
        Assert.Equal(PraiseService.ErrUnavailable, (await Next(alice, PraiseService.Praise, MultiDungeonPraiseResp.Parser)).Result);

        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice, Request(50101, 1, 3120401)));
        await Next(bob, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser, n => !n.Cancel);
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        await Load(alice, 3120401);
        await Load(bob, 3120401);

        await alice.SendGameAsync(PraiseService.Praise, new MultiDungeonPraiseReq { TargetPlayerIds = { b, a, (long)carol.Self!.Uuid } });
        var liked = await Next(alice, PraiseService.Praise, MultiDungeonPraiseResp.Parser);
        Assert.Equal(0, liked.Result);
        Assert.Equal(0, liked.PraiseResult[b]);
        Assert.Equal(PraiseService.ErrSelf, liked.PraiseResult[a]);
        Assert.Equal(PraiseService.ErrUnavailable, liked.PraiseResult[(long)carol.Self.Uuid]);
        var toBob = await Next(bob, PraiseService.PraiseNotify, MultiDungeonPraiseNotify.Parser);
        Assert.Equal(a, toBob.PraisePlayerId); // "Alice liked you" tip
        Assert.Equal(1, toBob.PraiseInfo[b]);
        Assert.Equal(0, toBob.PraiseInfo[a]);
        Assert.Equal([a], toBob.PraisedPlayerIds);
        Assert.Empty(toBob.LikePlayer);
        var toAlice = await Next(alice, PraiseService.PraiseNotify, MultiDungeonPraiseNotify.Parser);
        Assert.Equal(0, toAlice.PraisePlayerId);
        Assert.True(toAlice.LikePlayer[b]);
        Assert.Equal(3120401, toAlice.DungeonId);

        // Once per target per run: the second like is refused ("You've already given this a Like").
        await alice.SendGameAsync(PraiseService.Praise, new MultiDungeonPraiseReq { TargetPlayerIds = { b } });
        Assert.Equal(PraiseService.ErrAlreadyLiked, (await Next(alice, PraiseService.Praise, MultiDungeonPraiseResp.Parser)).PraiseResult[b]);
        // Bob likes back: a mutual like (the client flags it from likePlayer + praisedPlayerIds).
        await bob.SendGameAsync(PraiseService.Praise, new MultiDungeonPraiseReq { TargetPlayerIds = { a } });
        Assert.Equal(0, (await Next(bob, PraiseService.Praise, MultiDungeonPraiseResp.Parser)).PraiseResult[a]);
        var mutual = await Next(alice, PraiseService.PraiseNotify, MultiDungeonPraiseNotify.Parser, n => n.PraisePlayerId == b);
        Assert.Equal([b], mutual.PraisedPlayerIds);
        Assert.True(mutual.LikePlayer[b]);
        Assert.Equal(1, mutual.PraiseInfo[a]);
    }

    // ---- Hidden Realm ----

    [ClientDataFact]
    public async Task HiddenRealmDrawsAHardBossAndCountsTheClearAsTheHiddenRealm()
    {
        await OnLoop(() =>
        {
            // One-second card countdown instead of FiveGamePlay_RandomDuration's 10 (this host's cached row only).
            ((Dictionary<string, JsonElement>)Service<GameData>().Table("PublicParamConfig"))["FiveGamePlay_RandomDuration"] =
                JsonSerializer.SerializeToElement(1);
            return Task.CompletedTask;
        });
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 89");
        await Gm(alice, "!dungeonsolo on");
        // Personal unlock 35100006: base level 90.
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Enter(alice, Request(50199, 1, 3120601)));
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, Request(50199, 1, 3120601)));
        var drawn = await Next(alice, DungeonService.RandomDungeon, RandomDungeonNotify.Parser);
        Assert.Equal((50199L, 1), (drawn.Origin.MultiDungeonId, drawn.Origin.MarkId));
        Assert.Contains(drawn.Random.MultiDungeonId, new long[] { 50104, 50105, 50106 });
        Assert.InRange(drawn.Random.MarkId, 1, 3);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds() + 1, drawn.EnterTime);
        // During the card's countdown the party cannot start another entry.
        Assert.Equal(ErrorCodes.DungeonPreparing, await Enter(alice, Request(50199, 1, 3120601)));

        var multi = Service<DungeonCatalog>().MultiRows.Single(r => GameData.Int(r, "_iID") == drawn.Random.MultiDungeonId);
        var boss = Service<DungeonCatalog>().Boss(multi, drawn.Random.MarkId)!.Value;
        long scene = GameData.Int(boss, "_iDungeon");
        var (instance, info) = await Load(alice, scene);
        Assert.Equal(drawn.Random.MultiDungeonId, info.InitData.MultiDungeonInitData.MultiDungeonId);
        Assert.Equal(drawn.Random.MarkId, info.InitData.MultiDungeonInitData.MarkId);
        Assert.Equal(50199, info.InitData.MultiDungeonInitData.Origin.MultiDungeonId);
        await Kill(alice, await BossIn(instance, (int)GameData.Int(boss, "_iBoss")));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await Next(alice, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser);
        // The clear is the Hidden Realm's: its record, not the drawn Hard boss's.
        Assert.True(Session(alice).Dungeons!.Records["50199:1"].Passed);
        Assert.False(Session(alice).Dungeons!.Records.ContainsKey($"{drawn.Random.MultiDungeonId}:{drawn.Random.MarkId}"));
        for (int i = 0; i < 100 && AwardInfo(alice, 50199, 1).FirstPassState != MultiDungeonFirstPassState.PassLongTime; i++)
            await Task.Delay(20); // 5062 follows the award notify
        Assert.Equal(MultiDungeonFirstPassState.PassLongTime, AwardInfo(alice, 50199, 1).FirstPassState);
    }

    // ---- Realm of the Gods ----

    private const int Trial = 100103, TrialBoss = 14720027, Normal = 100101, NormalBoss = 14720026;
    private const long RaidScene = 3020101;

    [ClientDataFact]
    public async Task FinalTrialClearFillsHallOfFameAndPaysHonorsAfterStartBattle()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await alice.SendGameAsync(DungeonService.HallOfFame, new QueryHallOfFameInfoReq { MultiDungeonBossId = TrialBoss });
        var none = await Next(alice, DungeonService.HallOfFame, QueryHallOfFameInfoResp.Parser);
        Assert.Equal(TrialBoss, none.MultiDungeonBossId);
        Assert.Null(none.HallOfFameInfo); // the widget then says "No Team First Clear Yet"

        Assert.Equal(ErrorCodes.DungeonTeamSize, await Enter(alice, Request(Trial, 1, RaidScene)));
        await Gm(alice, "!dungeonsolo on"); // test aid: the trial asks for six
        Assert.Equal(0, await Enter(alice, Request(Trial, 1, RaidScene)));
        var (instance, info) = await Load(alice, RaidScene);
        // The raid opens in its preparation phase: Start Battle (var 1001 = [1]) and no boss yet.
        var prepare = info.Vars.Any(v => v.VarId == 1001) ? info
            : await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.Vars.Any(v => v.VarId == 1001));
        Assert.Equal([1L], prepare.Vars.Single(v => v.VarId == 1001).Vi64);
        Assert.Equal(0, prepare.StageModuleData.MapState);
        await Task.Delay(200);
        Assert.Null(await MonsterIn(instance, TrialBoss));
        await alice.SendGameAsync(DungeonService.ModuleButton, new DungeonModuleButtonReq { ButtonId = 2 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, DungeonService.ModuleButton, DungeonModuleButtonResp.Parser)).Result);
        await alice.SendGameAsync(DungeonService.ModuleButton, new DungeonModuleButtonReq { ButtonId = 1 });
        Assert.Equal(0, (await Next(alice, DungeonService.ModuleButton, DungeonModuleButtonResp.Parser)).Result);
        var fight = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            m => m.Vars.Any(v => v.VarId == 1001 && v.Vi64.Count == 0));
        Assert.Equal(1, fight.StageModuleData.MapState);
        await Kill(alice, await BossIn(instance, TrialBoss));

        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        var award = await Next(alice, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser);
        // A quick clear with the whole revive pool left finishes all seven goals (TenGameplayFinalTrialConfig 101011..101031).
        Assert.Equal([101011L, 101012, 101013, 101021, 101022, 101023, 101031], award.Honor.HonorInfo.Keys.Order());
        Assert.All(award.Honor.HonorInfo.Values, state => Assert.Equal(DBMultiDungeonHonorState.Finish, state));

        // The hall of fame keeps the first clear, written by its own operation right after the settlement.
        QueryHallOfFameInfoResp fame = null!;
        for (int i = 0; i < 100; i++)
        {
            await alice.SendGameAsync(DungeonService.HallOfFame, new QueryHallOfFameInfoReq { MultiDungeonBossId = TrialBoss });
            fame = await Next(alice, DungeonService.HallOfFame, QueryHallOfFameInfoResp.Parser);
            if (fame.HallOfFameInfo != null) break;
            await Task.Delay(20);
        }
        Assert.NotNull(fame.HallOfFameInfo);
        Assert.Equal((long)alice.Self!.Uuid, fame.HallOfFameInfo.TeamLeaderId);
        Assert.Equal("Alice", fame.HallOfFameInfo.TeamLeaderName);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds(), fame.HallOfFameInfo.PassTimestamp);
        var card = Assert.Single(fame.HallOfFameInfo.RoleInfo);
        Assert.Equal((long)alice.Self.Uuid, card.Rolebase.Roleid);
        Assert.Equal(99, card.Rolebase.BaseLevel);
        Assert.NotEmpty(card.Selfproperty.Propertys);
        Assert.NotNull(Service<Server.Accounts.AccountStore>().ReadSync<string?>(
            new Server.Accounts.Persistence.SeasonStateReadPlan(DungeonService.ServerInfoKey)));

        // 5062 shows the goals; 82589 pays a finished goal once (AwardConfig 10071101: 5 x item 9).
        for (int i = 0; i < 100 && AwardInfo(alice, Trial, 1).Honor?.HonorInfo.GetValueOrDefault(101011) != DBMultiDungeonHonorState.Finish; i++)
            await Task.Delay(20); // 5062 follows the award notify
        Assert.Equal(DBMultiDungeonHonorState.Finish, AwardInfo(alice, Trial, 1).Honor.HonorInfo[101011]);
        long before = Service<CharacterService>().CountOf(Session(alice), 9);
        await alice.SendGameAsync(DungeonService.ClaimHonor, new MultiDungeonClaimHonorAwardReq
        { MultiDungeonId = Trial, MarkId = 1, HonorIds = { 101011 } });
        Assert.Equal(0, (await Next(alice, DungeonService.ClaimHonor, MultiDungeonClaimHonorAwardResp.Parser)).Result);
        Assert.Equal(before + 5, Service<CharacterService>().CountOf(Session(alice), 9));
        Assert.Equal(DBMultiDungeonHonorState.Received, AwardInfo(alice, Trial, 1).Honor.HonorInfo[101011]);
        await alice.SendGameAsync(DungeonService.ClaimHonor, new MultiDungeonClaimHonorAwardReq
        { MultiDungeonId = Trial, MarkId = 1, HonorIds = { 101011 } });
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(alice, DungeonService.ClaimHonor, MultiDungeonClaimHonorAwardResp.Parser)).Result);
        await alice.SendGameAsync(DungeonService.ClaimHonor, new MultiDungeonClaimHonorAwardReq
        { MultiDungeonId = Normal, MarkId = 1, HonorIds = { 101012 } });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, DungeonService.ClaimHonor, MultiDungeonClaimHonorAwardResp.Parser)).Result);
        var saved = JsonSerializer.Deserialize<DungeonService.State>(Service<Server.Accounts.RoleStore>().LoadDungeons((long)alice.Self.Uuid)!)!;
        Assert.Equal(2, saved.Honors[$"{Trial}:1"][101011]);
        Assert.Equal(1, saved.Honors[$"{Trial}:1"][101012]);
    }

    [ClientDataFact]
    public async Task FirstClearUnlocksSacredBlessingLevelsTipAndRunBuffs()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Null(AwardInfo(alice, Normal, 1).SacredBlessings);
        // 14720026's blessing (group 1000) unlocks with the server's first clear ([3034,14720026,0]); level 2 a day later.
        await Gm(alice, $"!firstclear {NormalBoss} 1.5");
        var blessing = AwardInfo(alice, Normal, 1).SacredBlessings;
        Assert.NotNull(blessing);
        Assert.Equal(2, blessing.UnlockSacredBlessingID); // TenGameplayBuffConfig 2: group 1000 level 2 (_iUnlockDay 1)
        Assert.Equal((long)alice.Self!.Uuid, blessing.TeamLeaderId);
        Assert.Equal("Alice", blessing.FirstPassTeamLeaderName);
        Assert.True(blessing.IsNeedTip);
        await alice.SendGameAsync(DungeonService.MarkBlessings, new MultiDungeonMarkSacredBlessingsReq { MultiDungeonId = Normal, MarkId = 1 });
        Assert.Equal(0, (await Next(alice, DungeonService.MarkBlessings, MultiDungeonMarkSacredBlessingsResp.Parser)).Result);
        Assert.False(AwardInfo(alice, Normal, 1).SacredBlessings.IsNeedTip);
        await alice.SendGameAsync(DungeonService.MarkBlessings, new MultiDungeonMarkSacredBlessingsReq { MultiDungeonId = Trial, MarkId = 1 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, DungeonService.MarkBlessings, MultiDungeonMarkSacredBlessingsResp.Parser)).Result);

        // A day later the next level is up and the tip shows again.
        _clock.Advance(TimeSpan.FromDays(1));
        await OnLoop(() => Service<DungeonService>().SyncAsync(Session(alice)));
        Assert.Equal(3, AwardInfo(alice, Normal, 1).SacredBlessings.UnlockSacredBlessingID);
        Assert.True(AwardInfo(alice, Normal, 1).SacredBlessings.IsNeedTip);

        // In the run the level's buff (990601) is on every member.
        await Gm(alice, "!dungeonsolo on");
        Assert.Equal(0, await Enter(alice, Request(Normal, 1, RaidScene)));
        await Load(alice, RaidScene);
        for (int i = 0; i < 100 && !Session(alice).Buffs.Any(b => b.ConfigId == 990601); i++) await Task.Delay(20);
        Assert.Contains(Session(alice).Buffs, b => b.ConfigId == 990601);
    }
}
