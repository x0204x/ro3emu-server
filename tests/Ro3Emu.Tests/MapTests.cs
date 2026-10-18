using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Map entry, NPCs, portals and Kafra teleports on the client's own world data.</summary>
public sealed partial class MapTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-map-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    private const long Prontera = 10100, SouthGate = 10111;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        string gameData = Path.Combine(RepoRoot(), "data");
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = gameData;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
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
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private async Task WaitSceneOnlinePublicationAsync(BotClient bot)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!await loop.InvokeAsync(() => Task.FromResult(sessions.All().Any(s =>
                   s.Account == bot.Account && s.Role != null && s.Handoff == null && s.GuildOnlinePublished))))
        {
            if (elapsed.Elapsed >= Wait) throw new TimeoutException("Scene online publication did not start for the peer role.");
            await Task.Delay(10);
        }
        // The ready packets precede SceneLoaded's Realm-protected guild publication. Drain that
        // publication before Alice's held MVP settlement so Bob's channel can process lottery.
        await loop.InvokeAsync(async () => { using var scope = await loop.Owners!.EnterAsync(
            "map test scene online barrier", ReservationKey.Realm); }).WaitAsync(Wait);
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    /// <summary>Follows a server-sent map change like the client: 70010, then 30005, returns the ENTITYSELF of the new map.</summary>
    private static async Task<(SwitchSceneLineResp Switch, EntityBase Self, AoIViewChangeData Npcs)> FollowSceneChange(BotClient bot)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        var npcs = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.EntityType == 2));
        return (sw, self.Base, npcs);
    }

    [ClientDataFact]
    public async Task NewRoleLandsOnWalkableGroundAndSeesTheMapsNpcs()
    {
        await using var alice = await InWorld("Alice");
        var pos = alice.Self!.MovementInfo.Pos;
        Assert.Equal(347.26f, pos.X, 0.01f); // South Gate born point (m_kBornPoints), where the first quest waits
        Assert.NotEqual(0f, pos.Y); // nav-grid height, not the editor's
        var npcs = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        Assert.All(npcs.Bases, b => Assert.Equal(2, b.EntityType));
        Assert.Contains(npcs.Bases, b => b.EntityTemplateID == 100194); // the novice crowd next to it

        await Gm("alice", "!warp 10111 156 192"); // PublicParamConfig.SpawnScenePos
        var town = (await FollowSceneChange(alice)).Npcs;
        Assert.Equal("卡普拉職員", town.Bases.First(b => b.EntityTemplateID == 200015).Name);
        Assert.Contains(town.Bases, b => b.EntityTemplateID == 300001); // portal to Prontera
    }

    [ClientDataFact]
    public void KafrasStandOnTheGroundSoTheClientsInteractDistanceReachesThem()
    {
        // Sograt Desert 10514: the scene editor puts the Kafra at y -4.958 over ground -8.5; the client checks a 3D 2 m distance.
        var map = _app.Services.GetRequiredService<WorldData>().Map(10514)!;
        var kafras = map.Npcs.Where(n => n.Kind == WorldNpcKind.Kafra).ToArray();
        Assert.NotEmpty(kafras);
        Assert.All(kafras, k => Assert.InRange(k.Y - map.Nav!.WalkHeight(k.X, k.Z, k.Y)!.Value, -0.01f, 0.01f));
        Assert.Contains(kafras, k => MathF.Abs(k.X - 205) < 1 && MathF.Abs(k.Z - 331) < 1 && k.Y < -7f);
    }

    [ClientDataFact]
    public async Task PortalLeadsToProntera()
    {
        await using var alice = await InWorld("Alice");
        var npcs = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        var portal = npcs.Bases.Single(b => b.EntityTemplateID == 300001);
        Assert.Equal(169.6569f, portal.MovementInfo.Pos.X, 0.001f); // scene-editor floats verbatim

        await Gm("alice", "!jump 169.6 372");
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)alice.Self!.Uuid);
        var req = new NpcInteractionReq { NpcUid = (long)portal.Uuid, InteractionId = 8, SceneConfigId = SouthGate, Pos = portal.MovementInfo.Pos };
        await alice.SendGameAsync(SceneService.NpcInteraction, req);
        var rsp = await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser);
        Assert.Equal(0, rsp.Result);
        Assert.Equal(req, rsp.Req);

        var (sw, self, pronteraNpcs) = await FollowSceneChange(alice);
        Assert.Equal(Prontera, sw.SceneEntry);
        Assert.Equal(156.02f, self.MovementInfo.Pos.X, 0.05f); // Prontera TP 36 + foothold (0, 0, 7)
        Assert.Equal(33.15f, self.MovementInfo.Pos.Z, 0.05f);
        Assert.True(pronteraNpcs.Bases.Count > 100);
    }

    [ClientDataFact]
    public async Task KafraTeleportsToTheTargetMapsKafraPoint()
    {
        await using var alice = await InWorld("Alice");
        var npcs = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        var kafra = npcs.Bases.First(b => b.EntityTemplateID == 200015 && b.MovementInfo.Pos.Z > 300); // at 174, 370
        await Gm("alice", "!jump 172 368");
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)alice.Self!.Uuid);

        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = (long)kafra.Uuid, InteractionId = 9, TargetSceneEntry = Prontera });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        var (sw, self, pronteraNpcs) = await FollowSceneChange(alice);
        Assert.Equal(Prontera, sw.SceneEntry);
        Assert.Equal(164.02f, self.MovementInfo.Pos.X, 0.05f); // Kafra 100001 (IsKaPuLaPos) + foothold (5, 0, 0)
        Assert.Equal(189.88f, self.MovementInfo.Pos.Z, 0.05f);

        // A map outside MapLinkData.Capra (Glast Heim) is refused, also by a Kafra standing next to the player.
        var pronteraKafra = pronteraNpcs.Bases.First(b => b.EntityTemplateID == 100001);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = (long)pronteraKafra.Uuid, InteractionId = 9, TargetSceneEntry = 10700 });
        Assert.NotEqual(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task WarpWithoutCoordinatesUsesTheBornPointAndDropsStaleWalks()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!warp 10100");
        var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        // A walk from the old map arriving before 30005 must not move the player.
        await alice.WalkAsync(10, 0, 10, stop: true);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        var self = await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(156.04f, self.Base.MovementInfo.Pos.X, 0.05f);
        Assert.Equal(181.91f, self.Base.MovementInfo.Pos.Z, 0.05f);
        Assert.Equal(180f, self.Base.MovementInfo.Face);
    }

    [ClientDataFact]
    public async Task MonstersSpawnAndAPlayerCanKillOne()
    {
        await using var alice = await InWorld("Alice");
        // Monsters arrive as one 31037 after the NPCs: entityType 1, NPCConfig template, camp 3, HP in the base.
        var units = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.EntityType == 1));
        var monster = units.Bases.Where(b => b.EntityType == 1).OrderBy(b => Dist(b, alice.Self!)).First();
        Assert.Equal(3, monster.EntityCamp);
        var hpProp = monster.Property.Single(p => p.Idx == 101);
        Assert.True(hpProp.Data > 0);
        Assert.Contains(monster.EntityTemplateID, new ulong[] { 5000001, 5000002, 5000003, 5000035 });

        await Gm("alice", "!lvl 60"); // hit hard enough to finish quickly
        await Gm("alice", "!quest accept 10101220"); // Monster Hunt: 310001 [20, 10111] = kill 20 on South Gate
        await Gm("alice", $"!jump {monster.MovementInfo.Pos.X:0.0} {monster.MovementInfo.Pos.Z + 1:0.0}".Replace(',', '.'));
        await alice.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)monster.Uuid });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)monster.Uuid });
        var rsp = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, rsp.Result);
        var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == (long)alice.Self!.Uuid);
        Assert.Equal(1000001, swing.SkillID);
        Assert.True(swing.SkillMs > 0);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == (long)alice.Self!.Uuid);
        Assert.Equal(1000002, hit.ShowPoint);
        var dst = Assert.Single(hit.Dsts);
        Assert.Equal((long)monster.Uuid, dst.DstID);

        // The server keeps swinging (auto-attack) until the monster dies: 31049 Base with state Dead.
        alice.Timeout = TimeSpan.FromSeconds(40);
        var death = await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.EntityBaseChange) && EntityBase.Parser.ParseFrom(e.Body) is var b
            && b.Uuid == monster.Uuid && b.State == EntityState.Dead, TimeSpan.FromSeconds(40));
        var dead = EntityBase.Parser.ParseFrom(death.Body);
        Assert.Equal(EntityBase.Types.NotifyType.Base, dead.NotifyType);
        Assert.Equal(3, dead.EntityCamp); // full overwrite: camp and template kept
        Assert.Equal(monster.EntityTemplateID, dead.EntityTemplateID);
        // The killer gets the drop tables' Base EXP (item 100) as exp: a 5066 increment.
        var exp = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark && m.HasExp);
        Assert.True(exp.Exp > 0 || exp.HasLevel);
        Assert.True(exp.JobExp > 0 || exp.HasJobLevel);
        // The kill counts for the quest: 5027 completeNum 1 of 20.
        var quest = await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.TryGetValue(10101220, out var t) && t.Inc != null);
        Assert.Equal([1L], quest.Infos[10101220].Inc.CompleteNum.All.Value);
        // The corpse leaves after _iBodyRemainTime.
        await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView) && AoIViewChangeData.Parser.ParseFrom(e.Body).LeaveAoi.Contains(monster.Uuid), TimeSpan.FromSeconds(6));
    }

    [ClientDataFact]
    public async Task PotionHealsWithCooldownAndSwordmanSkillsWork()
    {
        await using var alice = await InWorld("Alice");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long potion = bag.PackInfo[1].All.Items.Values.Single(i => i.ItemEntry == 1000000).ItemGuid;

        // Beginner Potion: action 100 RecoverFixed +2000 HP, _iCD 30 s, group 20 shares 10 s.
        await Gm("alice", "!hp 5");
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        var use = await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser);
        Assert.Equal(0, use.Result);
        Assert.True(use.ItemCd > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 25000);
        var healed = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 2000 && p.Data > 5));
        Assert.True(healed.Property.Propertys.Single(p => p.PropertyId == 2000).Data > 5);
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        Assert.Equal(161, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result); // in cooldown

        // Swordman with Endure (11005) learned: self buff 1100501 (+55 DEF) and a 15 s cooldown.
        await Gm("alice", "!job 1100");
        var req = new SkillGrowthSkillPlanUpdateReq();
        req.LevelUpList[11004] = new SkillData { SkillId = 11004, SkillLevel = 5 }; // Endure needs Provoke 5
        req.LevelUpList[11005] = new SkillData { SkillId = 11005, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, req);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100501 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var cd = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser);
        Assert.Equal(11005, cd.Infos.Single().SkillId);
        var buff = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser);
        Assert.Equal(1100501, buff.Buffs.Single().ConfigId);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100501 });
        Assert.Equal(3012, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result); // in cooldown
    }

    private static float Dist(EntityBase a, EntityBase b)
    {
        float dx = a.MovementInfo.Pos.X - b.MovementInfo.Pos.X, dz = a.MovementInfo.Pos.Z - b.MovementInfo.Pos.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    [ClientDataFact]
    public async Task FunctionsOpenByLevelFromTheClientConditions()
    {
        await using var alice = await InWorld("Alice");
        var open = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser);
        Assert.True(open.SyncAllMark);
        Assert.True(open.FunctionOpenInfos[2404].All); // map ("near") chat: level 1
        Assert.True(open.FunctionOpenInfos[26].All);   // bag: level 1
        Assert.False(open.FunctionOpenInfos.ContainsKey(45)); // friends: level 15
        await Gm("alice", "!lvl 15");
        var more = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(more.FunctionOpenInfos[45].All);
    }

    [ClientDataFact]
    public async Task NoviceStoryStartsAtTheBornPointAndProgressesByEvents()
    {
        await using var alice = await InWorld("Alice");
        // Full 5027: the story root running, with a completeNum per target and a non-empty completeTaskNum.
        var full = await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.SyncAllMark);
        var first = full.Infos[TaskService.FirstTask].All;
        Assert.Equal(TaskState.Running, first.Status);
        Assert.True(first.Follow);
        Assert.Equal([0L], first.CompleteNum);
        Assert.NotEmpty(full.CompleteTaskNum);
        Assert.InRange(alice.Self!.MovementInfo.Pos.X, 340f, 355f); // South Gate born point (347.26, 293.01)

        // Task 1: reach area 10000001 -> delivered, task 2 (plot 18) accepted.
        await alice.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = 10000001 });
        var step = await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(10101020));
        Assert.Equal(TaskState.Delivery, step.Infos[TaskService.FirstTask].Inc.Status);
        Assert.Equal(1, step.CompleteTaskNum[TaskService.FirstTask].All);
        Assert.Equal(TaskState.Running, step.Infos[10101020].All.Status);

        // Task 2: cutscene 18 ends -> +6 base exp, task 3 accepted.
        await alice.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = 18 });
        var exp = await Next(alice, CharacterService.GetRewardNotify, GetRewardNotify.Parser, r => r.Rewards.Any(i => i.ItemEntry == 100));
        Assert.Equal(6, exp.Rewards.Single(i => i.ItemEntry == 100).Stack);
        step = await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(10101030));
        Assert.Equal(TaskState.Delivery, step.Infos[10101020].Inc.Status);

        // Task 3 (area) -> task 4: talk to Rhine (client task point 1010104001) -> task 5 kill 5 Porings.
        await alice.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = 1010103001 });
        await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(10101040));
        var rhine = _app.Services.GetRequiredService<WorldData>().Map(SouthGate)!.Points[1010104001];
        await Gm("alice", FormattableString.Invariant($"!jump {rhine.X} {rhine.Z}")); // dialogue requires proximity
        await alice.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = 1010104001, Areapointid = 1010104001 });
        step = await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(10101050));
        Assert.Equal([0L], step.Infos[10101050].All.CompleteNum);

        // A wrong dialog does nothing; the GM shortcut completes the kills -> task 6 accepted, the Auto-Potion function opens on its delivery later.
        await Gm("alice", "!quest finish 10101050");
        step = await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(10101060));
        Assert.Equal(TaskState.Running, step.Infos[10101060].All.Status);
        await alice.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = 1010106001, Areapointid = 1010104001 });
        var potions = await Next(alice, CharacterService.GetRewardNotify, GetRewardNotify.Parser, r => r.Rewards.Any(i => i.ItemEntry == 1000000));
        Assert.Equal(50, potions.Rewards.Single(i => i.ItemEntry == 1000000).Stack);
        var functions = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.FunctionOpenInfos.ContainsKey(82));
        Assert.True(functions.FunctionOpenInfos[82].All); // Auto-Potion: condition 4004 task 10101060
    }

    [ClientDataFact]
    public async Task FieldMvpSpawnsAndItsKillPaysOutAndStartsTheRespawnTimer()
    {
        var errors = new MvpDiagnosticErrors();
        _app.Services.GetRequiredService<ILoggerFactory>().AddProvider(errors);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest accept 100014"); // 103011: two MVP settlements
        await Gm("alice", "!quest accept 110001"); // 103022: once-MVP settlement
        // 43009: all 17 field MVPs, Angeling (cfg 1) alive.
        await alice.SendGameAsync(MvpService.BriefInfo, new MVPBriefInfoReq());
        var brief = await Next(alice, MvpService.BriefInfo, MVPBriefInfoRsp.Parser);
        Assert.Equal(17, brief.Infos.Count);
        Assert.Equal(MVP_STATE.Born, brief.Infos.Single(i => i.CfgId == 1).State);

        await Gm("alice", "!lvl 99");
        await Gm("alice", "!quest done 10105090"); // Opens To-Do; 500010 tracks settled MVP participation.
        // The ticker spawns the MVPs within a second of start; ask until it is alive.
        for (int i = 0; i < 20; i++)
        {
            await Gm("alice", "!mvp where 1");
            var reply = await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify) && (System.Text.Encoding.UTF8.GetString(e.Body).Contains("MVP 1") || System.Text.Encoding.UTF8.GetString(e.Body).StartsWith("10214 ")), Wait);
            if (System.Text.Encoding.UTF8.GetString(reply.Body).StartsWith("10214 ")) break;
            await Task.Delay(300);
        }
        await Gm("alice", "!mvp goto 1"); // Poring Island 10214
        var (_, _, npcs) = await FollowSceneChange(alice);
        var angeling = npcs.Bases.FirstOrDefault(b => b.EntityTemplateID == 5020001)
            ?? (await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.EntityTemplateID == 5020001)))
                .Bases.Single(b => b.EntityTemplateID == 5020001);
        Assert.Equal(1, angeling.EntityType);

        // The GM shortcut offsets x by 3m while retaining the previous scene's height hint.
        // A random MVP location can put that offset off the walkable layer; this test exercises
        // combat/settlement, so start at the live boss's grounded position and stop idle roaming.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            var boss = _app.Services.GetRequiredService<World>().Map(player.WorldScene).Units.OfType<MonsterUnit>()
                .Single(m => m.Uid == (long)angeling.Uuid);
            boss.Advance(MonsterService.NowMs);
            boss.Moving = false;
            boss.NextRoamMs = long.MaxValue;
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(player,
                new MapPoint(boss.X, boss.Y, boss.Z, boss.Face));
        });

        // Hit it: the boss fights back with its own normal attack and summons 3 Self-Destruct Porings.
        await alice.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)angeling.Uuid });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)angeling.Uuid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var summon = await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill) && UseInSkillInfo.Parser.ParseFrom(e.Body) is var u
            && u.SrcID == (long)angeling.Uuid && u.SkillID == 5202101, TimeSpan.FromSeconds(10));
        Assert.NotNull(summon);
        var minions = await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView)
            && AoIViewChangeData.Parser.ParseFrom(e.Body).Bases.Count(b => b.EntityTemplateID == 5000036) >= 1, TimeSpan.FromSeconds(10));
        Assert.NotNull(minions);

        // The real summon is observed above. Retire its three minions before the slow fault
        // phases: freezing the boss's swing alone leaves their normal attacks able to kill Alice.
        bool minionsRetired = false;
        for (int attempt = 0; attempt < 50 && !minionsRetired; attempt++)
        {
            minionsRetired = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
            {
                var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                var map = _app.Services.GetRequiredService<World>().Map(player.WorldScene);
                var boss = map.Units.OfType<MonsterUnit>().Single(m => m.Uid == (long)angeling.Uuid);
                boss.NextSwingMs = long.MaxValue;
                var summoned = map.Units.OfType<MonsterUnit>()
                    .Where(m => m.Template.NpcId == 5000036 && !m.Dead).ToArray();
                foreach (var m in summoned) m.NextSwingMs = long.MaxValue;
                if (summoned.Length < 3) return false;
                Assert.Equal(3, summoned.Length);
                _app.Services.GetRequiredService<MvpService>().StopSummonsAfterObservationForTest(boss);
                var monsters = _app.Services.GetRequiredService<MonsterService>();
                foreach (var m in summoned) await monsters.KillAsync(m);
                return true;
            });
            if (!minionsRetired) await Task.Delay(50);
        }
        Assert.True(minionsRetired, "Angeling's observed summon must create all three minions.");

        // This phase verifies a prepared lethal settlement, not FLEE probability. Keep the real
        // cast/damage/award path, using the same deterministic hit seam as other settlement tests.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        { SureHitRolls.Install(_app); return Task.CompletedTask; });
        async Task ReadyBossForLethalCastAsync()
        {
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
            {
                var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                var boss = _app.Services.GetRequiredService<World>().Map(player.WorldScene).Units.OfType<MonsterUnit>()
                    .Single(m => m.Uid == (long)angeling.Uuid);
                Assert.False(player.Dead);
                Assert.False(boss.Dead);
                boss.Advance(MonsterService.NowMs);
                boss.Moving = false;
                boss.NextRoamMs = long.MaxValue;
                await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(player,
                    new MapPoint(boss.X, boss.Y, boss.Z, boss.Face));
                _app.Services.GetRequiredService<MonsterService>().Engage(boss, player);
                boss.NextSwingMs = long.MaxValue; // the already observed summon/attack phase is complete
                Assert.False(boss.Returning);
            });
            await Gm("alice", "!mvp weaken 1");
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var boss = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .Single(m => m.Uid == (long)angeling.Uuid);
                Assert.Equal(1, boss.Hp);
                Assert.False(boss.Returning);
                return Task.CompletedTask;
            });
        }
        await ReadyBossForLethalCastAsync();
        var accounts = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.AccountStore>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var mvp = _app.Services.GetRequiredService<MvpService>();
        long ReceiptCount(string kind)
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind=$kind";
            command.Parameters.AddWithValue("$kind", kind);
            return (long)command.ExecuteScalar()!;
        }
        void RecordRollbackAndStopRepeat(MonsterUnit monster, long before, long after,
            TaskCompletionSource<(long Before, long After, bool Dead)> observed)
        {
            if (monster.Uid != (long)angeling.Uuid) return;
            var snapshot = (before, after, monster.Dead);
            // UseSkill(1000001) also arms an auto-repeat; after HP is restored to 1,
            // a delayed repeat or an already queued cast would kill it during the next fixture phase.
            var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            player.Attack = null;
            if (player.SkillCast is { } cast) cast.Cancelled = true;
            observed.TrySetResult(snapshot);
        }
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource<(long Before, long After, bool Dead)>(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.LethalRollbackObservedForTest = (monster, before, after) =>
            RecordRollbackAndStopRepeat(monster, before, after, restored);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/event/mvp-settlement") return Task.CompletedTask;
            rejected.TrySetResult();
            throw new IOException("Injected prepublication settlement refusal.");
        };
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)angeling.Uuid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            try { await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException error) { throw new InvalidOperationException(string.Join("\n", errors.Messages), error); }
            var rollback = await restored.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal((1L, 1L, false), rollback); // observed at the rollback boundary, before the boss's Return AI heals it
            await Task.Delay(1200); // complete the failed action before retrying the normal attack
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var boss = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .Single(m => m.Uid == (long)angeling.Uuid);
                Assert.False(boss.Dead);
                return Task.CompletedTask;
            });
            Assert.Equal(0, ReceiptCount("event/mvp-settlement"));
        }
        finally { accounts.BeforeOwnedCommitForTest = null; combat.LethalRollbackObservedForTest = null; }

        // A failure in the awaited damage callback happens before any MVP source exists. The same
        // rollback restores targetability and reverses the invisible lethal hit's MVP score.
        await ReadyBossForLethalCastAsync();
        var damageRestored = new TaskCompletionSource<(long Before, long After, bool Dead)>(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.LethalRollbackObservedForTest = (monster, before, after) =>
            RecordRollbackAndStopRepeat(monster, before, after, damageRestored);
        Func<GameSession, MonsterUnit, long, Task> failDamage = (_, monster, _) =>
            monster.Uid == (long)angeling.Uuid ? Task.FromException(new IOException("Injected MVP damage callback refusal.")) : Task.CompletedTask;
        combat.EventDamageRecorded.Add(failDamage);
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)angeling.Uuid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            Assert.Equal((1L, 1L, false), await damageRestored.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, ReceiptCount("event/mvp-settlement"));
        }
        finally { combat.EventDamageRecorded.Remove(failDamage); combat.LethalRollbackObservedForTest = null; }

        // A failed source probe is an unknown outcome. Retain its original source and resolve it
        // on the next MVP tick; do not roll a second settlement while its result is unknown.
        await ReadyBossForLethalCastAsync();
        var probeFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unknownRestored = new TaskCompletionSource<(long Before, long After, bool Dead)>(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.LethalRollbackObservedForTest = (monster, before, after) =>
            RecordRollbackAndStopRepeat(monster, before, after, unknownRestored);
        accounts.BeforeOwnedCommitForTest = label => label == "economy/event/mvp-settlement"
            ? Task.FromException(new IOException("Injected unknown settlement refusal.")) : Task.CompletedTask;
        mvp.BeforeSettlementProbeForTest = _ =>
        {
            if (probeFault.Task.IsCompleted) return Task.CompletedTask;
            probeFault.TrySetResult();
            return Task.FromException(new IOException("Injected one-shot source probe failure."));
        };
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)angeling.Uuid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await probeFault.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, ReceiptCount("event/mvp-settlement"));
            Assert.Equal((1L, 1L, false), await unknownRestored.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, ReceiptCount("event/mvp-settlement"));
        }
        finally
        {
            accounts.BeforeOwnedCommitForTest = null;
            mvp.BeforeSettlementProbeForTest = null;
            combat.LethalRollbackObservedForTest = null;
        }
        await using var bob = await InWorld("Bob");
        await WaitSceneOnlinePublicationAsync(bob);
        await ReadyBossForLethalCastAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskService = _app.Services.GetRequiredService<TaskService>();
        var beforeProgress = taskService.BeforeProgress;
        bool prepHeld = false;
        taskService.BeforeProgress = async session =>
        {
            if (session.Role?.Id == (long)alice.Self!.Uuid && !prepHeld)
            {
                prepHeld = true;
                prepEntered.TrySetResult();
                await prepRelease.Task;
            }
            if (beforeProgress != null) await beforeProgress(session);
        };
        Task? gmAdvance = null;
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/event/mvp-settlement") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)angeling.Uuid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await prepEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Gm("alice", "!mvp weaken 1"); // before the cooldown reservation, HP0 itself fences the GM
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var boss = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .Single(m => m.Uid == (long)angeling.Uuid);
                Assert.False(boss.Dead);
                Assert.Equal(0, boss.Hp);
                return Task.CompletedTask;
            });
            prepRelease.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            gmAdvance = Gm("alice", "!mvp weaken 1");
            await Task.Delay(200);
            Assert.False(gmAdvance.IsCompleted); // the GM cannot reset the boss during its held lethal settlement
            Assert.Equal(0, ReceiptCount("event/mvp-settlement"));
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var boss = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .Single(m => m.Uid == (long)angeling.Uuid);
                Assert.False(boss.Dead);
                Assert.Equal(0, boss.Hp);
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(MvpService.BriefInfo, new MVPBriefInfoReq());
            var heldBrief = await Next(alice, MvpService.BriefInfo, MVPBriefInfoRsp.Parser);
            Assert.Equal(MVP_STATE.Born, heldBrief.Infos.Single(i => i.CfgId == 1).State);
            await bob.SendGameAsync(LotteryService.DrawRequest,
                new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
            Assert.NotEqual(0, (await Next(bob, LotteryService.DrawRequest, LotteryResp.Parser)).Result);
        }
        finally
        {
            prepRelease.TrySetResult();
            release.TrySetResult();
            taskService.BeforeProgress = beforeProgress;
            accounts.BeforeOwnedCommitForTest = null;
        }
        if (gmAdvance != null) await gmAdvance.WaitAsync(TimeSpan.FromSeconds(20));
        alice.Timeout = TimeSpan.FromSeconds(20);

        // The killer gets First strike + Final blow + Ranking + Participation (Minted Coin among them) and a result popup.
        var result = MVPResultNtf.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(MvpService.ResultNtf), TimeSpan.FromSeconds(20))).Body);
        Assert.Equal(1, result.CfgId);
        Assert.Contains(result.Awards, a => a.Tp == MVP_AWARD_TYPE.First);
        Assert.Contains(result.Awards, a => a.Tp == MVP_AWARD_TYPE.End);
        Assert.Contains(result.Awards, a => a.Tp == MVP_AWARD_TYPE.Join);
        Assert.Contains(result.Awards, a => a.ItemId == 2 && a.Count >= 30000); // 30 x 1000 Minted Coin (pool 21001)
        Assert.Equal(1, result.PlayerInfo.FirstNum);
        Assert.Equal(1, ReceiptCount("event/mvp-settlement"));
        Assert.Equal(1, ReceiptCount("event/mvp-delivery"));
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}"))
        {
            db.Open();
            using var source = db.CreateCommand();
            source.CommandText = "SELECT o.source_key,o.delivered,r.leg_count FROM mvp_award_obligation o JOIN operation_receipt r ON r.command_kind='event/mvp-settlement' WHERE o.player_id=$role";
            source.Parameters.AddWithValue("$role", (long)alice.Self!.Uuid);
            using var row = source.ExecuteReader();
            Assert.True(row.Read());
            Assert.StartsWith("mvp/kill/1/", row.GetString(0));
            Assert.Equal(1, row.GetInt64(1));
            Assert.True(row.GetInt64(2) > 0);
            Assert.False(row.Read());
        }
        var daily = await Next(alice, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser,
            m => m.ActivityData.TryGetValue(105, out var row) && row.Inc?.TaskCompleteTimes == 1);
        Assert.Equal(1, daily.ActivityData[105].Inc.TaskCompleteTimes);
        var liveness = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.Currency.TryGetValue(5, out var c) && c.All == 4);
        Assert.Equal(4, liveness.Currency[5].All);
        await Next(alice, MvpService.DeadNotify, MVPDeadNotify.Parser);

        // The client re-asks 43009: REBORN with the death time; the report lists the kill.
        await alice.SendGameAsync(MvpService.BriefInfo, new MVPBriefInfoReq());
        var after = await Next(alice, MvpService.BriefInfo, MVPBriefInfoRsp.Parser, r => r.Infos.Any(i => i.CfgId == 1 && i.State == MVP_STATE.Reborn));
        Assert.True(after.Infos.Single(i => i.CfgId == 1).StateTime > 0);
        await alice.SendGameAsync(MvpService.ReportRank, new MVPReportRankReq { CfgId = 1, Tp = MVPRankType.Attack });
        var report = await Next(alice, MvpService.ReportRank, MVPReportRankRsp.Parser);
        Assert.Equal((long)alice.Self!.Uuid, report.Info.EndPlayerId);
        Assert.Single(report.VersionList);
        await alice.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = 100014 });
        await Next(alice, TaskService.TaskFollow, TaskFollowResp.Parser); // wait for committed quest settlement
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);
            Assert.Equal(1, session.Tasks!.Infos[100014].CompleteNum[0]);
            Assert.Equal(1, session.Tasks.Infos[110001].CompleteNum[0]);
            return Task.CompletedTask;
        });
    }

    private sealed class MvpDiagnosticErrors : ILoggerProvider, ILogger
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Warning) Messages.Enqueue(formatter(state, error) + " " + error); }
        public void Dispose() { }
    }

    private async Task<(BotClient Alice, long BossUid)> ReadySoloMvpAsync()
    {
        var alice = await InWorld("Alice");
        try
        {
            await Gm("alice", "!lvl 99");
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Gm("alice", "!mvp where 1");
                var reply = await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
                    && (System.Text.Encoding.UTF8.GetString(e.Body).Contains("MVP 1")
                        || System.Text.Encoding.UTF8.GetString(e.Body).StartsWith("10214 ")), Wait);
                if (System.Text.Encoding.UTF8.GetString(reply.Body).StartsWith("10214 ")) break;
                await Task.Delay(200);
            }
            await Gm("alice", "!mvp goto 1");
            var (_, _, npcs) = await FollowSceneChange(alice);
            var angeling = npcs.Bases.FirstOrDefault(b => b.EntityTemplateID == 5020001)
                ?? (await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                    v => v.Bases.Any(b => b.EntityTemplateID == 5020001)))
                    .Bases.Single(b => b.EntityTemplateID == 5020001);
            long uid = (long)angeling.Uuid;
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
            {
                SureHitRolls.Install(_app);
                var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                var boss = _app.Services.GetRequiredService<World>().Map(player.WorldScene).Units.OfType<MonsterUnit>()
                    .Single(m => m.Uid == uid);
                boss.Advance(MonsterService.NowMs);
                boss.Moving = false;
                boss.NextRoamMs = long.MaxValue;
                await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(player,
                    new MapPoint(boss.X, boss.Y, boss.Z, boss.Face));
                _app.Services.GetRequiredService<MonsterService>().Engage(boss, player);
                boss.NextSwingMs = long.MaxValue;
                // The engaged boss must not chase either: with map ticks no longer deferred behind Realm, a chase step
                // could put it behind a nav edge from the caster (SkillBlocked).
                boss.NextChaseMs = long.MaxValue;
            });
            await Gm("alice", "!mvp weaken 1");
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var boss = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>().Single(m => m.Uid == uid);
                Assert.Equal(1, boss.Hp);
                Assert.False(boss.Returning);
                return Task.CompletedTask;
            });
            return (alice, uid);
        }
        catch { await alice.DisposeAsync(); throw; }
    }

    [ClientDataFact]
    public async Task FieldMvpApplyFaultPublishesOnlyTheCommittedFrozenSource()
    {
        var (alice, uid) = await ReadySoloMvpAsync();
        await using (alice)
        {
            var accounts = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.AccountStore>();
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            accounts.BeforeOwnedApplyForTest = label =>
            {
                if (label != "economy/event/mvp-settlement") return Task.CompletedTask;
                committed.TrySetResult();
                throw new IOException("Injected MVP postcommit apply fault.");
            };
            try
            {
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await committed.Task.WaitAsync(Wait);
                // The MVP source committed, but its Apply fault quarantined the original
                // owner before E04 could admit the separate ordinary reward source.
                await WaitMergedBossAsync(uid, m => m.Hp == 1 && m.StagedLethalDamage > 0);
            }
            finally { accounts.BeforeOwnedApplyForTest = null; }
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                Assert.False(_app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .Single(m => m.Uid == uid).Dead);
                return Task.CompletedTask;
            });
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(MvpService.DeadNotify));
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == uid && d.IsDead));
            using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
            db.Open();
            using var source = db.CreateCommand();
            source.CommandText = "SELECT count(*) FROM mvp_settlement WHERE cfg_id=1";
            Assert.Equal(1, (long)source.ExecuteScalar()!);
            using var receipt = db.CreateCommand();
            receipt.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/mvp-settlement'";
            Assert.Equal(1, (long)receipt.ExecuteScalar()!);
            receipt.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='monster-kill'";
            Assert.Equal(0, (long)receipt.ExecuteScalar()!);
            using var obligation = db.CreateCommand();
            obligation.CommandText = "SELECT count(*) FROM mvp_award_obligation WHERE player_id=$role AND delivered=0";
            obligation.Parameters.AddWithValue("$role", (long)alice.Self!.Uuid);
            Assert.Equal(1, (long)obligation.ExecuteScalar()!); // owner quarantine defers, not rerolls, delivery
        }
    }

    [ClientDataFact]
    public async Task FieldMvpUnknownCommittedSourceReselectsWithoutGenericKillCredit()
    {
        var (alice, uid) = await ReadySoloMvpAsync();
        await using (alice)
        {
            long roleId = (long)alice.Self!.Uuid;
            var roles = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.RoleStore>();
            long Coins() => roles.LoadProgress(roleId).Currency.GetValueOrDefault(2);
            long beforeCoins = Coins();
            var accounts = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.AccountStore>();
            var combat = _app.Services.GetRequiredService<CombatService>();
            var mvp = _app.Services.GetRequiredService<MvpService>();
            var rewards = _app.Services.GetRequiredService<RewardService>();
            object? oldRole = null, oldAttachment = null;
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                oldRole = session.Role;
                oldAttachment = session.Attachment;
                return Task.CompletedTask;
            });
            int genericKills = 0, ordinaryDrops = 0, probes = 0;
            Func<GameSession, MonsterUnit, Task> genericObserver = (_, monster) =>
            {
                if (monster.Uid == uid) Interlocked.Increment(ref genericKills);
                return Task.CompletedTask;
            };
            Func<GameSession, MonsterUnit, IReadOnlyList<(int Entry, long Count)>, Task> dropObserver = (_, monster, _) =>
            {
                if (monster.Uid == uid) Interlocked.Increment(ref ordinaryDrops);
                return Task.CompletedTask;
            };
            var firstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var beforePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<MonsterUnit, Task> afterLethal = monster =>
            {
                if (monster.Uid == uid) finalized.TrySetResult();
                return Task.CompletedTask;
            };
            combat.Killed.Insert(0, genericObserver); // before loot and quest callbacks
            rewards.KillDropsGranted.Add(dropObserver);
            combat.AfterLethalPublication.Add(afterLethal);
            mvp.AfterSettlementCommitForTest = _ => Task.FromException(new IOException("Injected committed MVP acknowledgement fault."));
            mvp.BeforeSettlementProbeForTest = _ =>
            {
                if (Interlocked.Increment(ref probes) != 1) return Task.CompletedTask;
                firstProbe.TrySetResult();
                return Task.FromException(new IOException("Injected one-shot source probe failure."));
            };
            mvp.BeforeRetainedPublicationForTest = _ =>
            {
                beforePublish.TrySetResult();
                return releasePublish.Task;
            };
            try
            {
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await firstProbe.Task.WaitAsync(Wait);
                await beforePublish.Task.WaitAsync(Wait); // same committed source was re-read, without a second roll
                using (var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}"))
                {
                    db.Open();
                    using var source = db.CreateCommand();
                    source.CommandText = "SELECT count(*) FROM mvp_settlement WHERE cfg_id=1";
                    Assert.Equal(1, (long)source.ExecuteScalar()!);
                }

                Assert.Equal((ulong)roleId, (await alice.EnterWorldAsync(roleId)).Uuid); // real same-role handover
                await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
                {
                    var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                    Assert.Equal(roleId, session.Role?.Id);
                    Assert.NotSame(oldAttachment, session.Attachment);
                    Assert.NotNull(oldRole);
                    return Task.CompletedTask;
                });
                var result = await Next(alice, MvpService.ResultNtf, MVPResultNtf.Parser);
                long coins = result.Awards.Where(a => a.ItemId == 2).Sum(a => a.Count);
                Assert.True(coins >= 30000);
                Assert.Equal(beforeCoins + coins, Coins());
                releasePublish.TrySetResult();
                await finalized.Task.WaitAsync(Wait);
                Assert.Equal(0, genericKills); // neither ordinary quest kill nor repeatable world loot ran on the new lease
                Assert.Equal(0, ordinaryDrops);
                using (var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}"))
                {
                    db.Open();
                    using var receipt = db.CreateCommand();
                    receipt.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/mvp-settlement'";
                    Assert.Equal(1, (long)receipt.ExecuteScalar()!);
                    receipt.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/mvp-delivery'";
                    Assert.Equal(1, (long)receipt.ExecuteScalar()!);
                    using var obligation = db.CreateCommand();
                    obligation.CommandText = "SELECT count(*) FROM mvp_award_obligation WHERE player_id=$role AND delivered=1";
                    obligation.Parameters.AddWithValue("$role", roleId);
                    Assert.Equal(1, (long)obligation.ExecuteScalar()!);
                }
                await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
                    mvp.RecoverAsync(_app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice")));
                Assert.Equal(beforeCoins + coins, Coins());
                Assert.Equal(0, genericKills);
            }
            finally
            {
                releasePublish.TrySetResult();
                mvp.AfterSettlementCommitForTest = null;
                mvp.BeforeSettlementProbeForTest = null;
                mvp.BeforeRetainedPublicationForTest = null;
                combat.Killed.Remove(genericObserver);
                combat.AfterLethalPublication.Remove(afterLethal);
                rewards.KillDropsGranted.Remove(dropObserver);
            }
        }
    }

    [ClientDataFact]
    public void NavGridMatchesTheClient()
    {
        var grid = NavGrid.Load(Path.Combine(RepoRoot(), "data", "nav", "sc_prontera_001_dynamicmapdata.bytes"));
        Assert.Equal(300, grid.Width);
        Assert.Equal(395, grid.Height);
        Assert.True(grid.IsWalkable(156.04f, -0.2f, 181.91f));  // born point
        Assert.False(grid.IsWalkable(1f, 0f, 1f));             // map corner, outside the walls
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
