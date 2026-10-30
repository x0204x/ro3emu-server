using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The siege's moving and fighting units (TerritoryWarsService.SiegeUnits.cs, SiegeVehicles.cs): the ladders,
/// the ram, the archers, the guide arrows, the fixed vehicles and the kill time bonus.</summary>
public sealed partial class SiegeTests
{
    private const int LeftLadder = 46, RightLadder = 47, RamPoint = 48;

    /// <summary>The Gate stage: Prepare's 300 s are over.</summary>
    private async Task GateStage(TerritoryWarsService.SiegeRun run)
    {
        _clock.Set(_siege.Start + 300);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        Assert.Equal(1, run.State.Stage);
    }

    /// <summary>Fixture: the unit stands just short of its march's goal, so the next monster tick finds it arrived.</summary>
    private async Task Arrive(MonsterUnit unit)
    {
        await OnLoop(() =>
        {
            var march = unit.March!;
            unit.Moving = false;
            march.Route.Clear();
            march.Halted = false;
            (unit.X, unit.Z) = (march.GoalX + 0.4f, march.GoalZ);
            return Task.CompletedTask;
        });
        for (int i = 0; i < 50 && !unit.March!.Arrived; i++) await Task.Delay(100);
        Assert.True(unit.March!.Arrived);
    }

    private static async Task Until(Func<bool> done, int tenths = 80)
    {
        for (int i = 0; i < tenths && !done(); i++) await Task.Delay(100);
        Assert.True(done());
    }

    [ClientDataFact]
    public async Task TheLaddersTheRamTheArchersTheArrowsAndTheVehiclesStandWithTheCastle()
    {
        var (attack, defense) = await SeededSiege("U1");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var map = Service<WorldData>().Map(2230101)!;
        int Npc(int point) => run.Objects[point].Template.NpcId;

        // UIModule_NpcData: 46 the left ladder, 47 the ladder, 48 the ram; the rows at 20 and 34 stand on the same spots.
        Assert.Equal((4900012, 4900000, 4900002), (Npc(LeftLadder), Npc(RightLadder), Npc(RamPoint)));
        Assert.DoesNotContain(20, run.Objects.Keys);
        Assert.DoesNotContain(34, run.Objects.Keys);
        // The 38 archers of group 248; the guide arrows at their editor height (_iNotGroundBorn), not at the origin.
        Assert.Equal(38, run.Objects.Values.Count(u => u.Template.NpcId == 4901001));
        Assert.Equal(11, run.Objects.Values.Count(u => u.Template.NpcId == 4903001));
        Assert.Equal(map.Points[216].Y, run.Objects[216].Y);
        Assert.DoesNotContain(16, run.Objects.Keys);
        Assert.DoesNotContain(272, run.Objects.Keys);
        // The fixed Madogears: camp 1 at 301/251/250 and camp 2 at 253/302/303, each its camp's guild's, owned by nobody.
        var steel = Assert.IsType<TerritoryWarsService.VehicleUnit>(run.Objects[301]);
        Assert.Equal((4901004, 1L, run.State.Attack, 0L), (steel.NpcId, steel.Camp, steel.Guild, steel.Owner));
        var defenders = Assert.IsType<TerritoryWarsService.VehicleUnit>(run.Objects[253]);
        Assert.Equal((4901007, 2L, run.State.Defense), (defenders.NpcId, defenders.Camp, defenders.Guild));
        Assert.All(run.Objects.Values, u => Assert.True(u.Uid < 1L << 53 && (long)(double)u.Uid == u.Uid));

        // 30115 shows the ladders, the ram and the vehicles (_iNeedNotifyGlobalData 1), not the archers (0).
        var full = await Next(attack, GlobalObjectNotify, GlobalObjectDataNotify.Parser,
            n => new[] { LeftLadder, RightLadder, RamPoint, 301 }.All(p => AtPoint(n, p) is { Hp: > 0 }));
        Assert.Null(AtPoint(full, 76));

        // Prepare: nothing moves yet; the top bar's state (511) is the stage.
        Assert.Null(run.Objects[RamPoint].March);
        Assert.Equal(0, Assert.Single(LastBaseInfo(defense).Vars, v => v.VarId == 511).Vi64.Single());

        // The castle's units take only the other camp: an archer (camp 2) spares the defenders, the ram (camp 1) the attackers.
        var spares = Service<MonsterService>().Spares!;
        GameSession a = Session(attack), d = Session(defense);
        Assert.True(spares(run.Objects[76], d));
        Assert.False(spares(run.Objects[76], a));
        Assert.True(spares(run.Objects[RamPoint], a));
        // The defenders break the attackers' ladders and ram; the attackers the archers.
        var wars = Service<TerritoryWarsService>();
        Assert.True(wars.CanHitObjective(d, run.Objects[LeftLadder]) && !wars.CanHitObjective(a, run.Objects[LeftLadder]));
        Assert.True(wars.CanHitObjective(d, run.Objects[RamPoint]));
        Assert.True(wars.CanHitObjective(a, run.Objects[76]) && !wars.CanHitObjective(d, run.Objects[76]));
        Assert.False(wars.CanHitObjective(a, run.Objects[215]) || wars.CanHitObjective(d, run.Objects[215])); // arrows

        // DefenseValue (1021004): what the defenders take from the attackers' ladders and ram.
        var ladder = run.Objects[RightLadder];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(d, new MapPoint(ladder.X + 1.5f, ladder.Y, ladder.Z, 270)));
        await defense.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = ladder.Uid });
        Assert.Equal(0, (await Next(defense, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(defense, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(x => x.DstID == ladder.Uid && x.Damage > 0));
        Assert.True(run.State.Players[d.Role!.Id].DefenseValue > 0);
        await defense.SendGameAsync(30117, new GetDungeonStatisticsInDungeonReq());
        var stats = (await Next(defense, 30117, GetDungeonStatisticsInDungeonResp.Parser)).DungeonStatistics;
        Assert.True(stats.SceneStatistics.Values.Single().RoleStatistics[d.Role.Id].ExtraData[1021004].LongValue > 0);
    }

    [ClientDataFact]
    public async Task ALadderAtTheWallOpensItsLaneAndComesBackAfterItFalls()
    {
        var (attack, defense) = await SeededSiege("U2");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();
        var map = Service<WorldData>().Map(2230101)!;
        await GateStage(run);
        Assert.Equal(1, Assert.Single(LastBaseInfo(attack).Vars, v => v.VarId == 511).Vi64.Single());

        // The Gate stage sets the ladders off for their ends at the city wall (46 -> 18).
        var ladder = run.Objects[LeftLadder];
        Assert.Equal((map.Points[18].X, map.Points[18].Z), (ladder.March!.GoalX, ladder.March.GoalZ));
        await Arrive(ladder);
        await OnLoop(wars.ReconcileAsync);
        Assert.Contains(LeftLadder, run.State.PlacedLadders);
        // Against the wall: the city wall's air wall there (65) goes and the lane's rails (67, 68) come; 47's wall stays.
        var placed = Walls(await Next(attack, 30401, DungeonBaseInfoNotify.Parser, i => !Walls(i).Contains(65)));
        Assert.Contains(67L, placed);
        Assert.Contains(68L, placed);
        Assert.Contains(19L, placed);
        Assert.DoesNotContain(32L, placed);
        // Its lane carries a climb over the wall's blocked cells; the other lane has no ladder.
        var bridged = Service<SceneService>().WalkBridged!;
        GameSession a = Session(attack);
        Assert.True(bridged(a, new Vec3 { X = 605, Y = 14.2f, Z = 92.5f }, new Vec3 { X = 596, Y = 29, Z = 92.5f }));
        Assert.False(bridged(a, new Vec3 { X = 605, Y = 14.2f, Z = 206 }, new Vec3 { X = 596, Y = 29, Z = 206 }));

        // It falls: 30115 deletes it, the wall closes again and it comes back after 70-75 s
        // (UIModule_NpcRefreshDuration), which 30401 npcRefreshModuleData counts down.
        await OnLoop(() => { ladder.Hp = 0; ladder.Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        await Next(attack, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n => AtPoint(n, LeftLadder) is { Hp: 0 });
        Assert.DoesNotContain(LeftLadder, run.State.Destroyed);
        Assert.DoesNotContain(LeftLadder, run.Objects.Keys);
        var waiting = await Next(attack, 30401, DungeonBaseInfoNotify.Parser,
            i => i.NpcRefreshModuleData?.NpcRefreshTime.ContainsKey(LeftLadder) == true);
        var refresh = waiting.NpcRefreshModuleData.NpcRefreshTime[LeftLadder];
        Assert.Equal((LeftLadder, _clock.GetUtcNow().ToUnixTimeMilliseconds()), (refresh.NpcPointId, refresh.RefreshStartTime));
        Assert.InRange(refresh.RefreshEndTime - refresh.RefreshStartTime, 70000, 75000);
        Assert.Contains(65L, Walls(waiting));
        Assert.DoesNotContain(67L, Walls(waiting));

        _clock.Set(_siege.Start + 300 + 76);
        await OnLoop(wars.ReconcileAsync);
        var back = run.Objects[LeftLadder];
        Assert.NotSame(ladder, back);
        Assert.False(back.Dead);
        Assert.Empty(run.State.Refresh);
        await Next(attack, 30401, DungeonBaseInfoNotify.Parser, i => i.NpcRefreshModuleData is { NpcRefreshTime.Count: 0 });
        await Next(attack, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n => AtPoint(n, LeftLadder) is { Hp: > 0 } g
            && g.GlobalObjId == back.Uid);
    }

    [ClientDataFact]
    public async Task TheRamMovesWhileEscortedAndBreaksTheGateFromItsEnd()
    {
        var (attack, defense) = await SeededSiege("U3");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();
        var map = Service<WorldData>().Map(2230101)!;
        await GateStage(run);
        var ram = run.Objects[RamPoint];
        var gate = run.Objects[GatePoint];
        Assert.Equal((map.Points[35].X, map.Points[35].Z), (ram.March!.GoalX, ram.March.GoalZ));

        // Nobody of the attackers within its 32 m circle (they stand in their camp, 58 m off): it waits.
        await OnLoop(wars.ReconcileAsync);
        Assert.True(ram.March.Halted);
        float startX = ram.X;
        // An attacker beside it pushes it on towards the gate.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(ram.X + 5, ram.Y, ram.Z, 270)));
        await OnLoop(wars.ReconcileAsync);
        Assert.False(ram.March.Halted);
        await Until(() => ram.X < startX - 0.5f);

        // At its end it strikes the gate with its ATK each second (no skill: its _kAttriScale raises its PATK).
        await Arrive(ram);
        await OnLoop(wars.ReconcileAsync);
        await Until(() => gate.Hp < gate.Template.MaxHp);
        Assert.Equal(0, (gate.Template.MaxHp - gate.Hp) % ram.Template.Atk);
        Assert.Equal(210240, ram.Template.Atk);

        // It breaks the gate: the inner city's stage begins; a ram that falls after it does not come back.
        await OnLoop(() => { gate.Hp = 1; ram.NextSwingMs = 0; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        await Until(() => run.State.Stage == 2);
        Assert.Contains(GatePoint, run.State.Destroyed);
        Assert.Equal(0, run.State.Players[Session(attack).Role!.Id].CityDamage); // nobody's blow
        await OnLoop(() => { ram.Hp = 0; ram.Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        Assert.Contains(RamPoint, run.State.Destroyed);
        Assert.Empty(run.State.Refresh);
    }

    [ClientDataFact]
    public async Task ArchersShootTheAttackersInSightAndTheLaddersAtTheWall()
    {
        var (attack, defense) = await SeededSiege("U4");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        await GateStage(run);
        var archer = run.Objects[76];

        // A defender below the archer is spared; an attacker there is shot at.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(610, 14.2f, 107, 270)));
        await Task.Delay(1500);
        Assert.Null(archer.Target);
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(612, 14.2f, 107, 270)));
        await Until(() => ReferenceEquals(archer.Target, Session(attack)));
        await Next(attack, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == archer.Uid);
        // Out of its reach it lets go.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(700, 14.2f, 150, 270)));
        await Until(() => archer.Target == null);

        // A ladder against the wall is within the wall's archers' reach: they shoot it (at half damage, tag 3004 on 3005).
        var ladder = run.Objects[LeftLadder];
        await Arrive(ladder);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        await Until(() => ladder.Hp < ladder.Template.MaxHp, tenths: 120);
    }

    [ClientDataFact]
    public async Task AGuardiansFallAddsItsTimeToTheStageCountdown()
    {
        var (attack, defense) = await SeededSiege("U5");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var wars = Service<TerritoryWarsService>();
        await GateStage(run);
        long gateEnd = _siege.Start + 300 + 1500;

        // UIModule_KillAddTimeList "0" [120, 300]: an Archer Guardian (Bow Guardian, OuterTower) +120 s, var 542 [end, added].
        var bow = run.Objects[38];
        await OnLoop(() => { bow.Hp = 0; bow.Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        var added = await Next(attack, 30401, DungeonBaseInfoNotify.Parser,
            i => i.Vars.Any(v => v.VarId == 542 && v.Vi64[1] == 120000));
        Assert.Equal([(gateEnd + 120) * 1000, 120000], Assert.Single(added.Vars, v => v.VarId == 542).Vi64);
        Assert.Equal((gateEnd + 120) * 1000, added.StageModuleData.StageEndTime);
        // A Knight Guardian (InnerTower) +300 s.
        var knight = run.Objects[41];
        await OnLoop(() => { knight.Hp = 0; knight.Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        await Next(defense, 30401, DungeonBaseInfoNotify.Parser,
            i => i.Vars.Any(v => v.VarId == 542 && v.Vi64[0] == (gateEnd + 420) * 1000 && v.Vi64[1] == 300000));
        // The next stage starts its own countdown, without the additions.
        await OnLoop(() => { run.Objects[GatePoint].Hp = 0; run.Objects[GatePoint].Dead = true; return Task.CompletedTask; });
        await OnLoop(wars.ReconcileAsync);
        var inner = await Next(defense, 30401, DungeonBaseInfoNotify.Parser, i => i.StageModuleData.Stage == 2);
        Assert.Equal(0, Assert.Single(inner.Vars, v => v.VarId == 542).Vi64[1]);
    }

    [ClientDataFact]
    public async Task TheCampsFixedVehiclesCarryItsOperatorsAndStayDestroyed()
    {
        var (attack, defense) = await SeededSiege("U6");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        var steel = run.Objects[301];
        var at = new MapPoint(steel.X + 1, steel.Y, steel.Z, 0);
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(attack), at));
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense), at));

        // Interaction 4900022 needs the Vehicle permission and the vehicle's camp, as a vehicle from the warehouse.
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(attack, (ulong)steel.Uid, 4900022));
        await attack.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
        { ToolType = WartimeToolType.Vehicle, BagIndex = 1, RoleGuildId = Session(attack).Role!.Id });
        Assert.Equal(0, (await Next(attack, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser)).Result);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(defense, (ulong)steel.Uid, 4900022));
        Assert.Equal(0, await Interact(attack, (ulong)steel.Uid, 4900022));
        long driver = Session(attack).Role!.Id;
        await Next(attack, WorldCoreService.AoiInfo, AoIInfoTotal.Parser,
            t => t.AoiData.Any(x => x.EntityUuid == (ulong)driver && x.CarryInfo?.BeCarried?.Id == (ulong)steel.Uid));

        // The defenders destroy it: its rider gets off and it stays destroyed for the siege.
        Assert.True(Service<TerritoryWarsService>().CanHitObjective(Session(defense), steel));
        await OnLoop(() => { steel.Hp = 1; return Task.CompletedTask; });
        await defense.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = steel.Uid });
        Assert.Equal(0, (await Next(defense, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(attack, WorldCoreService.AoiInfo, AoIInfoTotal.Parser,
            t => t.AoiData.Any(x => x.EntityUuid == (ulong)driver && x.CarryInfo?.BeCarried is { Id: 0 }));
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        await Next(attack, GlobalObjectNotify, GlobalObjectDataNotify.Parser, n => AtPoint(n, 301) is { Hp: 0 });
        Assert.Contains(301, run.State.Destroyed);
    }

    [ClientDataFact]
    public async Task TheTestAidEndsPrepareAndListsTheUnits()
    {
        var (attack, defense) = await SeededSiege("U7");
        await using var _ = attack;
        await using var __ = defense;
        var run = Run(attack);
        Assert.Equal(0, run.State.Stage);
        await attack.SendChatAsync("4_1", 2, "!siege prepare", channelIndex: 4);
        await Next(attack, 30401, DungeonBaseInfoNotify.Parser, i => i.StageModuleData.Stage == 1);
        Assert.Equal((_siege.Start + 25) * 1000, LastBaseInfo(attack).StageModuleData.StageStartTime);
        Assert.NotNull(run.Objects[RamPoint].March);
        await attack.SendChatAsync("4_1", 2, "!siege objects", channelIndex: 4);
        var listed = await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser, n => n.Content.Contains("48=4900002"));
        Assert.Contains("archers 38", listed.Content);
    }
}
