using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Reconstructed story dungeons (Reconstructed/dungeon-monsters.json): plans, a full clear of each, the switch.</summary>
public sealed partial class DungeonTests
{
    public static TheoryData<long> ReconstructedScenes() => [.. new ReconstructedDungeons(
        Options.Create(new ServerOptions())).All.Select(d => d.Scene).Order()];

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

    // The client finds a dungeon's item limit in DungeonConfig (Lua_CFG_MapConfigBase.GetEntryPtr). The server looked only
    // in MapConfig, which has no dungeon ids, and refused the World Tree cast in every dungeon.
    [ClientDataFact]
    public async Task TheYggdrasilLeafCastFollowsTheDungeonsOwnItemLimit()
    {
        const int leafCast = 2200101, leaf = 1002004;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        // 準備合法的飛船來源任務，保留正式入場門檻；本案只驗副本道具限制。
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(TaskState.Running, Session(alice).Tasks!.Infos[10103210].Status);
        await Gm(alice, $"!item {leaf} 2");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1000501 })); // _iLimitType 0
        await Load(alice, 1000501);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = leafCast, TargetId = (long)alice.Self!.Uuid });
        Assert.NotEqual(ErrorCodes.ItemUseAreaForbidden, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // 1000502 has _iLimitType 1, [1, 2]: the Leaf's type. It has no encounter to enter, so only the id is taken.
        await OnLoop(() => { Session(alice).SceneEntry = 1000502; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = leafCast, TargetId = (long)alice.Self!.Uuid });
        Assert.Equal(ErrorCodes.ItemUseAreaForbidden, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(2, Session(alice).Items.Where(i => i.Entry == leaf).Sum(i => i.Stack));
        await OnLoop(() => { Session(alice).SceneEntry = 1000501; return Task.CompletedTask; });
    }

    // 47 party dungeons have OnClickRevive [0] and 8 a timed [2,5], with the Leaf allowed and no boss count. The allowance
    // read their count of 0 as "none left", so every Leaf in them was refused while a click revived for free.
    [ClientDataTheory]
    [InlineData(0, 0)]
    [InlineData(2, 5)]
    public async Task AYggdrasilLeafRevivesADeadAllyInsideADungeon(int option, int seconds)
    {
        JsonArray revive = option == 0 ? new JsonArray(0) : new JsonArray(option, seconds);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long room = await LeafRoom(alice, bob, trigger: 0, revive);
        await KillAndCastLeaf(alice, bob);
        Assert.Equal("Bob", (await Next(alice, BuffService.WorldTreeRevive, ItemWorldTreeReviveNtf.Parser)).WorldTreePlayerName);
        await OnLoop(() =>
        {
            Assert.False(Session(bob).Dead);
            Assert.True(Service<CharacterService>().Stats(Session(bob))[2000] > 0);
            Assert.Equal(room, Session(bob).InstanceId);
            Assert.Equal(1, Session(alice).Items.Where(i => i.Entry == LeafItem).Sum(i => i.Stack));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task AYggdrasilLeafIsRefusedWhereTheDungeonHasNoRevive()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await LeafRoom(alice, bob, trigger: 2, []); // NoRevive, as 3120401
        Assert.Equal(ErrorCodes.FeatureUnavailable, await KillAndCastLeaf(alice, bob, expectAccepted: false));
        await OnLoop(() =>
        {
            Assert.True(Session(bob).Dead);
            Assert.Equal(2, Session(alice).Items.Where(i => i.Entry == LeafItem).Sum(i => i.Stack));
            return Task.CompletedTask;
        });
    }

    private const int LeafCast = 2200101, LeafItem = 1002004;

    /// <summary>Alice and Bob in one room of a story scene, its row set to leave the Leaf alone and to the given revive rule.</summary>
    private async Task<long> LeafRoom(BotClient alice, BotClient bob, int trigger, JsonArray revive)
    {
        var request = ConfigureTwoActorStory();
        ChangeRow("DungeonConfig", request.SceneEntry.ToString(System.Globalization.CultureInfo.InvariantCulture), row =>
        {
            row["_iLimitType"] = 0;
            row["_iReviveTrigger"] = trigger;
            row["_kTriggerParm"] = new JsonArray(revive);
        });
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        await Gm(alice, $"!item {LeafItem} 2");
        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice, request));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        long room = await Load(alice, request.SceneEntry);
        Assert.Equal(room, await Load(bob, request.SceneEntry));
        return room;
    }

    private async Task<int> KillAndCastLeaf(BotClient alice, BotClient bob, bool expectAccepted = true)
    {
        await OnLoop(() => Service<CombatService>().KillPlayerAsync(Session(bob)));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = LeafCast, TargetId = (long)bob.Self!.Uuid });
        int result = (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
        if (expectAccepted) Assert.Equal(0, result);
        return result;
    }

    [ClientDataFact]
    public async Task ASoloStoryPlaneReturnsThePlayerToTheFieldAfterItsClear()
    {
        // Seen on a player's client: Southern Payon's portal waves ended at (3/3) and the empty plane stayed until exit.
        const long scene = 1021301;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var plan = Session(alice).DungeonAttempt!.Plan;
        for (int wave = 0; wave < plan.Waves.Count; wave++)
            foreach (var actor in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(alice, actor);
        using var left = alice.Inbox.Expect(e => e.IsGame(DungeonService.Leave));
        await AssertDungeonSuccessAsync(alice);
        Assert.Equal(instance, Session(alice).InstanceId); // the result first, the return a moment later
        var leave = await left.WaitAsync(TimeSpan.FromSeconds(DungeonService.PlaneExitSeconds + 10));
        Assert.Equal(0, LeaveDungeonRsp.Parser.ParseFrom(leave.Body).Result);
        for (int i = 0; i < 100 && Session(alice).InstanceId != 0; i++) await Task.Delay(50);
        Assert.Equal(0, Session(alice).InstanceId);
    }

    [ClientDataFact]
    public async Task ASoloStoryPlaneShowsItsGuideRoutesUntilItIsCleared()
    {
        // The Culvert plane's scene editor (sc_prt_sewb1_001_pl01) has the guide routes 19 and 21.
        const long scene = 1015051;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var running = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.DungeonId == instance && info.Vars.Any(v => v.VarId == DungeonService.GuideWayVar));
        long[] routes = [19, 21];
        Assert.Equal(routes, running.Vars.Single(v => v.VarId == DungeonService.GuideWayVar).Vi64);

        using var hidden = alice.Inbox.Expect(e => e.IsGame(DungeonService.Event)
            && DungeonEventNotify.Parser.ParseFrom(e.Body).Vars.Any(v => v.VarId == DungeonService.GuideWayVar));
        var plan = Session(alice).DungeonAttempt!.Plan;
        for (int wave = 0; wave < plan.Waves.Count; wave++)
            foreach (var actor in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(alice, actor);
        await AssertDungeonSuccessAsync(alice);
        var cleared = DungeonEventNotify.Parser.ParseFrom((await hidden.WaitAsync(TimeSpan.FromSeconds(10))).Body);
        Assert.Empty(cleared.Vars.Single(v => v.VarId == DungeonService.GuideWayVar).Vi64);
    }

    [Fact]
    public void GuideRoutesAreTheSceneEditorRoutesWithAPath()
    {
        using var editor = JsonDocument.Parse("""
            { "m_kGuidePoints": { "21": { "pointList": [ {}, {} ] }, "19": { "pointList": [ {}, {}, {} ] },
              "7": { "pointList": [ {} ] }, "x": { "pointList": [ {}, {} ] }, "8": null } }
            """);
        long[] routes = [19, 21];
        Assert.Equal(routes, SceneEditorGuides.RouteIds(editor.RootElement));
        using var none = JsonDocument.Parse("{}");
        Assert.Empty(SceneEditorGuides.RouteIds(none.RootElement));
    }

    [ClientDataFact]
    public async Task TheEggThiefPortalWaveKeepsPursuingFromOutsideTheFieldLeash()
    {
        // The encounter's own monsters (NPCConfig 5001046-5001050, AI 20200004: 150 m chase) reach the player from every
        // portal with their client templates; no scene-specific pace or leash.
        const long scene = 1021301;
        await using var alice = await InWorld("Alice");
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
                var client = monsters.Template(actor.Template.NpcId)!;
                Assert.Equal((client.SpeedCm, client.LeashDistance), (actor.Template.SpeedCm, actor.Template.LeashDistance));
            }
            return Task.CompletedTask;
        });
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
        await AssertDungeonSuccessAsync(alice);
        for (int i = 0; i < 100 && State() == TaskState.Running && !TaskService.IsDelivered(Session(alice), 10103120); i++)
            await Task.Delay(20);
        Assert.True(State() is TaskState.Finish || TaskService.IsDelivered(Session(alice), 10103120), $"quest state {State()}");
    }

    [ClientDataTheory]
    [InlineData(1021301L, 10102100L, 10102110L)]
    [InlineData(1022001L, 10102200L, 10102210L)]
    [InlineData(1051001L, 10103120L, 10103130L)]
    public async Task AStoryDungeonClearDeliversItsQuestAndAcceptsTheFollowUp(long scene, long task, long next)
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, $"!quest accept {task}");
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<DungeonService>().Completed.Add((s, completedScene, _) =>
        {
            if (s.Account == alice.Account && completedScene == scene) completed.TrySetResult(completedScene);
            return Task.CompletedTask;
        });
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await ClearReconstructedStoryRun(alice, instance);
        await AssertDungeonSuccessAsync(alice);
        Assert.Equal(scene, await completed.Task.WaitAsync(Wait));
        await StoryMovesOn(alice, task, next);
    }

    [ClientDataFact]
    public async Task TheTigerKingsFollowUpUsesTheFieldTaskPointAfterTheDungeonIsCleared()
    {
        const long field = 10220, dungeon = 1022001;
        const long clearTask = 10102200, peltTask = 10102210, nextTask = 10102220;
        const long taskPoint = 1010221001;
        const int interaction = 40022001;

        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, $"!quest accept {clearTask}");
        await MovePublic(alice, field);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = dungeon }));
        long instance = await Load(alice, dungeon);
        await ClearReconstructedStoryRun(alice, instance);
        await AssertDungeonSuccessAsync(alice);
        await StoryMovesOn(alice, clearTask, peltTask);

        // The pelt is not the private-dungeon Eddga corpse. TaskConfig tracks a client-side
        // task-point NPC (400220) on the public Payon Forest map, where 40022001 reports 31214.
        var point = Service<WorldData>().Map(field)!.Points[taskPoint];
        var npc = Service<GameData>().Row("NPCConfig", 400220)!.Value;
        Assert.Contains(interaction, npc.GetProperty("_iInteractID").EnumerateArray()
            .Select(e => (int)(GameData.Number(e) ?? 0)));

        // 原服虎王在持久化成功及30132後自動返回，不需玩家30402。
        var returned = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            sw => sw.SceneEntry == field);
        Assert.Equal(field, returned.SceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = field });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), point));
        await alice.SendGameAsync(TaskService.TriggerTaskEvent,
            new TriggerTaskEventReq { TaskId = interaction, GoalCount = 1 });
        Assert.Equal(0, (await Next(alice, TaskService.TriggerTaskEvent, TriggerTaskEventResp.Parser)).Result);
        await StoryMovesOn(alice, peltTask, nextTask);
    }

    private async Task AssertDungeonSuccessAsync(BotClient bot)
    {
        if (DungeonService.CapturedStoryScene(Session(bot).DungeonAttempt!.Plan.SceneEntry))
            Assert.Equal(1, (await Next(bot, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);
        else Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }

    private async Task ClearReconstructedStoryRun(BotClient bot, long instance)
    {
        var plan = Session(bot).DungeonAttempt!.Plan;
        if (plan.SceneEntry == 1000501)
        {
            await AirshipStoryThroughCrew(bot, instance);
            await AirshipLifecycleCaptainOption(bot);
            return;
        }
        for (int wave = 0; wave < plan.Waves.Count; wave++)
        {
            var step = plan.Waves[wave].Step;
            if (step != null)
            {
                await Stage(bot, wave + 1, quest: step.Quest);
                await ActorsIn(bot, step.Actors.Count);
            }
            if (plan.Waves[wave].Spawns.Count > 0)
                foreach (var monster in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(bot, monster);
            if (step?.Complete.Count > 0) await Gm(bot, "!actors use");
        }
    }

    [ClientDataFact]
    public async Task TheEggThiefWavesPayTheQuestShirtAndThePoringCardTheCardGuideNeeds()
    {
        // Reported: "survive 3 waves" paid nothing, and the card guide 26 (after 10102110) found no card to insert.
        // 10102100 [101018,1021301,1] pays _iAward 31210 (AwardConfig 10031210: Cotton Shirt 2550130); the wave Poring
        // 5001049 drops NpcNormalDropConfig 100241949 -> DropPool 241949 -> DropGroup 144: Card: Poring 1420300,
        // _iQuestLimit 10102100 (dialogue 1010211001: "you picked up a Card: Poring!").
        const long scene = 1021301, quest = 10102100;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 16");
        await Gm(alice, $"!quest accept {quest}");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var plan = Session(alice).DungeonAttempt!.Plan;
        for (int wave = 0; wave < plan.Waves.Count; wave++)
            foreach (var actor in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(alice, actor);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        for (int i = 0; i < 100 && !TaskService.IsDelivered(Session(alice), quest); i++) await Task.Delay(20);
        Assert.True(TaskService.IsDelivered(Session(alice), quest));
        Assert.Equal(1, Items(alice, 2550130));
        Assert.Equal(1, Items(alice, 1420300));
    }

    [ClientDataFact]
    public async Task ADeathInADungeonRunGetsOnlyTheRunsReviveNotice()
    {
        // Seen live: the generic 30-second 30112 and the run's own 5-second one both reached the client.
        await using var alice = await InWorld("Alice");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1015051 }));
        long instance = await Load(alice, 1015051);
        await WaveIn(instance, 2);
        Session(alice).NoRegenUntilMs = long.MaxValue; // a recovery tick would lift this 1 HP out of one lethal hit
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

    // 1022001's door (condition 1000000: 1101, quest 10102200 running) is closed once its quest is finished, while region
    // 1010220001 is armed for Running and Finish: walking through it after the fight popped "This feature is unavailable".
    [ClientDataFact]
    public async Task TheEddgaRegionPopsNoRefusalOnceItsQuestIsFinished()
    {
        await using var alice = await InWorld("Alice");
        var region = Service<WorldData>().Map(10220)!.Regions[1010220001];
        await Gm(alice, "!quest accept 10102200");
        bool entered = await Service<GameLoop>().InvokeAsync(() =>
        {
            Session(alice).Tasks!.Infos[10102200].Status = TaskState.Finish;
            return Service<DungeonService>().EnterFromRegionAsync(Session(alice), region);
        });
        Assert.False(entered);
        // Still running, the same step enters.
        entered = await Service<GameLoop>().InvokeAsync(() =>
        {
            Session(alice).Tasks!.Infos[10102200].Status = TaskState.Running;
            return Service<DungeonService>().EnterFromRegionAsync(Session(alice), region);
        });
        Assert.True(entered);
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
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
