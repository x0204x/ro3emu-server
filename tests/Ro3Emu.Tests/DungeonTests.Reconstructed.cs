using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Reconstructed story dungeons (Reconstructed/dungeon-monsters.json): plans, a full clear of each, the switch.</summary>
public sealed partial class DungeonTests
{
    public static TheoryData<long> ReconstructedScenes() => new(new ReconstructedDungeons(
        Options.Create(new ServerOptions())).All.Select(d => d.Scene).Order());

    [ClientDataTheory]
    [MemberData(nameof(ReconstructedScenes))]
    public void AReconstructedStoryDungeonResolvesToItsWavesOfClientKnownMonsters(long scene)
    {
        var rebuilt = Service<ReconstructedDungeons>().For(scene)!;
        var plan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = scene });
        Assert.NotNull(plan);
        Assert.Equal(rebuilt.Waves.Count, plan.Waves.Count);
        for (int w = 0; w < plan.Waves.Count; w++)
        {
            Assert.Equal(rebuilt.Waves[w].Spawns.Select(s => s.NpcId), plan.Waves[w].Spawns.Select(s => s.NpcId));
            // Each wave plays a configured DungeonQuestConfig quest of this scene (the client tracker shows it).
            Assert.True(Service<GameData>().Table("DungeonQuestConfig").Values.Any(q => GameData.Int(q, "_iDungeonID") == scene
                && GameData.Int(q, "_iQuestID") == plan.Waves[w].Quest), $"wave {w + 1} quest {plan.Waves[w].Quest}");
        }
        foreach (var npc in plan.Waves.SelectMany(w => w.Spawns).Select(s => s.NpcId).Distinct())
        {
            var template = Service<MonsterService>().Template(npc);
            Assert.NotNull(template); // a client NPCConfig monster: the client can show it
            // Story templates swing their own normal attacks (MainStory_*NormalAttack, the rows the airship crew list).
            if (npc / 100000 == 53) Assert.NotEqual(0, template.NormalSkill);
        }
    }

    [ClientDataTheory]
    [MemberData(nameof(ReconstructedScenes))]
    public async Task AReconstructedStoryDungeonCanBeClearedWaveByWave(long scene)
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        var request = new EnterDungeonReq { SceneEntry = scene };
        Assert.Equal(0, await Enter(alice, request));
        long instance = await Load(alice, scene);
        var plan = Session(alice).DungeonAttempt!.Plan;
        for (int wave = 0; wave < plan.Waves.Count; wave++)
        {
            // The wave's event carries var 10001 (PDungeonQuestIdList) ending with the quest it plays.
            var started = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.StageNum == wave + 1);
            Assert.Equal(plan.Waves[wave].Quest, Assert.Single(started.Vars, v => v.VarId == 10001).Vi64.Last());
            // The client's task widget reads them from 30401 only: every wave's state is published there too.
            await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, info => info.Stage == wave + 1
                && info.Vars.Any(v => v.VarId == 10001 && v.Vi64.LastOrDefault() == plan.Waves[wave].Quest));
            int size = plan.Waves[wave].Spawns.Count;
            // ...and var 10003 (PDungeonQuestGoalParamList), the goal's "(done/total)" for this wave.
            Assert.Equal([0L, size], Assert.Single(started.Vars, v => v.VarId == 10003).Vi64);
            var actors = await WaveIn(instance, size);
            Assert.Equal(plan.Waves[wave].Spawns.Select(s => s.NpcId).Order(), actors.Select(a => a.Template.NpcId).Order());
            Assert.Equal(wave, Session(alice).DungeonAttempt!.Wave);
            for (int i = 0; i < actors.Count; i++)
            {
                await Kill(alice, actors[i]);
                // the counter follows each kill, the last one included (it shows done before the next wave or the result)
                await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, info => info.Vars
                    .Any(v => v.VarId == 10003 && v.Vi64.Count == 2 && v.Vi64[0] == i + 1 && v.Vi64[1] == size));
            }
        }
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        Assert.DoesNotContain(Service<World>().AllUnits(), m => m.InstanceId == instance && m is MonsterUnit);
    }

    [ClientDataFact]
    public async Task TheEggThiefPortalWaveKeepsPursuingFromOutsideTheFieldLeash()
    {
        const long scene = 1021301;
        await using var alice = await InWorld("Alice");
        var fieldTemplate = Assert.IsType<MonsterTemplate>(Service<MonsterService>().Template(5000035));
        Assert.Equal(0, fieldTemplate.SpeedCm);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var actors = await WaveIn(instance, 3);
        var player = Session(alice);

        await OnLoop(() =>
        {
            var monsters = Service<MonsterService>();
            var map = Service<World>().Map(player.WorldScene);
            foreach (var actor in actors)
            {
                float dx = actor.Home.X - player.Position.X, dz = actor.Home.Z - player.Position.Z;
                float homeDistance = MathF.Sqrt(dx * dx + dz * dz);
                Assert.True(actor.Template.SpeedCm > 0, $"portal monster {actor.Template.NpcId} cannot move");
                Assert.True(actor.Template.LeashDistance >= homeDistance,
                    $"portal monster {actor.Template.NpcId} leash {actor.Template.LeashDistance} < {homeDistance}");
                Assert.Same(player, actor.Target);
                float before = MathF.Sqrt(MathF.Pow(actor.X - player.Position.X, 2) + MathF.Pow(actor.Z - player.Position.Z, 2));
                monsters.Tick(actor, map);
                Assert.Same(player, actor.Target);
                Assert.True(actor.Moving, $"portal monster {actor.Template.NpcId} did not start pursuing");
                float destination = MathF.Sqrt(MathF.Pow(actor.ToX - player.Position.X, 2) + MathF.Pow(actor.ToZ - player.Position.Z, 2));
                Assert.True(destination < before, $"portal monster {actor.Template.NpcId} did not move toward the player");
            }
            return Task.CompletedTask;
        });
        Assert.Same(fieldTemplate, Service<MonsterService>().Template(5000035));
        Assert.Equal(0, fieldTemplate.SpeedCm); // the public-map template remains untouched
    }

    [ClientDataFact]
    public async Task AReconstructedClearCountsTowardItsStoryQuestDungeonPassTarget()
    {
        // Quest 10103120 targets [101018, 1051001, 1]: pass the Desert Crossroad Peco Peco nest once.
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103120");
        TaskState State() => Session(alice).Tasks!.Infos.TryGetValue(10103120, out var info) ? info.Status : TaskState.None;
        Assert.Equal(TaskState.Running, State());
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1051001 }));
        long instance = await Load(alice, 1051001);
        Assert.Equal(TaskState.Running, State()); // entering is not a pass
        foreach (var actor in await WaveIn(instance, 1)) await Kill(alice, actor);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        for (int i = 0; i < 100 && State() == TaskState.Running && !TaskService.IsDelivered(Session(alice), 10103120); i++)
            await Task.Delay(20);
        Assert.True(State() is TaskState.Finish || TaskService.IsDelivered(Session(alice), 10103120), $"quest state {State()}");
    }

    [ClientDataFact]
    public async Task ADeathInADungeonRunGetsOnlyTheRunsReviveNotice()
    {
        // Seen live: the generic 30-second 30112 and the run's own 5-second one both reached the client.
        await using var alice = await InWorld("Alice");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1015051 }));
        long instance = await Load(alice, 1015051);
        await WaveIn(instance, 2);
        await Gm(alice, "!hp 1");
        await Next(alice, DungeonService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, n => n.BDead);
        await Task.Delay(500); // anything else published for this death has arrived by now
        var notices = alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.ReviveInfo))
            .Select(e => SyncClientPlayerReviveInfoNtf.Parser.ParseFrom(e.Body)).Where(n => n.BDead).ToArray();
        var notice = Assert.Single(notices);
        long now = _clock.GetUtcNow().ToUnixTimeSeconds(); // the fixture's manual clock
        Assert.InRange(notice.ReviveTime, now, now + 6); // Culvert's OnClickRevive [[2,5]], not the generic 30 s
    }

    [Fact]
    public void TheSwitchOffLeavesTheStoryDungeonsWithoutAnEncounter()
    {
        var off = new ReconstructedDungeons(Options.Create(new ServerOptions { ReconstructedDungeons = false }));
        Assert.Null(off.For(1015051));
        Assert.Null(off.AttackSource(5300002));
        Assert.NotEmpty(off.All); // the data still loads; only its use is switched off
    }

    /// <summary>The living actors of the room's current wave, once all of them have spawned.</summary>
    private async Task<IReadOnlyList<MonsterUnit>> WaveIn(long instance, int count)
    {
        for (int i = 0; i < 150; i++)
        {
            var alive = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().Where(m => m.InstanceId == instance && !m.Dead && m.Hp > 0).ToArray()));
            if (alive.Length == count) return alive;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The wave of {count} did not spawn in instance {instance}");
    }
}
