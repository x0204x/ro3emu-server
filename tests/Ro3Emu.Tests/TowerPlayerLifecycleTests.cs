using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Tower-only return recovery across the real 30402/70010/30005 wire path.</summary>
public sealed class TowerPlayerLifecycleTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tower-return-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);

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
        Service<SeasonService>().SetPlayBook(101);
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

    private async Task<BotClient> InWorld()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent("!quest done 10105020"))).EnsureSuccessStatusCode();
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    private async Task<(long Instance, EntityBase[] Monsters)> EnterFloor(BotClient bot, int floor)
    {
        await bot.SendGameAsync(TowerService.Start, new ClimTowerStartReq { Layer = floor });
        Assert.Equal(0, (await Next(bot, TowerService.Start, ClimTowerStartRes.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(2100001, sw.SceneEntry);
        await bot.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
        await Next(bot, MsgId.PlayerAsset, PlayerAssetResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        var dungeon = await Next(bot, TowerService.DungeonInfo, DungeonBaseInfoNotify.Parser);
        Assert.Equal(sw.SceneID, dungeon.DungeonId);
        var aoi = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityType == 1 && b.BornTimeMs >= dungeon.StageModuleData.StageStartTime));
        return (sw.SceneID, aoi.Bases.Where(b => b.EntityType == 1 && b.BornTimeMs >= dungeon.StageModuleData.StageStartTime).ToArray());
    }

    private async Task<EntitySelf> ReturnToWorld(BotClient bot)
    {
        await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(RoleStore.SpawnSceneEntry, sw.SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        return await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    private async Task Kill(BotClient bot, ulong uid)
    {
        Vec3 position = null!;
        await OnLoop(() =>
        {
            var monster = Service<World>().AllUnits().OfType<MonsterUnit>().Single(m => m.Uid == (long)uid);
            monster.Hp = 1;
            // Keep this controlled target in range between the position snapshot and wire cast.
            // Cancel the old segment; the test-only slow bounds new AI movement to 1 cm/s.
            monster.Moving = false;
            monster.SlowMovementBp = 10000;
            monster.SlowMovementExpireMs = long.MaxValue;
            position = monster.Pos;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == (long)uid && d.IsDead));
    }

    [ClientDataFact]
    public async Task FatalTowerFailureWaitsForPublicationThenReturnsAliveAtFullHp()
    {
        await using var bot = await InWorld();
        var floor = await EnterFloor(bot, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            var totem = new InvItem
            {
                Guid = 990001, Entry = 17300001, Stack = 1, Pack = 4,
                Attrs = new Dictionary<int, long> { [3] = 1, [240] = 10051001, [241] = 10053005 },
            };
            Session.Items.Add(totem);
            Session.Totems!.TotemPlans[0].SetUpTotem[1] = totem.Guid;
            await Service<TotemEffects>().RefreshAsync(Session);
            Assert.Contains(Session.Buffs, b => b.IsPassive && b.SourceId == totem.Guid && b.ConfigId == 2500201);
            await Service<CharacterService>().SetHpSpAsync(Session, 1, 1);
            Service<CombatService>().PlayerDied.Add(async player =>
            {
                if (player != Session) return;
                entered.TrySetResult();
                await release.Task;
            });
        });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = floor.Monsters[0].MovementInfo.Pos, BStop = true, SpeedRate = 1 });
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.Dsts.Any(d => d.DstID == (long)bot.Self!.Uuid && d.IsDead));
        var failed = await Next(bot, TowerService.End, ClimTowerEndNotify.Parser);
        Assert.False(failed.ClimTowerResult);
        await entered.Task.WaitAsync(Wait);
        Assert.True(Session.Dead);
        Assert.Equal(0, Service<CharacterService>().Stats(Session)[2000]);
        try
        {
            // A stale attempt with the wrong room identity must not become a free field revive.
            await OnLoop(() => { Session.InstanceId = 0; return Task.CompletedTask; });
            await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
            Assert.Equal(ErrorCodes.SceneMissing,
                (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
            Assert.True(Session.Dead);
            Assert.Equal(0, Service<CharacterService>().Stats(Session)[2000]);
            await OnLoop(() => { Session.InstanceId = floor.Instance; return Task.CompletedTask; });

            await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(RoleStore.SpawnSceneEntry, sw.SceneEntry);
            int priorSelf = bot.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf));
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Task.Delay(100);
            Assert.Equal(priorSelf, bot.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf)));
            Assert.True(Session.Dead);
            Assert.DoesNotContain(Session.Buffs, b => b.IsTotemOwned && b.ConfigId == 2500201);
        }
        finally { release.TrySetResult(); }

        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        long maxHp = self.Base.Property.Single(p => p.Idx == 101).Data;
        Assert.Equal(EntityState.Alive, self.Base.State);
        Assert.Equal(maxHp, self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.Equal(self.Base.Property.Single(p => p.Idx == 102).Data, self.Base.Property.Single(p => p.Idx == 2001).Data);
        Assert.Equal(maxHp, Service<CharacterService>().Stats(Session)[2000]);
        Assert.Contains(Session.Buffs, b => b.IsPassive && b.SourceId == 990001 && b.ConfigId == 2500201);
        Assert.Equal(Service<CharacterService>().Stats(Session)[103], self.Base.Property.Single(p => p.Idx == 103).Data);
        Assert.False(Session.Dead);
        Assert.Equal(0, Session.InstanceId);
        Assert.Null(Session.TowerAttempt);
        await Next(bot, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, info => !info.BDead);

        await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(ErrorCodes.SceneMissing,
            (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
        Assert.Equal(maxHp, Service<CharacterService>().Stats(Session)[2000]);
    }

    [ClientDataFact]
    public async Task ManualExitKeepsLivingPlayersWoundedHp()
    {
        await using var bot = await InWorld();
        await EnterFloor(bot, 1);
        long wounded = Service<CharacterService>().Stats(Session)[101] / 2;
        long spentSp = Service<CharacterService>().Stats(Session)[102] / 2;
        await OnLoop(async () =>
        {
            foreach (var monster in Session.TowerAttempt!.Monsters) monster.Dead = true;
            Session.LastCombatMs = MonsterService.NowMs;
            Session.StatusBits |= 1L << 15; // suppress out-of-combat regen during the return assertion
            await Service<CharacterService>().SetHpSpAsync(Session, wounded, spentSp);
        });
        var self = await ReturnToWorld(bot);
        Assert.Equal(EntityState.Alive, self.Base.State);
        Assert.Equal(wounded, self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.Equal(spentSp, self.Base.Property.Single(p => p.Idx == 2001).Data);
        Assert.Equal(wounded, Service<CharacterService>().Stats(Session)[2000]);
        Assert.Null(Session.TowerAttempt);
    }

    [ClientDataFact]
    public async Task ReturnWaitsForCompetingReviveClaimBeforeComposingSelf()
    {
        await using var bot = await InWorld();
        await EnterFloor(bot, 1);
        var combat = Service<CombatService>();
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(Session, 0, null);
            Session.Dead = true; // isolated claim-contention fixture; lethal wire is covered above
            Assert.True(combat.TryClaimRevive(Session));
        });
        try
        {
            await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            int priorSelf = bot.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf));
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Task.Delay(100);
            Assert.Equal(priorSelf, bot.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf)));
            Assert.True(Session.Dead);
        }
        finally { combat.ReleaseReviveClaim(Session); }
        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(EntityState.Alive, self.Base.State);
        Assert.Equal(self.Base.Property.Single(p => p.Idx == 101).Data,
            self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.False(Session.Dead);
    }

    [ClientDataFact]
    public async Task InterruptedReturnCannotPublishSelfOrReviveInTheNewSceneHandshake()
    {
        await using var bot = await InWorld();
        await EnterFloor(bot, 1);
        var combat = Service<CombatService>();
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(Session, 0, null);
            Session.Dead = true;
            Assert.True(combat.TryClaimRevive(Session));
        });
        int priorSelf = bot.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf));
        try
        {
            await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            // An independent server/GM handoff can run while the return 30005 waits for the revive claim.
            // Use the same destination to prove SceneLoading matters even when scene/instance IDs match.
            await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session, sw.SceneEntry)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        }
        finally { combat.ReleaseReviveClaim(Session); }

        // A second request on the same connection completes only after the suspended 30005 has returned.
        await bot.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
        await Next(bot, MsgId.PlayerAsset, PlayerAssetResp.Parser);
        Assert.Equal(priorSelf, bot.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf)));
        Assert.DoesNotContain(Session, Service<World>().PlayersIn(Session.WorldScene));
        Assert.True(Session.SceneLoading);
        Assert.True(Session.Dead);
        Assert.Equal(0, Service<CharacterService>().Stats(Session)[2000]);

        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Session.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(EntityState.Dead, self.Base.State);
        Assert.Equal(0, self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.True(Session.Dead); // canceled tower recovery is not inherited by the later scene load
    }

    [ClientDataFact]
    public async Task DelayedTowerMonsterHitCannotDamagePlayerAfterReturn()
    {
        await using var bot = await InWorld();
        var floor = await EnterFloor(bot, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(() =>
        {
            Service<CombatService>().IncomingDamageAdjusters.Add(async (monster, target, _, damage) =>
            {
                if (monster.InstanceId != floor.Instance || target != Session) return damage;
                entered.TrySetResult();
                await release.Task;
                resumed.TrySetResult();
                return damage;
            });
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(MsgId.Walk,
            new WalkReq { Pos = floor.Monsters[0].MovementInfo.Pos, BStop = true, SpeedRate = 1 });
        await entered.Task.WaitAsync(Wait);
        long hp = Service<CharacterService>().Stats(Session)[2000];
        try
        {
            var self = await ReturnToWorld(bot);
            Assert.Equal(EntityState.Alive, self.Base.State);
            Assert.Equal(hp, self.Base.Property.Single(p => p.Idx == 2000).Data);
        }
        finally { release.TrySetResult(); }
        await resumed.Task.WaitAsync(Wait);
        await Task.Delay(200); // allow the resumed hit task to complete its guarded commit
        Assert.Equal(hp, Service<CharacterService>().Stats(Session)[2000]);
        Assert.False(Session.Dead);
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == (long)bot.Self!.Uuid));
    }

    [ClientDataFact]
    public async Task SuccessAndTimeoutReturnThroughTheSameTowerExitWithoutHealingLivingHp()
    {
        await using var bot = await InWorld();
        var first = await EnterFloor(bot, 1);
        foreach (var monster in first.Monsters) await Kill(bot, monster.Uuid);
        Assert.True((await Next(bot, TowerService.End, ClimTowerEndNotify.Parser)).ClimTowerResult);
        long wounded = Service<CharacterService>().Stats(Session)[101] / 2;
        await OnLoop(() =>
        {
            Session.LastCombatMs = MonsterService.NowMs;
            Session.StatusBits |= 1L << 15;
            return Service<CharacterService>().SetHpSpAsync(Session, wounded, null);
        });
        var successSelf = await ReturnToWorld(bot);
        Assert.Equal(EntityState.Alive, successSelf.Base.State);
        Assert.Equal(wounded, successSelf.Base.Property.Single(p => p.Idx == 2000).Data);

        var second = await EnterFloor(bot, 2);
        await OnLoop(() => { Session.TowerAttempt!.StartedAt -= TimeSpan.FromSeconds(60); return Task.CompletedTask; });
        await Kill(bot, second.Monsters[0].Uuid);
        Assert.False((await Next(bot, TowerService.End, ClimTowerEndNotify.Parser)).ClimTowerResult);
        await OnLoop(() =>
        {
            Session.LastCombatMs = MonsterService.NowMs;
            Session.StatusBits |= 1L << 15;
            return Task.CompletedTask;
        });
        long afterTimeout = Service<CharacterService>().Stats(Session)[2000];
        var timeoutSelf = await ReturnToWorld(bot);
        Assert.Equal(EntityState.Alive, timeoutSelf.Base.State);
        Assert.Equal(afterTimeout, timeoutSelf.Base.Property.Single(p => p.Idx == 2000).Data);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "data", "config"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Repository data/config not found");
    }
}
