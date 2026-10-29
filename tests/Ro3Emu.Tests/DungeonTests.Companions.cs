using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Allied companions (CompanionUnit, a scene actor marked "companion" in Reconstructed/scripted-scenes.json): Payon
/// Forest's Boya (5300087) and Wule (5300086), NPCConfig camp 1 with their own _kSkills and AI 5300007, fight Eddga
/// (5300001) at the scene's second step. They walk up to the nearest room monster until a ready skill reaches it, and
/// back to their points when there is none; Eddga may answer them once no player holds him; a kill of theirs counts
/// like the player's.
/// </summary>
public sealed partial class DungeonTests
{
    private const long PayonForest = 1022001;
    private const int Eddga = 5300001, Wule = 5300086, Boya = 5300087;
    /// <summary>The scene's born point (sc_pay_fild10_001_pl01 m_kBornPoints 出生点).</summary>
    private const long BornPoint = 16;

    [ClientDataFact]
    public void OnlyAPlayerSideMonsterNpcWithItsOwnSkillsCanBeACompanion()
    {
        var data = Service<GameData>();
        var monsters = Service<MonsterService>();
        var wule = CompanionUnit.Build(data, monsters, Wule)!;
        // NPCConfig _kSkills with SkillConfig _iCD and _iDistanceMax; AIConfig 5300007 _iSearchTime, _iAttackTickTime, _iViewRange.
        Assert.Equal([5353901, 5353601], wule.Skills.Select(s => s.Id));
        Assert.Equal([15000, 2000], wule.Skills.Select(s => s.CooldownMs));
        Assert.Equal([6f, 4f], wule.Skills.Select(s => s.Range));
        Assert.Equal((100, 1000, 100f), (wule.SearchMs, wule.AttackTickMs, wule.ViewRange));
        // The monster template: camp 1, level 20, HP from _iAttribute and _kAttriScale.
        Assert.Equal((1, 20), (wule.Template.Camp, wule.Template.Level));
        Assert.Equal(monsters.Template(Wule)!.MaxHp, wule.Template.MaxHp);
        Assert.NotNull(CompanionUnit.Build(data, monsters, Boya));
        Assert.Null(CompanionUnit.Build(data, monsters, Eddga)); // camp 2, no skills of its own
        Assert.Null(CompanionUnit.Build(data, monsters, 5300080)); // the hostile Wule, camp 3

        // Payon Forest's Eddga step shows the pair as companions, not as scene actors.
        var plan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = PayonForest })!;
        var step = plan.Waves[1].Step!;
        Assert.Equal([(Wule, 21L), (Boya, 23L)], step.Companions.Select(c => (c.NpcId, c.Point)));
        Assert.Empty(step.Actors);
        // A companion row that does not qualify refuses the scene.
        ChangeRow("NPCConfig", Boya.ToString(System.Globalization.CultureInfo.InvariantCulture), row => row["_kSkills"] = new JsonArray());
        Assert.Null(Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = PayonForest }));
    }

    [ClientDataFact]
    public async Task BoyaAndWuleFightEddgaAndTheirKillWinsTheStep()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        var map = Service<WorldData>().Map(PayonForest)!;
        Assert.Equal([Wule, Boya], companions.Select(c => c.NpcId).Order());
        Assert.All(companions, c => Assert.Equal((map.Points[c.Point].X, map.Points[c.Point].Z), (c.HomeX, c.HomeZ)));
        var wule = companions.Single(c => c.NpcId == Wule);
        long me = Session(alice).Role!.Id;
        var born = map.Points[BornPoint]; // about 20 m from either companion's point: Alice is not beside them
        Assert.All(companions, c => Assert.True(MonsterService.Distance(born.X, born.Z, c.HomeX, c.HomeZ) > 15));

        // A player cannot target a companion: its cast finds no monster there, and nothing hits it.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = wule.Uid });
        await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);

        // Alice stays at the born point; Eddga comes for her, and the pair walk up to him (31081) and hit him.
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => companions.Any(c => c.Uid == m.Uid) && m.MovementInfo.MoveType == MovementType.MoveTypeWalk);
        var cast = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => companions.Any(c => c.Uid == u.SrcID) && u.TargetId == eddga.Uid);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid && d.Damage > 0));
        var caster = companions.Single(c => c.Uid == cast.SrcID);
        Assert.True(await OnLoopValue(() => MonsterService.Distance(caster.X, caster.Z, caster.HomeX, caster.HomeZ) > 1),
            "the companion cast from where it walked to");
        Assert.True(await OnLoopValue(() => MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, born.X, born.Z) < 1));
        Assert.True(await OnLoopValue(() => eddga.Hp < eddga.Template.MaxHp));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == me && h.Dsts.Any(d => d.DstID == wule.Uid));
        Assert.Equal(wule.Template.MaxHp, await OnLoopValue(() => wule.Hp));

        // With no living monster in the room, each companion walks back to its point (its HP restored after).
        long eddgaHp = await OnLoopValue(() => eddga.Hp);
        await OnLoop(() => { eddga.Hp = 0; return Task.CompletedTask; });
        await Until(() => companions.All(c => MonsterService.Distance(c.X, c.Z, c.HomeX, c.HomeZ) < 0.5f), "the pair are back at their points");
        await OnLoop(() => { eddga.Hp = eddgaHp; return Task.CompletedTask; });

        // A companion's kill counts like the player's: the step completes, the run is won, the kill is Alice's.
        await OnLoop(() => { eddga.Hp = 1; return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid && d.IsDead));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        var attempt = Session(alice).DungeonAttempt!;
        Assert.Equal(1, attempt.Stats[me].KillNpcData[Eddga].LongValue);
        Assert.Equal(attempt.Spawned, attempt.Killed.Count); // the companions are not room monsters
    }

    [ClientDataFact]
    public async Task EddgaFightsBackAtACompanionThatFallsForGood()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid));

        // No player holds Eddga any more (as when she leaves his view): he turns to the companion that hit him (inferred).
        await OnLoop(() => { eddga.Target = null; return Task.CompletedTask; });
        var answer = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == eddga.Uid && h.Dsts.Any(d => companions.Any(c => c.Uid == d.DstID) && d.Damage > 0));
        var struck = companions.Single(c => answer.Dsts.Any(d => d.DstID == c.Uid));
        Assert.True(await OnLoopValue(() => struck.Hp < struck.Template.MaxHp));

        // At 0 HP it dies and leaves for good: no respawn.
        await OnLoop(() => { struck.Hp = 1; return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == eddga.Uid && h.Dsts.Any(d => d.DstID == struck.Uid && d.IsDead));
        await Until(() => !Session(alice).DungeonAttempt!.Companions.Contains(struck)
            && !Service<World>().AllUnits().Contains(struck), "the fallen companion is removed");
        Assert.True(struck.Dead);

        // The step still completes on Eddga's death.
        await Kill(alice, eddga);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.DoesNotContain(struck, Session(alice).DungeonAttempt!.Companions);
    }

    [ClientDataFact]
    public async Task ABossAreaSkillHitsTheCompanionsAndTowerPetsInsideIt()
    {
        await using var alice = await InWorld("Alice");
        var (_, companions) = await EddgaStep(alice);
        var options = Service<IOptions<ServerOptions>>().Value.BossSkills;
        options.DamagePercent = 1; // real damage that leaves the companion standing
        var s = Session(alice);
        var wule = companions.Single(c => c.NpcId == Wule);
        // Field Orc Hero's Terrifying Roar 5203001, an 8 m circle around itself, cast 3 m from Wule: its 1 m swing
        // never reaches him, and it casts nothing of its own (every skill spent).
        var orc = await SpawnBeside(s, Service<MonsterService>().Template(5020004)! with
        { AttackRange = 1, SpeedCm = 0, Aggressive = false, RoamDistance = 0 },
            wule.X + 5, wule.Y, wule.Z);
        var roar = Assert.Single(orc.Template.Skills!.Skills, k => k.Id == 5203001);
        Assert.Equal((BossSkillArea.AroundSelf, 8f), (roar.Area, roar.Radius));
        // A tower pet in the circle too (regression: it is hit as before).
        var pet = await OnLoopValue(() => TowerPetUnit.Create(Service<GameData>(),
            new DBPetInfo { Inited = true, ListPet = { [13001] = new PetData { PetId = 13001, Level = 12, Star = 1 } } }, s,
            Service<World>(), Service<MonsterService>(), Service<CombatService>(), TowerPetUnit.UidBase + (1L << 40), 13001,
            s.InstanceId, orc.X, orc.Y, orc.Z)!);
        await OnLoop(() => Service<World>().AddUnitAsync(pet));

        var hit = await RoarAt(alice, orc, roar, wule, pet);
        long wuleDamage = hit.Dsts.Single(d => d.DstID == wule.Uid).Damage;
        Assert.True(wuleDamage > 0);
        var petHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == orc.Uid && h.SkillId == roar.Id && h.Dsts.Any(d => d.DstID == pet.Uid && d.Damage > 0));
        Assert.Equal(hit.SessionId, petHit.SessionId); // the same cast hits both
        Assert.True(await OnLoopValue(() => wule.Hp <= wule.Template.MaxHp - wuleDamage && pet.Hp < pet.MaxHp));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)),
            h => h.SrcID == orc.Uid && h.Dsts.Any(d => d.DstID == s.Role!.Id)); // Alice, about 20 m away, is outside it

        // A companion the roar takes to 0 HP falls as under a normal attack: dead, shown dead, gone from the step.
        await OnLoop(() => { wule.Hp = 1; return Task.CompletedTask; });
        Assert.Contains((await RoarAt(alice, orc, roar, wule)).Dsts, d => d.DstID == wule.Uid && d.IsDead);
        await Until(() => wule.Dead && !s.DungeonAttempt!.Companions.Contains(wule), "Wule falls");
        Assert.Contains((wule.NpcId, wule.Point), s.DungeonAttempt!.FallenCompanions);
    }

    /// <summary>The boss, set 3 m beside the companion (and the pet 3 m beyond it), casts <paramref name="skill"/> as
    /// MonsterService's scheduler would; its hit on the companion.</summary>
    private async Task<SkillHitInfo> RoarAt(BotClient bot, MonsterUnit boss, BossSkill skill, CompanionUnit companion,
        TowerPetUnit? pet = null)
    {
        long serial = await OnLoopValue(() =>
        {
            var map = Service<World>().Map(boss.WorldScene);
            (boss.X, boss.Z) = (companion.X + 3, companion.Z);
            map.Moved(boss);
            if (pet != null)
            {
                (pet.X, pet.Z) = (companion.X + 6, companion.Z);
                map.Moved(pet);
            }
            var rt = boss.BossSkills ??= new BossSkillRuntime(boss.Template.Skills!) { Engaged = true };
            Array.Fill(rt.ReadyMs, long.MaxValue);
            boss.NextSpecialMs = long.MaxValue;
            return ++rt.CastSerial;
        });
        await OnLoop(() => Service<MonsterService>().CastBossSkill!(boss, null, null, skill, serial));
        return await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == boss.Uid && h.SkillId == skill.Id && h.Dsts.Any(d => d.DstID == companion.Uid));
    }

    [ClientDataFact]
    public async Task AnAggressiveMonsterAnsweringACompanionStillPutsAPlayerFirst()
    {
        await using var alice = await InWorld("Alice");
        var (_, companions) = await EddgaStep(alice);
        var s = Session(alice);
        var wule = companions.Single(c => c.NpcId == Wule);
        // An aggressive Eddga beside Wule, with a 6 m view: Alice, at the born point about 20 m away, is out of its sight.
        var probe = await SpawnBeside(s, Service<MonsterService>().Template(Eddga)! with
        { Aggressive = true, ViewRange = 6, LeashDistance = 1000, RoamDistance = 0 }, wule.X, wule.Y, wule.Z);
        await OnLoop(() => { Service<MonsterService>().Engage(probe, wule); return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == probe.Uid && h.Dsts.Any(d => d.DstID == wule.Uid));
        Assert.Null(await OnLoopValue(() => probe.Target));

        // Alice steps into its view: she becomes its target ahead of the companion, and its swing turns to her.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(s, new MapPoint(probe.X + 2, probe.Y, probe.Z, 0)));
        await Until(() => ReferenceEquals(probe.Target, s), "the aggressive monster takes Alice");
        long me = s.Role!.Id;
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == probe.Uid && u.TargetId == me);
    }

    [ClientDataFact]
    public async Task CompanionCreditGoesToALivingParticipantFirst()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (a, b) = (Session(alice), Session(bob));
        Assert.Same(a, await OnLoopValue(() => DungeonService.CompanionCreditAmong([a, b])));
        // The first participant is dead: the living one is credited.
        await OnLoop(() => { a.Dead = true; return Task.CompletedTask; });
        Assert.Same(b, await OnLoopValue(() => DungeonService.CompanionCreditAmong([a, b])));
        // None is alive: still the first, so a companion's kill can complete the run.
        await OnLoop(() => { b.Dead = true; return Task.CompletedTask; });
        Assert.Same(a, await OnLoopValue(() => DungeonService.CompanionCreditAmong([a, b])));
        Assert.Null(await OnLoopValue(() => DungeonService.CompanionCreditAmong([])));
    }

    [ClientDataFact]
    public async Task AMonsterThatCannotMoveDropsACompanionOutOfItsReach()
    {
        await using var alice = await InWorld("Alice");
        var (_, companions) = await EddgaStep(alice);
        var s = Session(alice);
        var wule = companions.Single(c => c.NpcId == Wule);
        // An Eddga that cannot move, 12 m from Wule, whose 2 m swing cannot reach him.
        var probe = await SpawnBeside(s, Service<MonsterService>().Template(Eddga)! with
        { SpeedCm = 0, AttackRange = 2, LeashDistance = 1000, RoamDistance = 0 }, wule.HomeX + 12, wule.Y, wule.HomeZ);
        // It lost its player (hurt, on its way home, as Disengage leaves it) and Wule's hit draws it.
        await OnLoop(() =>
        {
            probe.Hp = probe.Template.MaxHp / 2;
            probe.Returning = true;
            Service<MonsterService>().Engage(probe, wule);
            return Task.CompletedTask;
        });
        // It does not stand answering a companion it cannot reach: its return home completes (HP restored).
        await Until(() => probe.Hp == probe.Template.MaxHp && !probe.Returning, "the immobile monster finishes its return home");
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseInSkill))
            .Select(e => UseInSkillInfo.Parser.ParseFrom(e.Body)), u => u.SrcID == probe.Uid);
    }

    private Task<MonsterUnit> SpawnBeside(GameSession s, MonsterTemplate template, float x, float y, float z) =>
        Service<GameLoop>().InvokeAsync(async () =>
            (await Service<MonsterService>().SpawnSingleAsync(s.RuntimeMap, template.NpcId, new MapPoint(x, y, z, 0),
                customTemplate: template))!);

    /// <summary>Payon Forest at its Eddga step: the standoff point reached, Alice back at the born point, Eddga spawned
    /// and engaged, the pair shown.</summary>
    private async Task<(MonsterUnit Eddga, IReadOnlyList<CompanionUnit> Companions)> EddgaStep(BotClient alice)
    {
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10102200"); // Payon Forest's condition [1101, 10102200]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = PayonForest }));
        long instance = await Load(alice, PayonForest);
        await Stage(alice, 1, quest: 1);
        var standoff = Service<WorldData>().Map(PayonForest)!.Points[22];
        await Gm(alice, FormattableString.Invariant($"!actors walk {standoff.X} {standoff.Z}"));
        await Stage(alice, 2, quest: 2);
        var born = Service<WorldData>().Map(PayonForest)!.Points[BornPoint];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), born));
        var eddga = (await WaveIn(instance, 1)).Single();
        Assert.Equal(Eddga, eddga.Template.NpcId);
        CompanionUnit[] shown = [];
        await Until(() => (shown = [.. Session(alice).DungeonAttempt?.Companions ?? []]).Length == 2, "the pair is shown");
        return (eddga, shown);
    }

    private async Task<T> OnLoopValue<T>(Func<T> read) => await Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await OnLoopValue(condition)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(what);
    }
}
