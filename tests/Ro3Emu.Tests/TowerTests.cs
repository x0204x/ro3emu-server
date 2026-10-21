using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Spirit Tower's wire contract and authoritative lifecycle; no original client is run.</summary>
public sealed class TowerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tower-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

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
        });
        // Floors belong to playbook 101; keep this fixture independent of any persisted server playbook.
        SetPlayBook(101);
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

    private async Task<BotClient> InWorld(string name, bool create = true, int cleared = 0)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        // Earned-state fixture for higher floors; real clears and rewards below always use combat and the wire.
        if (cleared > 0) Service<RoleStore>().SaveTower(id, new DBClimTower { LayerConfID = cleared }.ToByteArray());
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task Eligible(BotClient bot, int level = 99)
    {
        await Gm(bot.Account!, $"!lvl {level}");
        await Gm(bot.Account!, "!quest done 10105020");
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    private void SetPlayBook(int id) => Service<SeasonService>().SetPlayBook(id);

    private static async Task<int> StartRequest(BotClient bot, int floor, bool inside = false)
    {
        await bot.SendGameAsync(TowerService.Start, new ClimTowerStartReq { Layer = floor, IsInClimTower = inside });
        return (await Next(bot, TowerService.Start, ClimTowerStartRes.Parser)).Result;
    }

    private static async Task<(SwitchSceneLineResp Scene, DungeonBaseInfoNotify Dungeon, EntityBase[] Monsters)> EnterFloor(BotClient bot, int floor, bool inside = false)
    {
        Assert.Equal(0, await StartRequest(bot, floor, inside));
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(2100001, sw.SceneEntry);
        Assert.NotEqual(sw.SceneEntry, sw.SceneID);
        Assert.Equal(4, (int)sw.Reason);
        await bot.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
        await Next(bot, MsgId.PlayerAsset, PlayerAssetResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.InRange(self.Base.MovementInfo.Pos.X, 127.9f, 128.1f);
        Assert.InRange(self.Base.MovementInfo.Pos.Z, 149.7f, 150.1f);
        var dungeon = await Next(bot, TowerService.DungeonInfo, DungeonBaseInfoNotify.Parser);
        Assert.Equal(sw.SceneID, dungeon.DungeonId);
        Assert.Equal(floor, dungeon.InitData.ClimbTowerInit.LayerConfID);
        Assert.Equal(new[] { (long)bot.Self!.Uuid }, Assert.Single(dungeon.Vars).Vi64);
        Assert.True(dungeon.StageModuleData.StageEndTime > dungeon.StageModuleData.StageStartTime);
        // Ignore retained world/previous-attempt AOI events in the bot's inbox.
        var aoi = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityType == 1 && b.BornTimeMs >= dungeon.StageModuleData.StageStartTime));
        return (sw, dungeon, aoi.Bases.Where(b => b.EntityType == 1 && b.BornTimeMs >= dungeon.StageModuleData.StageStartTime).ToArray());
    }

    private async Task Kill(BotClient bot, ulong uid)
    {
        // Under load the monster can step away (roam or chase) between reading its position and the cast reaching
        // the loop; the server then answers SkillDistance. Freeze its roaming and re-approach instead of failing.
        for (int attempt = 1; ; attempt++)
        {
            Vec3 position = null!;
            await OnLoop(() =>
            {
                var monster = Service<World>().AllUnits().OfType<MonsterUnit>().Single(m => m.Uid == (long)uid);
                // Controlled combat fixture: shorten the fight after asserting the configured spawn HP.
                // The ordinary 31102 cast and shared damage/death callbacks still determine the outcome.
                monster.Hp = 1;
                monster.Advance(MonsterService.NowMs);
                monster.Moving = false;
                monster.NextRoamMs = long.MaxValue;
                position = monster.Pos;
                return Task.CompletedTask;
            });
            await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)uid });
            int result = (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
            if (result == ErrorCodes.SkillDistance && attempt < 3) continue;
            Assert.Equal(0, result);
            break;
        }
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == (long)uid && d.IsDead));
    }

    private long Count(BotClient bot, int entry) => Service<ItemStore>().Load((long)bot.Self!.Uuid).Where(i => i.Entry == entry).Sum(i => (long)i.Stack);

    private void FailTowerWrites(bool enabled)
    {
        FixtureSql.Execute(Service<AccountStore>(), enabled
            ? "CREATE TRIGGER fail_tower BEFORE UPDATE OF tower ON role BEGIN SELECT RAISE(ABORT, 'tower test failure'); END"
            : "DROP TRIGGER fail_tower");
    }

    [ClientDataFact]
    public async Task NextFloorDifficultyUsesFiveAmBoundaryAndScalesOnlyPrivateActors()
    {
        var floor = Service<GameData>().Row("PetFloorConfig", 1)!.Value;
        var before = new DateTimeOffset(2026, 10, 8, 20, 59, 0, TimeSpan.Zero); // 04:59 at the client's UTC+8 offset
        long challenge = before.ToUnixTimeMilliseconds();
        Assert.Equal(10000, TowerService.DifficultyRatio(floor, 1011, challenge, before));
        Assert.Equal(9900, TowerService.DifficultyRatio(floor, 1011, challenge, before.AddMinutes(1)));
        Assert.Equal(9900, TowerService.DifficultyRatio(floor, 215, challenge, before.AddMinutes(1)));
        Assert.Equal(9000, TowerService.DifficultyRatio(floor, 1011, challenge, before.AddDays(11)));
        Assert.Equal(10000, TowerService.DifficultyRatio(floor, 1011, challenge, before.AddDays(-1)));

        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        await OnLoop(() =>
        {
            Session(alice).Tower!.ChallengeTime = DateTimeOffset.UtcNow.AddDays(-15).ToUnixTimeMilliseconds();
            return Task.CompletedTask;
        });
        var entered = await EnterFloor(alice, 1);
        Assert.Equal(2, entered.Monsters.Length);
        Assert.All(entered.Monsters, m => Assert.Equal(1080, m.Property.Single(p => p.Idx == 101).Data));
        await OnLoop(() =>
        {
            Assert.Equal(1200, Service<MonsterService>().Template(24000001)!.MaxHp);
            Assert.All(Service<World>().AllUnits().OfType<MonsterUnit>().Where(m => m.InstanceId == entered.Scene.SceneID), m =>
            {
                Assert.Equal(1080, m.Template.MaxHp);
                Assert.Equal(9000, m.DamageRatio);
            });
            return Task.CompletedTask;
        });
        Assert.Equal(225, CombatService.ScaleMonsterDamage(250, 9000));
        Assert.Equal(0, CombatService.ScaleMonsterDamage(0, 9000));
        await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task ATeamBelowTheRecommendedPowerMeetsASuppressedFloor()
    {
        // PowerSuppressConfig: a power under 80% of floor 1's 6000 puts 5000001 (215/216 -10%) on its monsters.
        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        var entered = await EnterFloor(alice, 1);
        Assert.Equal(2, entered.Monsters.Length);
        Assert.All(entered.Monsters, m => Assert.Equal(1200, m.Property.Single(p => p.Idx == 101).Data));
        var tower = Service<TowerService>();
        var buffs = Service<BuffService>();
        MonsterUnit[] floor = [];
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(50))
        {
            bool ready = false;
            await OnLoop(() =>
            {
                floor = Service<World>().AllUnits().OfType<MonsterUnit>().Where(m => m.InstanceId == entered.Scene.SceneID).ToArray();
                ready = floor.Length == 2 && floor.All(m => buffs.UnitAttributes(m).Any(a => a.Id == 215));
                return Task.CompletedTask;
            });
            if (ready) break;
            Assert.True(DateTime.UtcNow < until, "suppression buff not applied");
        }
        Assert.Equal(new TowerService.Suppression(5000001, -1000, -1000), Session(alice).TowerAttempt!.Suppressed);
        Assert.All(floor, m =>
        {
            Assert.Equal(10000, m.DamageRatio);
            Assert.Equal(900, tower.SuppressDealt(m, 1000));
            Assert.Equal(1100, tower.SuppressTaken(m, 1000));
        });
        await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
    }

    [Fact]
    public async Task FloorsFromAnotherPlayBookCannotBeStartedOrClaimed()
    {
        foreach (var (array, accepted) in new[] { ("[]", true), ("[0]", true), ("[101]", false), ("[101,102]", true), ("[101,-1]", true), ("[103,-1]", false) })
        {
            using var row = JsonDocument.Parse("{\"_kMultiverseArray\":" + array + "}");
            Assert.Equal(accepted, TowerService.FitsPlayBook(row.RootElement, 102));
        }
        SetPlayBook(102);
        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        Assert.Equal(ErrorCodes.Parameter, await StartRequest(alice, 1));
        await alice.SendGameAsync(TowerService.FirstPassRange, new QueryClimbTowerFirstPassRangeReq { MinLayer = 1, MaxLayer = 230 });
        var range = await Next(alice, TowerService.FirstPassRange, QueryClimbTowerFirstPassRangeResp.Parser);
        Assert.Equal(ErrorCodes.Parameter, range.Result);
        Assert.Empty(range.Results);
        await alice.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        Assert.NotEqual(0, (await Next(alice, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        Assert.Equal(0, Count(alice, 1250709));
        Assert.Null(Session(alice).TowerAttempt);
    }

    [ClientDataFact]
    public async Task LoginAndInfoHaveFourSlotsAndStoryAndLevelGatesAreIndependent()
    {
        await using var alice = await InWorld("Alice");
        var tower = await Next(alice, TowerService.TowerSync, DBClimTowerIncrement.Parser);
        Assert.True(tower.SyncAllMark);
        Assert.Equal(new[] { 0, 0, 0, 0 }, tower.PetIds.All.Value);
        Assert.Equal(0, tower.LayerConfID);
        Assert.False(tower.IsCanSweep);
        Assert.True(tower.ChallengeTime > 0);
        await alice.SendGameAsync(TowerService.Info, new ClimTowerInfoReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, TowerService.Info, ClimTowerInfoRsp.Parser)).Result);
        await Gm("alice", "!lvl 33");
        Assert.Equal(ErrorCodes.FeatureUnavailable, await StartRequest(alice, 1));
        await Gm("alice", "!quest done 10105020");
        await Gm("alice", "!lvl 32");
        Assert.Equal(ErrorCodes.FeatureUnavailable, await StartRequest(alice, 1));
        await Gm("alice", "!lvl 33");
        await alice.SendGameAsync(TowerService.Info, new ClimTowerInfoReq());
        var info = await Next(alice, TowerService.Info, ClimTowerInfoRsp.Parser);
        Assert.Equal(0, info.Result);
        Assert.Equal(new[] { 0, 0, 0, 0 }, info.PetIds);
        Assert.Equal(ErrorCodes.Parameter, await StartRequest(alice, 2));
        Assert.Equal(ErrorCodes.Parameter, await StartRequest(alice, 99999));
        Assert.Equal(ErrorCodes.Parameter, await StartRequest(alice, 1, inside: true));
        await alice.SendGameAsync(TowerService.Sweep, new ClimTowerSweepReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, TowerService.Sweep, ClimTowerSweepRes.Parser)).Result);
    }

    [ClientDataFact]
    public async Task FloorOneHandsOffSpawnsTwoMonstersSettlesAndPaysOnlyOnceAcrossRelog()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Eligible(alice);
            var entered = await EnterFloor(alice, 1);
            Assert.Equal(2, entered.Monsters.Length);
            Assert.All(entered.Monsters, m =>
            {
                Assert.Equal(24000001UL, m.EntityTemplateID);
                Assert.Equal(1200, m.Property.Single(p => p.Idx == 101).Data);
            });
            Assert.Equal(new[] { 122.91f, 133.3f }, entered.Monsters.Select(m => m.MovementInfo.Pos.X).Order().ToArray());
            Assert.Equal(ErrorCodes.Parameter, await StartRequest(alice, 1, inside: true));
            foreach (var monster in entered.Monsters) await Kill(alice, monster.Uuid);
            var end = await Next(alice, TowerService.End, ClimTowerEndNotify.Parser);
            Assert.True(end.ClimTowerResult);
            Assert.True(end.IsNewRecord);
            Assert.Equal((1, 1L), (end.Layer, end.PassedMaxLayerConfID));
            Assert.Equal(new[] { (1250701, 9), (1250709, 5) }, end.RewardNotify.Rewards.OrderBy(r => r.ItemEntry).Select(r => (r.ItemEntry, r.Stack)));
            Assert.All(end.RewardNotify.Rewards, r => { Assert.True(r.ItemGuid > 0); Assert.True(r.ItemInfos.ContainsKey(10001)); });
            var update = await Next(alice, TowerService.TowerSync, DBClimTowerIncrement.Parser, t => !t.SyncAllMark && t.LayerConfID == 1);
            Assert.Equal(60 - end.Duration, update.RemainDuration);
            Assert.Equal(end.CurTime, update.CompleteTime);
            Assert.Equal(5, Count(alice, 1250709));
            Assert.Equal(9, Count(alice, 1250701));
            var packets = alice.Inbox.Snapshot().ToList();
            Assert.True(packets.FindIndex(e => e.IsGame(TowerService.End)) < packets.FindIndex(e => e.IsGame(TowerService.TowerSync) && !DBClimTowerIncrement.Parser.ParseFrom(e.Body).SyncAllMark));

            var replay = await EnterFloor(alice, 1, inside: true);
            Assert.NotEqual(entered.Scene.SceneID, replay.Scene.SceneID);
            foreach (var monster in replay.Monsters) await Kill(alice, monster.Uuid);
            var again = await Next(alice, TowerService.End, ClimTowerEndNotify.Parser);
            Assert.True(again.ClimTowerResult);
            Assert.False(again.IsNewRecord);
            Assert.Empty(again.RewardNotify.Rewards);
            Assert.Equal(5, Count(alice, 1250709));
            await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(alice, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(RoleStore.SpawnSceneEntry, sw.SceneEntry);
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await WaitOffline(role);
        await using var relog = await InWorld("Alice", create: false);
        var saved = await Next(relog, TowerService.TowerSync, DBClimTowerIncrement.Parser);
        Assert.Equal(1, saved.LayerConfID);
        Assert.True(saved.RewardRecord[1].All);
        Assert.Equal(5, Count(relog, 1250709));
        Assert.Equal(9, Count(relog, 1250701));
    }

    [ClientDataFact]
    public async Task FirstPassRangesAreInclusiveBoundedAndNeverFabricateWinners()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(TowerService.FirstPassRange, new QueryClimbTowerFirstPassRangeReq { MinLayer = 1, MaxLayer = 230 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, TowerService.FirstPassRange, QueryClimbTowerFirstPassRangeResp.Parser)).Result);
        await Eligible(alice);
        await alice.SendGameAsync(TowerService.FirstPassRange, new QueryClimbTowerFirstPassRangeReq { MinLayer = 1, MaxLayer = 230 });
        var range = await Next(alice, TowerService.FirstPassRange, QueryClimbTowerFirstPassRangeResp.Parser);
        Assert.Equal(230, range.Results.Count);
        Assert.Equal(Enumerable.Range(1, 230).Select(i => (long)i), range.Results.Select(r => r.Layer));
        Assert.All(range.Results, r => { Assert.False(r.HasFirstPass); Assert.Equal(0, r.PlayerId); });
        foreach (var (min, max) in new[] { (0L, 1L), (11L, 10L), (1L, long.MaxValue), (230L, 231L) })
        {
            await alice.SendGameAsync(TowerService.FirstPassRange, new QueryClimbTowerFirstPassRangeReq { MinLayer = min, MaxLayer = max });
            Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TowerService.FirstPassRange, QueryClimbTowerFirstPassRangeResp.Parser)).Result);
        }
        await alice.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        Assert.Equal(0, Count(alice, 1250709));
    }

    [ClientDataFact]
    public async Task BossFirstPassBelongsToFirstWinnerAndOtherPlayersClaimItsMilestoneOnce()
    {
        await using var winner = await InWorld("Winner", cleared: 9);
        await using var other = await InWorld("Other");
        await Eligible(winner);
        await Eligible(other);
        var entered = await EnterFloor(winner, 10);
        await Kill(winner, Assert.Single(entered.Monsters).Uuid);
        Assert.True((await Next(winner, TowerService.End, ClimTowerEndNotify.Parser)).ClimTowerResult);
        await other.SendGameAsync(TowerService.FirstPassRange, new QueryClimbTowerFirstPassRangeReq { MinLayer = 10, MaxLayer = 10 });
        var record = Assert.Single((await Next(other, TowerService.FirstPassRange, QueryClimbTowerFirstPassRangeResp.Parser)).Results);
        Assert.True(record.HasFirstPass);
        Assert.Equal((long)winner.Self!.Uuid, record.PlayerId);
        Assert.Equal("Winner", record.PlayerName);
        Assert.True(record.PassTime > 0);
        // A database failure after the inventory write rolls back the grant and the claim marker together.
        FailTowerWrites(true);
        await other.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        Assert.Equal(ErrorCodes.Unknown, (await Next(other, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        Assert.Equal(0, Count(other, 1250709));
        Assert.Equal(0, Session(other).Tower!.ServerFirstPassClaimedLayer);
        FailTowerWrites(false);
        await other.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        var claim = await Next(other, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser);
        Assert.Equal((0, 10L), (claim.Result, claim.ClaimedUpToLayer));
        Assert.Equal(10, Count(other, 1250709));
        Assert.Equal(0, Session(other).Tower!.LayerConfID); // honor is independent of personal clears
        await other.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        Assert.Equal(ErrorCodes.Parameter, (await Next(other, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        Assert.Equal(10, Count(other, 1250709));
        // Replaying the floor cannot replace the recorded first pass.
        var replay = await EnterFloor(winner, 10, inside: true);
        await Kill(winner, Assert.Single(replay.Monsters).Uuid);
        await Next(winner, TowerService.End, ClimTowerEndNotify.Parser);
        var durable = Service<RoleStore>().LoadTowerFirstPasses(10, 10);
        Assert.Equal(record, ClimbTowerFirstPassData.Parser.ParseFrom(durable[10]));
        Assert.Equal(10, DBClimTower.Parser.ParseFrom(Service<RoleStore>().LoadTower((long)other.Self!.Uuid)!).ServerFirstPassClaimedLayer);
    }

    [ClientDataFact]
    public async Task PrivateFloorsRejectForeignTargetsAndForgedResultPackets()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Eligible(alice);
        await Eligible(bob);
        var a = await EnterFloor(alice, 1);
        var b = await EnterFloor(bob, 1);
        Assert.NotEqual(a.Scene.SceneID, b.Scene.SceneID);
        await OnLoop(() =>
        {
            Assert.DoesNotContain(Session(bob), Service<World>().Viewers(Session(alice)));
            Assert.Equal(2, Session(alice).VisibleUnits.Count);
            Assert.Equal(2, Session(bob).VisibleUnits.Count);
            return Task.CompletedTask;
        });
        await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)a.Monsters[0].Uuid });
        Assert.NotEqual(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(TowerService.End, new ClimTowerEndNotify { Layer = 230, ClimTowerResult = true, PassedMaxLayerConfID = 230 });
        await alice.SendGameAsync(TowerService.Info, new ClimTowerInfoReq());
        Assert.Equal(0, (await Next(alice, TowerService.Info, ClimTowerInfoRsp.Parser)).PassedMaxLayerConfID);
        Assert.Equal(0, Count(alice, 1250709));
        foreach (var monster in a.Monsters) await Kill(alice, monster.Uuid);
        Assert.True((await Next(alice, TowerService.End, ClimTowerEndNotify.Parser)).ClimTowerResult);
        Assert.False(Session(bob).TowerAttempt!.Settled);
        Assert.Equal(0, Count(bob, 1250709));
    }

    [ClientDataFact]
    public async Task WrongOrRepeatedLoadedPacketsCannotStartOrRespawnTheFloor()
    {
        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        Assert.Equal(0, await StartRequest(alice, 1));
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(ErrorCodes.SwitchingScenes, await StartRequest(alice, 1));
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = RoleStore.SpawnSceneEntry });
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        Assert.False(Session(alice).TowerAttempt!.Loaded);
        Assert.Empty(Session(alice).TowerAttempt!.Monsters);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 2100001 });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, TowerService.DungeonInfo, DungeonBaseInfoNotify.Parser);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.EntityTemplateID == 24000001));
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 2100001 });
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        Assert.Equal(2, Session(alice).TowerAttempt!.Monsters.Count);
    }

    [ClientDataFact]
    public async Task DeadlineBeatsTheLastKillAndDoesNotAdvanceOrPay()
    {
        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        var a = await EnterFloor(alice, 1);
        await OnLoop(() => { Session(alice).TowerAttempt!.StartedAt -= TimeSpan.FromSeconds(60); return Task.CompletedTask; });
        await Kill(alice, a.Monsters[0].Uuid);
        var end = await Next(alice, TowerService.End, ClimTowerEndNotify.Parser);
        Assert.False(end.ClimTowerResult);
        Assert.Equal(60, end.Duration);
        Assert.Equal(0, end.PassedMaxLayerConfID);
        Assert.False(end.IsNewRecord);
        Assert.Empty(end.RewardNotify.Rewards);
        Assert.Equal(0, Count(alice, 1250709));
        Assert.Equal(0, DBClimTower.Parser.ParseFrom(Service<RoleStore>().LoadTower((long)alice.Self!.Uuid)!).LayerConfID);
    }

    [ClientDataFact]
    public async Task FailedClearWriteRollsBackEveryRewardAndAllowsOneSuccessfulRetry()
    {
        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        await Gm("alice", "!item 1250709 4");
        var a = await EnterFloor(alice, 1);
        await Kill(alice, a.Monsters[0].Uuid);
        MonsterUnit final = null!;
        Vec3 position = null!;
        await OnLoop(() =>
        {
            final = Service<World>().AllUnits().OfType<MonsterUnit>().Single(m => m.Uid == (long)a.Monsters[1].Uuid);
            final.Hp = 1;
            position = final.Pos;
            return Task.CompletedTask;
        });
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<GameSession, MonsterUnit, Task> stageNotice = (_, monster) =>
        {
            if (ReferenceEquals(monster, final)) staged.TrySetResult();
            return Task.CompletedTask;
        };
        var hooks = Service<CombatService>().BeforeKilled;
        hooks.Insert(hooks.FindIndex(h => h.Target is RewardService), stageNotice);
        FailTowerWrites(true);
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = final.Uid });
        await staged.Task.WaitAsync(Wait);
        await Task.Delay(200); // Let the refused write return before stopping automatic repeat casts.
        await OnLoop(() => { Session(alice).Attack = null; return Task.CompletedTask; });
        hooks.Remove(stageNotice);
        Assert.False(final.Dead);
        Assert.Equal(1, final.Hp);
        Assert.False(await Service<CharacterService>().RewardSourceCommittedAsync(final.RewardSource!.Key));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(packet.Body).Dsts.Any(hit => hit.DstID == final.Uid && hit.IsDead));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(TowerService.End));
        Assert.Equal(4, Count(alice, 1250709));
        Assert.Equal(0, Count(alice, 1250701));
        Assert.Equal(4, Session(alice).Items.Where(i => i.Entry == 1250709).Sum(i => i.Stack));
        Assert.Equal(0, Session(alice).Tower!.LayerConfID);
        Assert.Empty(Session(alice).Tower!.RewardRecord);
        Assert.Empty(Service<RoleStore>().LoadTowerFirstPasses(1, 1));
        FailTowerWrites(false);
        await Kill(alice, a.Monsters[1].Uuid);
        var success = await Next(alice, TowerService.End, ClimTowerEndNotify.Parser);
        Assert.Equal(0, success.Result);
        Assert.True(success.ClimTowerResult);
        Assert.Equal(9, Count(alice, 1250709));
        Assert.Equal(9, Count(alice, 1250701));
        Assert.Equal(5, success.RewardNotify.Rewards.Single(i => i.ItemEntry == 1250709).Stack);
    }

    [ClientDataFact]
    public async Task CommittedClearWithApplyFaultRequiresRestartAndReloadsOneAward()
    {
        long role, room;
        TowerService.Attempt attempt = null!;
        MonsterUnit final = null!;
        int applyFaults = 0;
        await using (var alice = await InWorld("Alice"))
        {
            await Eligible(alice);
            role = (long)alice.Self!.Uuid;
            var entered = await EnterFloor(alice, 1);
            room = entered.Scene.SceneID;
            await Kill(alice, entered.Monsters[0].Uuid);
            Vec3 position = null!;
            await OnLoop(() =>
            {
                attempt = Session(alice).TowerAttempt!;
                final = Service<World>().AllUnits().OfType<MonsterUnit>()
                    .Single(m => m.Uid == (long)entered.Monsters[1].Uuid);
                final.Hp = 1;
                position = final.Pos;
                return Task.CompletedTask;
            });
            var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<GameSession, MonsterUnit, Task> failApply = (_, monster) =>
            {
                if (ReferenceEquals(monster, final))
                {
                    var settlement = Assert.IsType<RewardSettlement>(monster.RewardSettlement);
                    var apply = Assert.IsType<Action>(settlement.Applied);
                    staged.TrySetResult();
                    settlement.Applied = () =>
                    {
                        apply();
                        applyFaults++;
                        faulted.TrySetResult();
                        throw new IOException("tower postcommit apply fault");
                    };
                }
                return Task.CompletedTask;
            };
            var hooks = Service<CombatService>().BeforeKilled;
            hooks.Insert(hooks.FindIndex(h => h.Target is RewardService), failApply);
            try
            {
                await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
                await alice.SendGameAsync(CombatService.UseSkill,
                    new CastSkillReq { SkillId = 1000001, TargetId = final.Uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await staged.Task.WaitAsync(Wait);
                await faulted.Task.WaitAsync(Wait);
                // The child reward permit aborts first; the parent cast then finalizes the paid death.
                for (int i = 0; i < 100 && (!Session(alice).Channel.PeerClosed || !final.Dead); i++) await Task.Delay(20);
                Assert.True(Session(alice).Channel.PeerClosed);
            }
            finally { hooks.Remove(failApply); }
            Assert.Equal(1, applyFaults);
            Assert.True(final.Dead);
            Assert.True(final.RewardSettlement!.Committed);
            Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(final.RewardSettlement.SourceKey));
            Assert.True(attempt.ClearCommitted);
            Assert.DoesNotContain(final.Uid, attempt.Killed);
            Assert.Equal(RoleOwnerState.RecoveryRequired, Session(alice).Owner!.State);
            Assert.Equal(1, DBClimTower.Parser.ParseFrom(Service<RoleStore>().LoadTower(role)!).LayerConfID);
            Assert.Equal(5, Count(alice, 1250709));
            Assert.Equal(9, Count(alice, 1250701));
            Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(TowerService.End));
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Null(Service<InstanceService>().Find(room));
        Assert.Null(Session(recovered).TowerAttempt);
        Assert.Equal(1, Session(recovered).Tower!.LayerConfID);
        Assert.True(Session(recovered).Tower!.RewardRecord[1]);
        Assert.Equal(5, Count(recovered, 1250709));
        Assert.Equal(9, Count(recovered, 1250701));
    }

    [ClientDataFact]
    public async Task FinalTwoFloorMonstersInOneAreaHitCommitOneClear()
    {
        await using var alice = await InWorld("Alice");
        await Eligible(alice);
        await Gm("alice", "!job 1100");
        await OnLoop(() => { Session(alice).Progress!.Skills[11002] = 1; return Task.CompletedTask; });
        var entered = await EnterFloor(alice, 1);
        Assert.Equal(2, entered.Monsters.Length);
        MonsterUnit[] actors = [];
        await OnLoop(async () =>
        {
            actors = Service<World>().AllUnits().OfType<MonsterUnit>()
                .Where(m => m.InstanceId == entered.Scene.SceneID).ToArray();
            Assert.Equal(2, actors.Length);
            actors[0].Moving = actors[1].Moving = false;
            actors[1].X = actors[0].X;
            actors[1].Y = actors[0].Y;
            actors[1].Z = actors[0].Z;
            Service<World>().Map(actors[1].WorldScene).Moved(actors[1]);
            await Service<SceneService>().TeleportInMapAsync(Session(alice),
                new MapPoint(actors[0].X + 1, actors[0].Y, actors[0].Z, actors[0].Face));
            foreach (var actor in actors)
            {
                Service<MonsterService>().Engage(actor, Session(alice));
                actor.Hp = 1;
            }
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            packet => actors.All(actor => packet.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead)));
        Assert.Equal(2, hit.Dsts.Count(d => actors.Any(actor => d.DstID == actor.Uid && d.IsDead)));
        var end = await Next(alice, TowerService.End, ClimTowerEndNotify.Parser);
        Assert.True(end.ClimTowerResult);
        Assert.Single(alice.Inbox.Snapshot(), packet => packet.IsGame(TowerService.End));
        Assert.Equal(5, Count(alice, 1250709));
        Assert.Equal(9, Count(alice, 1250701));
    }

    [ClientDataFact]
    public async Task AuthoritativePlayerDeathLosesOnceWithoutProgressOrReward()
    {
        await using var alice = await InWorld("Alice");
        await Eligible(alice, level: 33);
        var a = await EnterFloor(alice, 1);
        await OnLoop(() => Service<CharacterService>().SetHpSpAsync(Session(alice), 1, null));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = a.Monsters[0].MovementInfo.Pos, BStop = true, SpeedRate = 1 });
        // Let the spawned monster's normal AI/skill land the fatal hit, rather than calling the settlement hook.
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.Dsts.Any(d => d.DstID == (long)alice.Self!.Uuid && d.IsDead));
        var end = await Next(alice, TowerService.End, ClimTowerEndNotify.Parser);
        Assert.Equal(0, end.Result);
        Assert.False(end.ClimTowerResult);
        Assert.False(end.IsNewRecord);
        Assert.Equal(0, end.PassedMaxLayerConfID);
        Assert.Empty(end.RewardNotify.Rewards);
        Assert.True(Session(alice).Dead);
        Assert.Equal(0, Count(alice, 1250709));
        Assert.Empty(Session(alice).Tower!.RewardRecord);
        await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await Task.Delay(1200); // pending normal attacks cannot deliver a second result after leaving
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(TowerService.End));
        Assert.Null(Session(alice).TowerAttempt);
    }

    [ClientDataFact]
    public async Task EveryWaveOccurrenceSurvivesExpansionAndExitCancelsTheRest()
    {
        var data = Service<GameData>();
        var map = Service<WorldData>().Map(2100001)!;
        var spawns = Service<TowerService>().Expand(data.Row("PetFloorConfig", 101)!.Value, map)!;
        Assert.Equal(new[] { 0, 8000, 15000 }, spawns.Select(s => s.DelayMs).Distinct().Order());
        Assert.Equal(2, spawns.Count(s => s.Point.X == 128 && s.Point.Z == 163.43f)); // group53 at two times
        await using var alice = await InWorld("Alice", cleared: 5);
        await Eligible(alice);
        var a = await EnterFloor(alice, 6);
        Assert.Single(a.Monsters);
        Assert.Equal(4, Session(alice).TowerAttempt!.Spawns.Count);
        await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        await Next(alice, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser);
        var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await Task.Delay(3200); // all three delayed waves are due; none may resurrect the abandoned attempt
        await OnLoop(() =>
        {
            Assert.Null(Session(alice).TowerAttempt);
            Assert.DoesNotContain(Service<World>().AllUnits(), u => u.InstanceId == a.Scene.SceneID);
            return Task.CompletedTask;
        });
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(TowerService.End));
        Assert.Equal(0, Count(alice, 1250709));
    }

    [ClientDataFact]
    public async Task DisconnectDuringTransferReturnsToWorldWithoutAReward()
    {
        long role, instance;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Eligible(alice);
            Assert.Equal(0, await StartRequest(alice, 1));
            instance = (await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneID;
        }
        await WaitOffline(role);
        await using var relog = await InWorld("Alice", create: false);
        Assert.Equal(RoleStore.SpawnSceneEntry, Session(relog).SceneEntry);
        Assert.Equal(0, Session(relog).InstanceId);
        Assert.Null(Session(relog).TowerAttempt);
        Assert.Equal(0, Count(relog, 1250709));
        await OnLoop(() => { Assert.DoesNotContain(Service<World>().AllUnits(), u => u.InstanceId == instance); return Task.CompletedTask; });
    }

    private async Task WaitOffline(long role)
    {
        for (int i = 0; i < 100; i++)
        {
            bool offline = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<SessionRegistry>().All().All(s => s.Role?.Id != role)));
            if (offline) return;
            await Task.Delay(20);
        }
        Assert.Fail("role did not disconnect");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    /// <summary>
    /// 101007 CheckFinishPetFloor and 101010 CheckWinPetFloor count settled floor attempts and won ones; 104008
    /// CheckPetFloor reads the highest cleared floor. The counts survive a relogin.
    /// </summary>
    [ClientDataFact]
    public async Task FloorAttemptsAndWinsMoveTheTowerQuests()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Eligible(alice);
            await QuestProbe.AcceptAsync(_app, _http, "alice", 306040); // [101007,1] "Complete Pet Tower once"
            await QuestProbe.AcceptAsync(_app, _http, "alice", 110005); // [101010,10] "Win Pet Tower a total of 10 times"
            await QuestProbe.AcceptAsync(_app, _http, "alice", 100025); // [104008,3] "Clear Floor 3 of the Pet Tower"
            await QuestProbe.AtAsync(_app, "alice", 100025, 0);
            var entered = await EnterFloor(alice, 1);
            foreach (var monster in entered.Monsters) await Kill(alice, monster.Uuid);
            Assert.True((await Next(alice, TowerService.End, ClimTowerEndNotify.Parser)).ClimTowerResult);
            await QuestProbe.DoneAsync(_app, "alice", 306040);
            await QuestProbe.AtAsync(_app, "alice", 110005, 1);
            await QuestProbe.AtAsync(_app, "alice", 100025, 1);
            await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(alice, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
        }
        await WaitOffline(role);
        await using var relog = await InWorld("Alice", create: false);
        await QuestProbe.DoneAsync(_app, "alice", 306040);
        await QuestProbe.AtAsync(_app, "alice", 110005, 1);
        await QuestProbe.AtAsync(_app, "alice", 100025, 1);
    }
}
