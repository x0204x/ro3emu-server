using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Bot;

namespace Ro3Emu.Tests;

/// <summary>
/// Scripted story scenes (Reconstructed/scripted-scenes.json, DungeonService.Scripted): each scene is
/// entered, played through the events its client sends (32700 on the scene actors, walks into server regions, 84011
/// at a story's end), settles as a clear, and the clear counts for its story step so the next one is offered.
/// </summary>
public sealed partial class DungeonTests
{
    public static TheoryData<long> ScriptedScenes() => new(new ReconstructedDungeons(
        Options.Create(new ServerOptions())).AllScripted.Select(s => s.Scene).Order());

    [ClientDataTheory]
    [MemberData(nameof(ScriptedScenes))]
    public void AScriptedSceneResolvesToOneStepPerConfiguredStageAgainstTheClientData(long scene)
    {
        var scripted = Service<ReconstructedDungeons>().Scripted(scene)!;
        var plan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = scene });
        Assert.NotNull(plan); // every actor, point, region, buff, plot, interaction and message resolved
        Assert.Equal(scripted.Steps.Count, plan.Waves.Count);
        // A step's fight spawns client-known monsters at its points; a step without one spawns nothing.
        Assert.Equal(scripted.Steps.Select(s => s.Fight.Select(f => f.NpcId)), plan.Waves.Select(w => w.Spawns.Select(s => s.NpcId)));
        Assert.Equal(scripted.Steps, plan.Waves.Select(w => w.Step!));
        // The tracker walks the scene's configured DungeonQuestConfig quests in order, each of them but the unplayed.
        var quests = Service<GameData>().Table("DungeonQuestConfig").Values
            .Where(q => GameData.Int(q, "_iDungeonID") == scene).Select(q => (int)GameData.Int(q, "_iQuestID"))
            .Where(q => !scripted.Unplayed.Contains(q)).Order();
        Assert.Equal(quests, plan.Waves.Select(w => w.Quest).Distinct());
        Assert.Equal(plan.Waves.Select(w => w.Quest).Order(), plan.Waves.Select(w => w.Quest));
    }

    // The airship cannon route recovered from G client data: 5300006 mounts status 12; status button 27 casts 5353001
    // through ordinary combat, button 28 quits, and only the enemy ships' real deaths advance the stage.
    [ClientDataFact]
    public async Task TheAirshipCannonsSinkTheEnemyShips()
    {
        const long scene = 1000501;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        await Gm(alice, "!actors use"); // the chitchat (all four)
        await Stage(alice, 2, quest: 2);
        var cannons = await ActorsIn(alice, 3, a => a.NpcId == 5300022);
        var ships = await WaveIn(instance, 2);
        Assert.Equal([5300015, 5300053], ships.Select(m => m.Template.NpcId).Order());
        var cannon = cannons[0];
        await Gm(alice, "!actors 5300022");
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        var mounted = await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.UpperCannon);
        Assert.Equal((12L, 5300006, cannon.Uid, cannon.Uid),
            (mounted.DataInfo.StateId, mounted.DataInfo.InteractConfigId, mounted.DataInfo.ActiveObjId,
                mounted.DataInfo.UpperCannonInfo.CannonGuId));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SkillId == 5365001);

        static CastSkillReq Shot(DungeonService.SceneActorUnit gun, MonsterUnit ship) => new()
        {
            SkillId = 5353001, OpUnitId = gun.Uid, TargetId = ship.Uid,
            TargetPos = new Vec3 { X = ship.X, Y = ship.Y, Z = ship.Z },
        };
        Task PrepareLethalShot(MonsterUnit ship) => OnLoop(() =>
        {
            // Story waves engage on spawn. Moving to the cannon can put the player outside the ship's leash;
            // its return-completion tick would then replace the test's 1 HP with full health before impact.
            // These non-aggressive ships stay idle after this owned setup; the shot still uses real combat/death.
            Assert.False(ship.Template.Aggressive);
            ship.Target = null;
            ship.PetTarget = null;
            ship.Returning = false;
            ship.Moving = false;
            ship.Hp = 1;
            return Task.CompletedTask;
        });
        await PrepareLethalShot(ships[0]); // no invented original fixed shot count
        await alice.SendGameAsync(CombatService.UseSkill, Shot(cannon, ships[0]));
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, Shot(cannon, ships[1]));
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result); // 500 ms action/CD gate
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 5353001);
        Assert.Contains(first.Dsts, d => d.DstID == ships[0].Uid && d.IsDead);

        await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 28 });
        Assert.Equal(0, (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await alice.SendGameAsync(CombatService.UseSkill, Shot(cannon, ships[1]));
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await Task.Delay(600);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.UpperCannon);
        await PrepareLethalShot(ships[1]);
        await alice.SendGameAsync(CombatService.UseSkill, Shot(cannon, ships[1]));
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var second = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 5353001);
        Assert.Contains(second.Dsts, d => d.DstID == ships[1].Uid && d.IsDead);
        Assert.All(ships, m => Assert.True(m.Dead));
        await Stage(alice, 3, quest: 3); // the boarding
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.Idle && n.Interrupt);
    }

    // Moonlight Flower's hide-and-seek (inferred): standing in the open is found and the game starts over; a darkness
    // point passes a round and is searched, so hiding there again is found; another side of the room wins the second round.
    [ClientDataFact]
    public async Task MoonlightFlowersHideAndSeekIsWonByHidingInTwoPlaces()
    {
        const long scene = 3120802;
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var seeker = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal(400097, seeker.NpcId);
        var map = Service<WorldData>().Map(scene)!;
        Task Hide(long point) => Gm(alice, FormattableString.Invariant($"!actors walk {map.Points[point].X} {map.Points[point].Z}"));
        Task<SystemBoardCastMsgNtf> Says(int chat) => Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == chat);

        // Her opening bubbles over her own head, then the first round in the open: found.
        Assert.Equal(seeker.Uid, (await Says(5310011)).PlayerId);
        await Says(5310005); // "I'm about to catch you!"
        await Says(5310010); // the countdown tip
        await Says(5310014); // "You're no good at hide-and-seek!"
        // In the south-west darkness: the round passes; staying there is found, the place having been searched.
        await Hide(67);
        await Says(5310006); // "Play with me a little longer!"
        await Says(5310014);
        // The game started over: the same place passes round one again, the south-east corner wins round two.
        await Says(5310006);
        await Hide(78);
        await Says(5310007); // "I had a great time~"
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }

    // The story Pharaoh's Cleansing Light (inferred): the raid's tip, then his strongest skill on whoever stands outside the
    // Pyramid's shadow.
    [ClientDataFact]
    public async Task ThePharaohsCleansingLightHitsWhoeverIsNotBehindThePyramid()
    {
        const long scene = 3120702;
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var pyramid = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300011));
        var point = Service<WorldData>().Map(scene)!.Points[20];
        Assert.Equal((point.X + 6, point.Z + 7), (pyramid.X, pyramid.Z)); // beside Pharaoh's point, no point of its own
        var pharaoh = await BossIn(instance, 5300010);
        long me = Session(alice).Role!.Id;
        await Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 4370002); // "hide behind the Pyramid!"
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 5340301 && h.Dsts.Any(d => d.DstID == me));
        Assert.Equal(pharaoh.Uid, hit.SrcID);
    }

    [Theory]
    [InlineData(0f, 7f, true)] // straight behind the cover
    [InlineData(1.4f, 8f, true)] // within the shadow's width
    [InlineData(2f, 8f, false)] // beside it
    [InlineData(0f, 3f, false)] // in front of it
    [InlineData(0f, 12f, false)] // past the shadow's depth
    public void ThePyramidsShadowLiesBehindItAsSeenFromTheBoss(float x, float z, bool sheltered)
    {
        var shelter = new ReconstructedDungeons.Shelter(0, 0, 0, 0, 20000, 4000, 1.5f, 6);
        Assert.Equal(sheltered, DungeonService.Sheltered(0, 0, 0, 5, x, z, shelter));
    }

    [ClientDataFact]
    public async Task TheVortexScenePlaysFromTheCorridorThroughTheJumpToTheEndOfThePassage()
    {
        const long scene = 1000201;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10104290"); // Enter the Ant Queen's Nest: [101018, 1000201, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var bobo = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal((5300027, 1), (bobo.NpcId, bobo.EntityType)); // Bobo, a monster-type unit the client creates

        // Explore ahead: the corridor region 25 (where guide point 24 ends) is the server's to notice.
        var map = Service<WorldData>().Map(scene)!;
        await WalkInto(alice, map.Regions[25]);
        await Stage(alice, 2, quest: 2);
        var vortex = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300030));
        Assert.DoesNotContain(await ActorsIn(alice, 1), a => a.NpcId == 5300027); // Bobo was sucked in

        // Jump into the vortex: the client's auto interaction 5300030 in range; then the passage below.
        Assert.Equal(ErrorCodes.NpcTooFar, await Interact(alice, vortex, 5300030)); // not from the corridor's far end
        await Beside(alice, vortex.X, vortex.Y, vortex.Z);
        Assert.Equal(0, await Interact(alice, vortex, 5300030));
        await Stage(alice, 3, quest: 3);
        var top = map.Points[21];
        Assert.True(MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, top.X, top.Z) < 1,
            "the jump lands at point 21, the top of the passage");
        await ActorsIn(alice, 0); // nothing is shown now

        await WalkInto(alice, map.Regions[22]); // the bottom of the passage
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 10104290, 10104300);
        Assert.DoesNotContain(Service<World>().AllUnits(), u => u.InstanceId == instance);
    }

    [ClientDataFact]
    public async Task TheDeniroSceneOpensOneInteractionPerStepUntilTheRideToTheDepths()
    {
        const long scene = 1000301;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10104300"); // Explore the Ant Queen's Nest: [101018, 1000301, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var actors = await ActorsWhen(alice, shown => shown.Count == 2 && shown.Any(a => a.Buffs.Count > 0));
        var bobo = actors.Single(a => a.NpcId == 5300041);
        var deniro = actors.Single(a => a.NpcId == 5300005);
        // Bobo carries buff group 53014, the NPC condition of 'Rescue Bobo'; the Deniro carries nothing yet.
        Assert.Equal([5301401], bobo.Buffs.Select(b => b.ConfigId));
        Assert.Empty(deniro.Buffs);
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            update => update.Bases.Any(actor => actor.Uuid == (ulong)bobo.Uid));
        var seen = view.Bases.Single(actor => actor.Uuid == (ulong)bobo.Uid);
        Assert.Equal((5300041UL, 1, 5301401), (seen.EntityTemplateID, seen.EntityType, seen.BuffData.AddBuffs.Single().ConfigId));

        await Beside(alice, deniro.X, deniro.Y, deniro.Z);
        Assert.Equal(0, await Interact(alice, deniro, 5300005)); // the Deniro's own ride, not this step's event
        Assert.Equal(1, Session(alice).DungeonAttempt!.Wave + 1);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(alice, deniro, 5300028)); // not one of the Deniro's

        (int Npc, int Interaction, int Buff)[] chain =
        [
            (5300041, 5300028, 5301401), // help Bobo up
            (5300005, 5300005, 5365401), // try to ride the Deniro
            (5300055, 5300010, 5365201), // collect the fluorescent substance
            (5300005, 5300009, 5365301), // feed the Deniro
            (5300005, 5300008, 5365101), // ride to the depths
        ];
        for (int step = 0; step < chain.Length; step++)
        {
            if (step > 0) await Stage(alice, step + 1, quest: step + 1); // the first one was awaited above
            var (npc, buff) = (chain[step].Npc, chain[step].Buff);
            // exactly one interaction is open: the actor of this step carries its buff, no other actor carries one
            var shown = await ActorsWhen(alice, all => all.Count == (step == 2 ? 3 : 2) && all.Count(a => a.Buffs.Count > 0) == 1
                && all.Any(a => a.NpcId == npc && a.Buffs.Select(b => b.ConfigId).SequenceEqual([buff])));
            var actor = shown.Single(a => a.NpcId == npc);
            Assert.All(shown.Where(a => a != actor), a => Assert.Empty(a.Buffs));
            if (step == 1)
                // The kept Deniro got its buff in place (31104), without leaving and re-entering the view.
                await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                    n => n.UnitId == actor.Uid && n.Buffs.Any(b => b.ConfigId == 5365401));
            await Beside(alice, actor.X, actor.Y, actor.Z);
            // The reverse dungeon's client reports the NPC's place (31089) before each interaction (LC_DAL_Interact.SyncNpcPos).
            await alice.SendGameAsync(WorldCoreService.TeleportObject, new ClientTeleportObject
            { TargetUid = actor.Uid, TargetPos = new Vec3 { X = actor.X, Y = actor.Y, Z = actor.Z } });
            Assert.Equal(0, await Interact(alice, actor, chain[step].Interaction));
        }
        // ... and gets no error tip for it.
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(99999)
            && APIErrorMsg.Parser.ParseFrom(e.Body).OriginMsgId == WorldCoreService.TeleportObject);
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        var end = Service<WorldData>().Map(scene)!.Points[31]; // the ride ends in the end region
        Assert.True(MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, end.X, end.Z) < 1);
        await StoryMovesOn(alice, 10104300, 10104310);
    }

    [ClientDataFact]
    public async Task LouiesGamePlaysItsStoriesAndOnlyKatiesBellWins()
    {
        const long scene = 3120402;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10106270"); // The Mysterious Thief Gang: [101018, 3120402, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        // The intro story: dungeon var 100020016 [140010] in a var-change event, never in the 30401 snapshot.
        await Story(alice, 140010);
        await ActorsIn(alice, 0); // nothing is shown now
        await PlotEnd(alice, 140010);

        await Stage(alice, 2, quest: 1);
        var actors = await ActorsIn(alice, 6);
        Assert.Equal(3, actors.Count(a => a.NpcId == 400137)); // the bells
        var katie = actors.Single(a => a.NpcId == 400134);
        var wrong = actors.Single(a => a.NpcId == 400135);
        Assert.Equal(2, katie.EntityType); // TriggerNpcInteract looks the ring up as an NPC by its config id
        // Walking up opens the ring's dialogue (auto interaction 20003001, answered so the client runs action 53).
        await Beside(alice, wrong.X, wrong.Y, wrong.Z);
        Assert.Equal(0, await Interact(alice, wrong, 20004001));
        Assert.Equal(0, await Interact(alice, wrong, 5300041)); // 'Choose this one!': message 12, a wrong guess
        await Task.Delay(200);
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1); // the game goes on
        await Beside(alice, katie.X, katie.Y, katie.Z);
        Assert.Equal(0, await Interact(alice, katie, 20003001));
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1); // opening the dialogue is not choosing
        Assert.Equal(0, await Interact(alice, katie, 5300040)); // message 11: Katie found

        var reveal = await Stage(alice, 3, quest: 1);
        Assert.Equal([1L, 1L], reveal.Vars.Single(v => v.VarId == 10003).Vi64); // the game's goal shows (1/1)
        await Story(alice, 140020);
        await ActorsIn(alice, 0); // nothing is shown now
        await PlotEnd(alice, 140020);

        await Stage(alice, 4, quest: 2);
        var device = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal((400046, 37L), (device.NpcId, device.Point));
        var instrument = Service<WorldData>().Map(scene)!.Points[37];
        await WalkTo(alice, instrument.X + 6, instrument.Y, instrument.Z); // not yet within its 4 m
        await Task.Delay(200);
        Assert.False(Session(alice).DungeonAttempt!.Settled);
        await WalkTo(alice, instrument.X + 1, instrument.Y, instrument.Z);
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 10106270, 10106280);
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.BaseInfo))
            .SelectMany(e => DungeonBaseInfoNotify.Parser.ParseFrom(e.Body).Vars), v => v.VarId == DungeonService.StoryVar);
    }

    [ClientDataFact]
    public async Task MoonlightFlowersGameEndsWhenThePlayerHidesAwayFromTheOpenCentre()
    {
        const long scene = 3120802;
        await using var alice = await InWorld("Alice");
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10; // the hide-and-seek rounds, shortened
        await Gm(alice, "!quest accept 205110"); // Falling into a Trap: [101018, 3120802, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        var stage = await Stage(alice, 1, quest: 1);
        Assert.Equal([0L, 1L], stage.Vars.Single(v => v.VarId == 10003).Vi64); // 'Hide in the darkness' (0/1)
        var flower = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal((400097, 1, 26L), (flower.NpcId, flower.EntityType, flower.Point)); // turned away, counting

        // The open centre and Moonlight Flower's side are not hiding places: the game goes on (walking there never ends it).
        var map = Service<WorldData>().Map(scene)!;
        var born = map.Points[22];
        await WalkTo(alice, born.X, born.Y, born.Z);
        await WalkTo(alice, flower.X + 2, flower.Y, flower.Z);
        var hide = map.Points[107]; // group 27, west of the born point
        await WalkTo(alice, hide.X, hide.Y, hide.Z + 4); // not yet within its 3 m
        await Task.Delay(200);
        Assert.False(Session(alice).DungeonAttempt!.Settled);

        // Hidden in the west darkness for a round, then on the far side of the room for the second.
        await WalkTo(alice, hide.X, hide.Y, hide.Z + 1);
        await Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 5310006); // "Play with me a little longer!"
        await Gm(alice, FormattableString.Invariant($"!actors walk {map.Points[78].X} {map.Points[78].Z}"));
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 205110, 205120);
    }

    [ClientDataFact]
    public async Task LouiesShellGameTakesTwoRightGuessesWhileWrongOnesChangeNothing()
    {
        const long scene = 3120703;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 205170"); // A New Round: [101018, 3120703, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        var stage = await Stage(alice, 1, quest: 1);
        Assert.Equal([0L, 1L], stage.Vars.Single(v => v.VarId == 10003).Vi64); // 'Find Kitty-Katie' (0/1)
        var round1 = await ActorsIn(alice, 8);
        Assert.Equal([25L, 26L], round1.Where(a => a.NpcId is 400131 or 400132).Select(a => a.Point).Order()); // Louie, Martha
        Assert.Equal(3, round1.Count(a => a.NpcId == 400138)); // the Bells
        Assert.DoesNotContain(round1, a => a.NpcId == 400133); // Katie hides

        // A wrong guess: the ring's dialogue opens, 'Choose this one!' sends message 12, and the round goes on.
        var wrong = round1.Single(a => a.NpcId == 400135);
        await Beside(alice, wrong.X, wrong.Y, wrong.Z);
        Assert.Equal(0, await Interact(alice, wrong, 20004001));
        Assert.Equal(0, await Interact(alice, wrong, 5300041));
        await Task.Delay(200);
        Assert.Equal(1, Session(alice).DungeonAttempt!.Wave + 1);
        var katie = round1.Single(a => a.NpcId == 400134);
        Assert.Equal(31L, katie.Point); // straight ahead of Katie's place
        await Beside(alice, katie.X, katie.Y, katie.Z);
        Assert.Equal(0, await Interact(alice, katie, 20003001));
        Assert.Equal(0, await Interact(alice, katie, 5300040)); // message 11: right

        // Round 2: the cat has moved; the Bells, Louie and Martha stay, the rings change places.
        await Stage(alice, 2, quest: 1);
        var round2 = await ActorsWhen(alice, shown => shown.Count == 8 && shown.Any(a => a.NpcId == 400134 && a.Point == 29));
        Assert.All(round1.Where(a => a.NpcId is 400131 or 400132 or 400138), kept => Assert.Contains(kept, round2));
        Assert.False(Session(alice).DungeonAttempt!.Settled);
        var stale = round2.Single(a => a.NpcId == 400135);
        await Beside(alice, stale.X, stale.Y, stale.Z);
        Assert.Equal(0, await Interact(alice, stale, 5300041)); // a wrong guess in round 2 changes nothing either
        await Task.Delay(200);
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1);
        katie = round2.Single(a => a.NpcId == 400134);
        await Beside(alice, katie.X, katie.Y, katie.Z);
        Assert.Equal(0, await Interact(alice, katie, 5300040));
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 205170, 205180);
    }

    // Louis's lines and the Bells' effects (inferred as to when): round 1 opens with 'Let's start the game!' and 'Guess which
    // Bell ...'; a wrong ring answers 'Sorry, wrong guess!' and shatters its Bell (5402201); Katie's ring answers
    // 'Congratulations ...' and lights hers (5402101); round 2 asks again and ends on 'Darn ... I give up.'.
    [ClientDataFact]
    public async Task LouisAnswersEveryGuessAndTheChosenBellShowsWhetherItWasRight()
    {
        const long scene = 3120703;
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 205170");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var round1 = await ActorsIn(alice, 8);
        Task<SystemBoardCastMsgNtf> Says(int chat) => Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == chat);
        Task<BuffUpdateNotify> Shows(IEnumerable<DungeonService.SceneActorUnit> shown, long point, int buff) =>
            Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == buff)
                && n.UnitId == shown.Single(a => a.NpcId == 400138 && a.Point == point).Uid);

        Assert.Equal(round1.Single(a => a.NpcId == 400131).Uid, (await Says(5310008)).PlayerId); // Louie's own uid
        await Says(5310002);
        var wrong = round1.Single(a => a.NpcId == 400135);
        await Beside(alice, wrong.X, wrong.Y, wrong.Z);
        Assert.Equal(0, await Interact(alice, wrong, 5300041));
        await Says(5310004); // 'Sorry, wrong guess!'
        await Shows(round1, wrong.Point, 5402201);
        var katie = round1.Single(a => a.NpcId == 400134);
        await Beside(alice, katie.X, katie.Y, katie.Z);
        Assert.Equal(0, await Interact(alice, katie, 5300040));
        await Says(5310003); // 'Congratulations ... Let's go again'
        await Shows(round1, katie.Point, 5402101);

        await Stage(alice, 2, quest: 1);
        await Says(5310002);
        var round2 = await ActorsWhen(alice, shown => shown.Count == 8 && shown.Any(a => a.NpcId == 400134 && a.Point == 29));
        katie = round2.Single(a => a.NpcId == 400134);
        await Beside(alice, katie.X, katie.Y, katie.Z);
        Assert.Equal(0, await Interact(alice, katie, 5300040));
        await Says(5310009); // 'Darn, you got it right again! I give up.'
        await Shows(round2, katie.Point, 5402101);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }

    [ClientDataFact]
    public async Task TheOpeningDanceEndsWithLunasGoodbyeEvenWhenTheClientLeavesAtOnce()
    {
        const long scene = 1010601;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 201040"); // Complete the Opening Dance: [101018, 1010601, 1]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var hall = await ActorsIn(alice, 23);
        var luna = hall.Single(a => a.NpcId == 400119);
        Assert.Equal((38L, 1), (luna.Point, luna.EntityType));
        Assert.DoesNotContain(hall, a => a.NpcId is 400122 or 400123); // no dialogue before the dance

        // Walking up on Luna's other side is not the partner's place; within 3 m of mark 49 (region 48) is.
        var map = Service<WorldData>().Map(scene)!;
        var partner = map.Points[49];
        await WalkTo(alice, luna.X - 2, luna.Y, luna.Z);
        await Task.Delay(200);
        Assert.Equal(1, Session(alice).DungeonAttempt!.Wave + 1);
        await WalkTo(alice, partner.X, partner.Y, partner.Z - 2.5f);

        await Stage(alice, 2, quest: 2);
        var shell = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 400122));
        Assert.Equal((38L, 2), (shell.Point, shell.EntityType)); // on Luna, an NPC the client clicks
        Assert.DoesNotContain(await ActorsIn(alice, 24), a => a.NpcId == 400123); // the dance never fails here
        await Beside(alice, shell.X, shell.Y, shell.Z);
        Assert.Equal(0, await Interact(alice, shell, 40012201)); // the click opens her dialogue
        Assert.Equal(2, Session(alice).DungeonAttempt!.Wave + 1);
        // 'Goodbye': 211 TriggerNpcInteract 40012401 (message 66), then 35 ExitDungeon sends 30402 without waiting.
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = shell.Uid, InteractionId = 40012401, SceneConfigId = shell.SceneEntry,
        });
        await alice.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        await StoryMovesOn(alice, 201040, 201050);
    }

    [Fact]
    public void TheSwitchOffLeavesTheScriptedScenesUnreconstructed()
    {
        var off = new ReconstructedDungeons(Options.Create(new ServerOptions { ReconstructedDungeons = false }));
        Assert.Null(off.Scripted(1000201));
        Assert.NotEmpty(off.AllScripted);
    }

    // A step's reactions come from scripted-scenes.json (no client data needed): Louie's shell game, both rounds.
    [Fact]
    public void AStepsReactionsAreReadFromTheScriptedScenes()
    {
        var louie = new ReconstructedDungeons(Options.Create(new ServerOptions())).Scripted(3120703)!;
        Assert.All(louie.Steps, step => Assert.Equal(400131, step.React!.Speaker));
        Assert.Equal([5310008, 5310002], louie.Steps[0].React!.Start);
        Assert.Equal(new ReconstructedDungeons.Response(5310004, 5402201, 400138), louie.Steps[0].React!.Ignored);
        Assert.Equal(new ReconstructedDungeons.Response(5310003, 5402101, 400138), louie.Steps[0].React!.Complete);
        Assert.Equal(new ReconstructedDungeons.Response(5310009, 5402101, 400138), louie.Steps[1].React!.Complete);
        Assert.All(new ReconstructedDungeons(Options.Create(new ServerOptions())).Scripted(3120802)!.Steps,
            step => Assert.Null(step.React)); // a scene without reactions keeps none
    }

    [ClientDataFact]
    public void ServerRegionsUseTheClientsCircleAndRectangleFootprints()
    {
        var map = Service<WorldData>().Map(1000201)!;
        var vortex = map.Regions[20]; // circle, radius 5 (its length)
        Assert.Equal(1, vortex.Shape);
        Assert.True(vortex.Contains(vortex.X + 4.9f, vortex.Z));
        Assert.False(vortex.Contains(vortex.X + 3.6f, vortex.Z + 3.6f));
        var end = map.Regions[22]; // rectangle 30 x 5: length along x, width along z
        Assert.Equal(2, end.Shape);
        Assert.True(end.Contains(end.X + 14.5f, end.Z + 2.4f));
        Assert.False(end.Contains(end.X, end.Z + 2.6f));
        Assert.False(end.Contains(end.X + 15.1f, end.Z));
        var turned = end with { Direction = 90 };
        Assert.True(turned.Contains(turned.X, turned.Z + 14.5f));
        Assert.False(turned.Contains(turned.X + 14.5f, turned.Z));
    }

    // ---- helpers ----

    /// <summary>The step's 30401: its stage number and the quest the tracker shows last in var 10001.</summary>
    private static Task<DungeonBaseInfoNotify> Stage(BotClient bot, int stage, int quest) =>
        Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Stage == stage && info.Vars.Any(v => v.VarId == 10001 && v.Vi64.LastOrDefault() == quest));

    private static async Task Story(BotClient bot, long plot)
    {
        var e = await Next(bot, DungeonService.Event, DungeonEventNotify.Parser, n => (int)n.EventType == 4);
        Assert.Equal([plot], e.Vars.Single(v => v.VarId == DungeonService.StoryVar).Vi64);
    }

    /// <summary>Prontera North Gate's 'Investigate the fork in the road' completes where the client's guide arrow ends
    /// (line 24, to (109.6, 87.0)), not at the born point or halfway along; then Baphomet comes at his point 20.</summary>
    [ClientDataFact]
    public async Task TheNorthGateForkIsWhereTheGuideArrowEnds()
    {
        const long scene = 1011301;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        var map = Service<WorldData>().Map(scene)!;
        var born = map.Points[19];
        await WalkTo(alice, born.X, born.Y, born.Z);
        await WalkTo(alice, 112.7f, born.Y, 80f); // halfway along the arrow, 23.7 m from point 20
        await Task.Delay(200);
        Assert.Equal(0, Session(alice).DungeonAttempt!.Wave);

        await WalkTo(alice, 109.6f, born.Y, 87.0f);
        await Stage(alice, 2, quest: 2);
        var baphomet = await BossIn(instance, 5300003);
        Assert.Equal((map.Points[20].X, map.Points[20].Z), (baphomet.Home.X, baphomet.Home.Z));
    }

    /// <summary>What the client sends when the story ends: 32709 and the plot's end action 601 → 84011.</summary>
    private static async Task PlotEnd(BotClient bot, long plot)
    {
        await bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
        await bot.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = plot });
    }

    /// <summary>The scene actors shown, once exactly <paramref name="count"/> of them match.</summary>
    private Task<IReadOnlyList<DungeonService.SceneActorUnit>> ActorsIn(BotClient bot, int count,
        Func<DungeonService.SceneActorUnit, bool>? match = null) =>
        ActorsWhen(bot, shown => shown.Count(a => match == null || match(a)) == count, match);

    /// <summary>The scene actors shown (those matching), once the room's whole set satisfies <paramref name="ready"/>.</summary>
    private async Task<IReadOnlyList<DungeonService.SceneActorUnit>> ActorsWhen(BotClient bot,
        Func<IReadOnlyList<DungeonService.SceneActorUnit>, bool> ready, Func<DungeonService.SceneActorUnit, bool>? match = null)
    {
        DungeonService.SceneActorUnit[] shown = [];
        for (int i = 0; i < 150; i++)
        {
            shown = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
                Session(bot).DungeonAttempt?.SceneActors.ToArray() ?? []));
            if (ready(shown)) return shown.Where(a => match == null || match(a)).ToArray();
            await Task.Delay(20);
        }
        throw new TimeoutException($"The scene actors did not reach the expected state; shown: {string.Join(", ", shown.Select(a => $"{a.NpcId}@{a.Point}[{string.Join('/', a.Buffs.Select(b => b.ConfigId))}]"))}");
    }

    private static async Task<int> Interact(BotClient bot, DungeonService.SceneActorUnit actor, int interaction)
    {
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = actor.Uid, InteractionId = interaction, SceneConfigId = actor.SceneEntry,
        });
        var reply = await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            r => r.Req.NpcUid == actor.Uid && r.Req.InteractionId == interaction);
        return reply.Result;
    }

    private Task Beside(BotClient bot, float x, float y, float z) =>
        OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(x + 1, y, z, 0)));

    /// <summary>The client's walk report ends at (x, z): a short real walk from beside it, like any step.</summary>
    private async Task WalkTo(BotClient bot, float x, float y, float z)
    {
        await Beside(bot, x - 1.5f, y, z);
        var to = new Vec3 { X = x, Y = y, Z = z };
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = to, BStop = true, SpeedRate = 1 });
        for (int i = 0; i < 100; i++)
        {
            var at = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(bot).Position));
            if (Math.Abs(at.X - x) < 0.01f && Math.Abs(at.Z - z) < 0.01f) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The walk to ({x}, {z}) was not accepted");
    }

    private Task WalkInto(BotClient bot, MapRegion region) => WalkTo(bot, region.X, region.Y, region.Z);

    /// <summary>The clear counts for the story step (101018) and the chain offers the next one.</summary>
    private async Task StoryMovesOn(BotClient bot, long task, long next)
    {
        TaskState State(long id) => Session(bot).Tasks!.Infos.TryGetValue(id, out var info) ? info.Status : TaskState.None;
        for (int i = 0; i < 200 && !(TaskService.IsDelivered(Session(bot), task) && State(next) is TaskState.Running or TaskState.Finish); i++)
            await Task.Delay(20);
        Assert.True(TaskService.IsDelivered(Session(bot), task), $"task {task} is {State(task)}");
        Assert.True(State(next) is TaskState.Running or TaskState.Finish, $"next task {next} is {State(next)}");
    }
}
