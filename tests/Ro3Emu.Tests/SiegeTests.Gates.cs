using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The siege castle (TerritoryWarsService.SiegeObjects.cs): its gates, guardians, barricades and markers.</summary>
public sealed partial class SiegeTests
{
    private const int GatePoint = 36;
    private const uint GlobalObjectNotify = 30115;
    private static readonly int[] OuterTowers = [38, 39, 40], InnerTowers = [41, 42, 43],
        Barricades = [321, 80, 81, 83, 90, 93, 97, 101, 103, 107], BuildingMarkers = [254, 255, 256, 273, 274, 308, 299],
        SeenOnLoad = [GatePoint, 41, 42, 43, 44, 254];
    private static readonly long[] PrepareWalls = [71, 264, 265, 72, 74, 65, 19, 322, 117, 231, 232, 233, 234, 235, 243, 245, 247,
        221, 240, 241, 224, 238, 239, 225, 236, 237];

    /// <summary>A seeded siege of 1001 with both sides loaded in: the attackers' bot is the fixture's GM account.</summary>
    private async Task<(BotClient Attack, BotClient Defense)> SeededSiege(string tag, long at = 25)
    {
        var attack = await Bot("SiegeGm");
        var defense = await Bot("SiegeGd" + tag);
        long ga = await Guild(attack, "SgGa" + tag), gd = await Guild(defense, "SgGd" + tag);
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + at);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Enter(attack, 1); await Enter(defense, 2);
        return (attack, defense);
    }

    private TerritoryWarsService.SiegeRun Run(BotClient bot) => Session(bot).SiegeBattle!;

    [ClientDataFact]
    public async Task TheCastleStandsAtItsPointsAndOnlyTheOtherCampHitsIt()
    {
        var (attack, defense) = await SeededSiege("A");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var map = Service<WorldData>().Map(2230101)!;
        int Npc(int point) => run.Objects[point].Template.NpcId;

        // NpcLevelPointRespawnConfig _iMapid 2230101: both gates, the six guardians, ten barricades and the markers.
        Assert.Equal((4900005, 4900005), (Npc(GatePoint), Npc(214)));
        Assert.All(OuterTowers, p => Assert.Equal(4901012, Npc(p)));
        Assert.All(InnerTowers, p => Assert.Equal(4901011, Npc(p)));
        Assert.All(Barricades, p => Assert.Equal(4900022, Npc(p)));
        Assert.All(BuildingMarkers, p => Assert.Equal(4903002, Npc(p)));
        Assert.Equal(4900009, Npc(44));
        // The revive point's row names point 45, a group id: it stands at UIModule_NpcData's 286.
        Assert.Equal(4900010, Npc(286));
        Assert.Equal((map.Points[286].X, map.Points[286].Z), (run.Objects[286].X, run.Objects[286].Z));
        Assert.Equal((4901021, 4901022, 4901022), (Npc(267), Npc(270), Npc(271)));
        Assert.DoesNotContain(269, run.Objects.Keys); // the contested point starts with the defenders

        // Camp 2 objects for the attackers only; the camp 4 revive point and the unselectable markers for nobody.
        var wars = Service<TerritoryWarsService>();
        GameSession a = Session(attack), d = Session(defense);
        Assert.True(wars.CanHitObjective(a, run.Objects[38]));
        Assert.False(wars.CanHitObjective(d, run.Objects[38]));
        Assert.True(wars.CanHitObjective(a, run.Objects[321]));
        Assert.False(wars.CanHitObjective(a, run.Objects[286]) || wars.CanHitObjective(d, run.Objects[286]));
        Assert.False(wars.CanHitObjective(a, run.Objects[254]) || wars.CanHitObjective(d, run.Objects[254]));
        Assert.False(wars.CanHitObjective(d, run.Objects[270]) || wars.CanHitObjective(a, run.Objects[270]));

        // A barricade destroyed stays destroyed; a damaged guardian keeps its HP in the siege record.
        var barricade = run.Objects[321];
        await OnLoop(() => { barricade.Hp = 1; return Task.CompletedTask; });
        await OnLoop(async () =>
            await Service<SceneService>().TeleportInMapAsync(a, new MapPoint(barricade.X + 1.5f, barricade.Y, barricade.Z, 0)));
        await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = barricade.Uid });
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(attack, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(x => x.DstID == barricade.Uid && x.IsDead));
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        Assert.Contains(321, run.State.Destroyed);
        Assert.Same(barricade, run.Objects[321]); // not spawned again
    }

    private static GlobalObjectData? AtPoint(GlobalObjectDataNotify n, int point) =>
        n.GlobalDatas.Values.FirstOrDefault(g => g.DungeonPointId == point);

    [ClientDataFact]
    public async Task ALoadedMemberSeesTheCastleAsGlobalObjectsAndTheirHpChanges()
    {
        var (attack, defense) = await SeededSiege("B");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);

        // Loading in: every standing object at its point, alive; the building markers keep their models intact.
        var full = await Next(defense, GlobalObjectNotify, GlobalObjectDataNotify.Parser,
            n => SeenOnLoad.All(p => AtPoint(n, p) is { Hp: > 0 }));
        var gate = AtPoint(full, GatePoint)!;
        Assert.Equal((4900005L, 2L, run.Objects[GatePoint].Uid), (gate.ConfigId, gate.Camp, gate.GlobalObjId));
        Assert.Equal(run.Objects[GatePoint].Template.MaxHp, gate.MaxHp);
        Assert.Equal(run.Objects[44].Uid, AtPoint(full, 44)!.GlobalObjId);

        // An HP change goes out with the siege clock; a death as hp 0, which deletes the object in the client.
        var guardian = run.Objects[38];
        await OnLoop(() => { guardian.Hp -= 1000; return Task.CompletedTask; });
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        Assert.Equal(guardian.Template.MaxHp - 1000, AtPoint(await Next(defense, GlobalObjectNotify, GlobalObjectDataNotify.Parser,
            n => AtPoint(n, 38) is { } g && g.Hp < g.MaxHp), 38)!.Hp);
        await OnLoop(() => { guardian.Hp = 0; guardian.Dead = true; return Task.CompletedTask; });
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        await Next(attack, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n => AtPoint(n, 38) is { Hp: 0 });
        Assert.Contains(38, run.State.Destroyed);
    }

    /// <summary>Fixture: the gate and the inner city's three Knight Guardians die (the siege clock finds them dead), which
    /// opens the Crystal stage once Prepare is over.</summary>
    private async Task BreakToTheCrystal(TerritoryWarsService.SiegeRun run)
    {
        await OnLoop(() =>
        {
            foreach (int point in InnerTowers.Prepend(GatePoint)) { run.Objects[point].Hp = 0; run.Objects[point].Dead = true; }
            return Task.CompletedTask;
        });
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        Assert.Equal(3, run.State.Stage);
    }

    private static DungeonBaseInfoNotify LastBaseInfo(BotClient bot) =>
        DungeonBaseInfoNotify.Parser.ParseFrom(bot.Inbox.Snapshot().Last(e => e.IsGame(30401)).Body);

    [ClientDataFact]
    public async Task TheSiegeOpensInPrepareTheGateStageFollowsAndTheCrystalWaitsForTheLastStage()
    {
        var (attack, defense) = await SeededSiege("C");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();

        // DungeonStageModule "0" Prepare: 300 s from the siege's start, its countdown in var 512.
        var loaded = LastBaseInfo(defense);
        Assert.Equal((0, 0), (loaded.Stage, loaded.StageModuleData.Stage));
        Assert.Equal((_siege.Start * 1000, (_siege.Start + 300) * 1000),
            (loaded.StageModuleData.StageStartTime, loaded.StageModuleData.StageEndTime));
        Assert.Equal((_siege.Start + 300) * 1000, Assert.Single(loaded.Vars, v => v.VarId == 512).Vi64.Single());

        // "1" Gate after it, 1500 s at most; its countdown is var 542 [end, added].
        _clock.Set(_siege.Start + 300);
        await OnLoop(wars.ReconcileAsync);
        var gate = await Next(defense, 30401, DungeonBaseInfoNotify.Parser, i => i.StageModuleData.Stage == 1);
        Assert.Equal((1, DungeonBaseInfoNotify.Types.eChangeType.Change), (gate.Stage, gate.ChangeType));
        Assert.Equal(((_siege.Start + 300) * 1000, (_siege.Start + 1800) * 1000),
            (gate.StageModuleData.StageStartTime, gate.StageModuleData.StageEndTime));
        Assert.Equal([(_siege.Start + 1800) * 1000, 0], Assert.Single(gate.Vars, v => v.VarId == 542).Vi64);

        // The crystal waits for "3": the gate and the three Knight Guardians first.
        Assert.False(wars.CanHitObjective(Session(attack), run.Crystal!));
        await BreakToTheCrystal(run);
        // The last Knight Guardian's fall adds its 300 s (UIModule_KillAddTimeList) to the stage it opened.
        var crystal = await Next(attack, 30401, DungeonBaseInfoNotify.Parser,
            i => i.StageModuleData.Stage == 3 && i.Vars.Any(v => v.VarId == 542 && v.Vi64[1] > 0));
        Assert.Equal(Math.Min(_siege.Start + 300 + 3000 + 300, _siege.End) * 1000, crystal.StageModuleData.StageEndTime);
        Assert.True(wars.CanHitObjective(Session(attack), run.Crystal!));
        Assert.False(wars.CanHitObjective(Session(defense), run.Crystal!));
    }

    [ClientDataFact]
    public async Task TheGateHoldsThroughPrepareAndItsFallOpensTheInnerCity()
    {
        var (attack, defense) = await SeededSiege("D");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();
        var gate = run.Objects[GatePoint];
        bool Protected(MonsterUnit m) => Service<BuffService>().UnitBuffs(m).Any(b => b.ConfigId == 4904401);

        // Prepare: both gates carry Battle Start Protection, so nobody breaks them; var 513 names the gateway's gate.
        Assert.True(Protected(gate) && Protected(run.Objects[214]));
        Assert.False(wars.CanHitObjective(Session(attack), gate));
        Assert.Equal(gate.Uid, Assert.Single(LastBaseInfo(attack).Vars, v => v.VarId == 513).Vi64.Single());

        // The Gate stage: the protection goes and the attackers' camp gate leaves the field.
        _clock.Set(_siege.Start + 300);
        await OnLoop(wars.ReconcileAsync);
        await Next(attack, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n => AtPoint(n, 214) is { Hp: 0 });
        Assert.DoesNotContain(214, run.Objects.Keys);
        Assert.False(Protected(gate));
        Assert.True(wars.CanHitObjective(Session(attack), gate));
        Assert.False(wars.CanHitObjective(Session(defense), gate));

        // The attackers break it: the client deletes it (hp 0) and the inner city's stage begins.
        await OnLoop(async () =>
        {
            gate.Hp = 1;
            await Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(gate.X + 1.5f, gate.Y, gate.Z, 270));
        });
        await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = gate.Uid });
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(defense, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n => AtPoint(n, GatePoint) is { Hp: 0 });
        await Next(defense, 30401, DungeonBaseInfoNotify.Parser, i => i.StageModuleData.Stage == 2);
        Assert.Contains(GatePoint, run.State.Destroyed);
        Assert.True(run.State.Players[Session(attack).Role!.Id].CityDamage > 0); // AttackCityValue
        await attack.SendGameAsync(30117, new GetDungeonStatisticsInDungeonReq());
        var stats = (await Next(attack, 30117, GetDungeonStatisticsInDungeonResp.Parser)).DungeonStatistics;
        Assert.True(stats.SceneStatistics.Values.Single().RoleStatistics[Session(attack).Role!.Id].ExtraData[1021001].LongValue > 0);
    }

    [ClientDataFact]
    public async Task TheCastleKeepsItsDamageAfterTheFightMovesAway()
    {
        var (attack, defense) = await SeededSiege("H");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();
        _clock.Set(_siege.Start + 300); // the Gate stage: the gate can be hit
        await OnLoop(wars.ReconcileAsync);
        var a = Session(attack);

        async Task<long> HitFromBeside(MonsterUnit m)
        {
            await OnLoop(() => Service<SceneService>().TeleportInMapAsync(a, new MapPoint(m.X + 1.5f, m.Y, m.Z, 270)));
            await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = m.Uid });
            Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(attack, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == m.Uid && d.Damage > 0));
            return m.Hp;
        }

        // The gate (AIConfig 4900000, _iCanInBattle 0) takes the hit without locking its attacker.
        var gate = run.Objects[GatePoint];
        long gateHp = await HitFromBeside(gate);
        Assert.True(gateHp < gate.Template.MaxHp);
        // A Bow Guardian (20000009, _iCanInBattle 1) fights back, then loses its attacker beyond its leash.
        var guardian = run.Objects[38];
        long guardianHp = await HitFromBeside(guardian);
        Assert.True(guardianHp < guardian.Template.MaxHp);
        for (int i = 0; i < 50 && guardian.Target == null; i++) await Task.Delay(100);
        Assert.Same(a, guardian.Target); // the hit engages a fighting object by now...
        Assert.Null(gate.Target); // ...but never the gate, which was hit first
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(a, new MapPoint(guardian.X + 60, guardian.Y, guardian.Z, 0)));
        await Task.Delay(2500); // monster ticks (real time): the guardian gives up and stands at home again
        Assert.Null(guardian.Target);
        Assert.False(guardian.Returning);

        // Neither healed back to full: the next 30115 carries the damage.
        Assert.Equal(gateHp, gate.Hp);
        Assert.True(guardian.Hp < guardian.Template.MaxHp);
        await OnLoop(wars.ReconcileAsync);
        // The gate and the guardian may come in one 30115 or in two, in either order.
        long? sentGate = null;
        bool sentGuardian = false;
        await Next(defense, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n =>
        {
            if (AtPoint(n, GatePoint) is { } g && g.Hp < g.MaxHp) sentGate = g.Hp;
            if (AtPoint(n, 38) is { } h && h.Hp < h.MaxHp) sentGuardian = true;
            return sentGate != null && sentGuardian;
        });
        Assert.Equal(gateHp, sentGate);
    }

    private static long[] Walls(DungeonBaseInfoNotify info)
    {
        var walls = Assert.Single(info.Vars, v => v.VarId == 133);
        Assert.Equal((0, 0L), (walls.GroupType, walls.GroupId)); // for everyone
        return [.. walls.Vi64];
    }

    [ClientDataFact]
    public async Task AirWallsHoldTheCampTheGateAndTheBarricadesUntilTheyFall()
    {
        var (attack, defense) = await SeededSiege("E");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();
        var a = Session(attack);
        float y = run.Objects[GatePoint].Y;
        Vec3 At(float x, float z) => new() { X = x, Y = y, Z = z };
        bool SkillBlocked(Vec3 from, Vec3 to) => Service<CombatService>().SkillLineBlocked!(a, null, from, to);
        bool WalkBlocked(Vec3 from, Vec3 to) => Service<SceneService>().WalkBlocked!(a, from, to);

        // Prepare: the attackers' camp, the gateway's two gate walls, the ladder ends, barricades and fortresses.
        var prepare = Walls(LastBaseInfo(attack));
        Assert.Equal([.. PrepareWalls.Order()], prepare);
        Assert.DoesNotContain(29L, prepare); // the city wall's face and the ladder lanes stay off
        Assert.DoesNotContain(32L, prepare);
        Assert.True(WalkBlocked(At(720, 150), At(705, 150)) && SkillBlocked(At(720, 150), At(705, 150)));
        Assert.True(WalkBlocked(At(605, 150), At(590, 150)) && SkillBlocked(At(605, 150), At(590, 150)));
        Assert.False(SkillBlocked(At(605, 150), At(599.5f, 150))); // the gate stands inside its own wall

        // The Gate stage opens the camp.
        _clock.Set(_siege.Start + 300);
        await OnLoop(wars.ReconcileAsync);
        var gateStage = Walls(await Next(attack, 30401, DungeonBaseInfoNotify.Parser, i => i.StageModuleData.Stage == 1));
        Assert.DoesNotContain(71L, gateStage);
        Assert.False(WalkBlocked(At(720, 150), At(705, 150)));

        // A barricade's fall drops its wall; the gate's drops 72 and 74.
        var barricade = run.Objects[321];
        await OnLoop(() => { barricade.Hp = 0; barricade.Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        Assert.DoesNotContain(322L, Walls(await Next(attack, 30401, DungeonBaseInfoNotify.Parser,
            i => !Walls(i).Contains(322))));
        var gate = run.Objects[GatePoint];
        await OnLoop(() => { gate.Hp = 0; gate.Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        var inner = Walls(await Next(defense, 30401, DungeonBaseInfoNotify.Parser, i => i.StageModuleData.Stage == 2));
        Assert.DoesNotContain(72L, inner);
        Assert.DoesNotContain(74L, inner);
        Assert.False(WalkBlocked(At(605, 150), At(590, 150)) || SkillBlocked(At(605, 150), At(590, 150)));
    }

    [ClientDataFact]
    public async Task ACannonShotDealsATenthToTheGateAndTheBarricades()
    {
        var (attack, defense) = await SeededSiege("F");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var a = Session(attack);
        var data = Service<Ro3Emu.Server.Data.GameData>();
        long Hit(MonsterUnit m, int skill, long damage) => Service<CombatService>().DamageAdjusters.Aggregate(damage,
            (d, adjust) => adjust(a, m, Ro3Emu.Server.Data.Config.SkillConfig.Get(data, skill)!.Value.Row,
                DamageHitResult.Normal, d));

        // GVGCannonConfig 4900010's skill 4900701 (cannon 4900013, bullet 4900101: tag 3002) on tag 3003:
        // ArtilleryAttackCityGateReduceDamage [3002, 3003, -9000].
        Assert.Equal(1000, Hit(run.Objects[GatePoint], 4900701, 10000));
        Assert.Equal(1000, Hit(run.Objects[321], 4900701, 10000));
        Assert.Equal(10000, Hit(run.Objects[38], 4900701, 10000)); // a guardian has no tag
        Assert.Equal(10000, Hit(run.Objects[GatePoint], 1000001, 10000)); // a player's own attack is no cannon
    }

    [ClientDataFact]
    public async Task TheAttackersBagNpcOpensTheWarBagForVehicleOperatorsOnly()
    {
        var (attack, defense) = await SeededSiege("G");
        await using var _ = attack;
        await using var __ = defense;
        var map = Service<WorldData>().Map(2230101)!;

        // 4900201 (camp 1) in front of the attackers' born point 75; the attackers see it on arrival.
        var bag = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 4900201))).Bases.Single(b => b.EntityTemplateID == 4900201);
        Assert.Equal((2, 1L), (bag.EntityType, bag.EntityCamp));
        // LC_InteractManager passes an interactable unit's uuid through a double array to the native overlap test and
        // looks the returned id up again: every uid of the siege room must survive a double unchanged.
        Assert.Equal(bag.Uuid, (ulong)(double)bag.Uuid);
        Assert.All(Run(attack).Objects.Values.Select(u => u.Uid).Concat(Run(attack).Bags.Keys),
            uid => Assert.True(uid < 1L << 53 && (long)(double)uid == uid, $"uid {uid} does not fit a double"));
        // Its NPCConfig stats give it a full HP bar (properties 101 max HP, 2000 HP).
        Assert.True(Property(bag, 101) > 0);
        Assert.Equal(Property(bag, 101), Property(bag, 2000));
        Assert.Equal(3, MonsterService.Distance(bag.MovementInfo.Pos.X, bag.MovementInfo.Pos.Z, map.Points[75].X, map.Points[75].Z), 1);

        // Interaction 4900004 needs the Vehicle permission (NPC condition 4900003): without it nothing opens.
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(attack, bag.Uuid, 4900004));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(attack, bag.Uuid, 4900005)); // not this NPC's interaction
        await attack.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
        { ToolType = WartimeToolType.Vehicle, BagIndex = 1, RoleGuildId = Session(attack).Role!.Id });
        Assert.Equal(0, (await Next(attack, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser)).Result);
        Assert.Equal(0, await Interact(attack, bag.Uuid, 4900004));

        // The defenders' own bag NPC stands at their born point; the attackers' one is not theirs (CheckCamp).
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense),
            new MapPoint(bag.MovementInfo.Pos.X + 1, bag.MovementInfo.Pos.Y, bag.MovementInfo.Pos.Z, 0)));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(defense, bag.Uuid, 4900004));
        Assert.Contains(Run(defense).Bags.Values, b => b.NpcId == 4900202
            && MonsterService.Distance(b.X, b.Z, map.Points[73].X, map.Points[73].Z) is > 2.9f and < 3.1f);
    }
}
