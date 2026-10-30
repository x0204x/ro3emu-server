using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>War machines in combat: their HP, the repair zone (1006010) and their destruction by the other camp.</summary>
public sealed partial class SiegeTests
{
    private const int RepairItem = 1006010, RepairZoneNpc = 4901010, RepairBuff = 4903501;

    private MonsterUnit Machine(ulong uid) =>
        Service<World>().AllUnits().OfType<MonsterUnit>().Single(u => u.Uid == (long)uid);

    private static long Property(EntityBase entity, long idx) => entity.Property.Single(p => p.Idx == idx).Data;

    [ClientDataFact]
    public async Task ARepairZoneHealsATankAndTheEnemyDestroysItWithItsRiderOnBoard()
    {
        var (attack, defense, ga) = await SiegeWithVehicle("H");
        await using var _ = attack;
        await using var __ = defense;
        await Enter(attack, 1); await Enter(defense, 2);
        long driver = Session(attack).Role!.Id;
        await OnLoop(() =>
        {
            var g = Service<GuildStore>().Get(ga)!;
            g.WartimeItems = new(Service<GuildService>().WartimeStock(g)) { [TankItem] = 1, [RepairItem] = 1 };
            g.WartimeStocked = true;
            Assert.True(Service<GuildStore>().Save(g));
            return Task.CompletedTask;
        });

        // The tank has an HP bar (NPCConfig _iNPCType 2 with AttriMonsterConfig stats): full on arrival.
        Assert.Equal(0, await UseVehicle(attack, TankItem));
        var tank = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == TankNpc))).Bases.Single(b => b.EntityTemplateID == TankNpc);
        long max = Property(tank, 101);
        Assert.True(max > 0);
        Assert.Equal(max, Property(tank, 2000));
        var unit = Machine(tank.Uuid);
        // A hit neither locks it nor leaves it to heal to full when the fight moves away (MonsterService returning home).
        Assert.True(unit.KeepsDamage && unit.StaysOutOfBattle);
        Assert.Equal(0, await Interact(attack, tank.Uuid, 4900022));

        // Only the other camp may hit it.
        var territory = Service<TerritoryWarsService>();
        Assert.False(territory.CanHitObjective(Session(attack), unit));
        Assert.True(territory.CanHitObjective(Session(defense), unit));

        // A repair zone at the driver's feet: the tank carries the repair buff and heals 1% of its max HP a second.
        await OnLoop(() => { unit.Hp = max / 2; return Task.CompletedTask; });
        Assert.Equal(0, await UseVehicle(attack, RepairItem));
        var zone = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == RepairZoneNpc))).Bases.Single(b => b.EntityTemplateID == RepairZoneNpc);
        Assert.Equal((4, driver), (zone.EntityType, zone.Owner));
        await Next(attack, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == (long)tank.Uuid && n.Buffs.Any(b => b.ConfigId == RepairBuff));
        var healed = await Next(attack, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            a => a.EntityUuid == tank.Uuid && a.Property.Any(p => p.Idx == 2000));
        Assert.Equal(max / 2 + max / 100, healed.Property.Single(p => p.Idx == 2000).Data);
        Assert.Equal(ErrorCodes.GuildSupplies, await UseVehicle(attack, RepairItem)); // the warehouse had one

        // The enemy's last blow destroys it: the rider is put back on foot and the slot shows nothing in use.
        var pos = tank.MovementInfo.Pos;
        await OnLoop(async () =>
        {
            unit.Hp = 1;
            await Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(pos.X + 1, pos.Y, pos.Z, 0));
        });
        await defense.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)tank.Uuid });
        Assert.Equal(0, (await Next(defense, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(defense, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == (long)tank.Uuid && d.IsDead));
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == driver && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.ToolType == WartimeToolType.Vehicle && n.RoleUsingToolId == 0);
        Assert.False(Service<GuildService>().WarMachines!.Standing((long)tank.Uuid));
    }

    [ClientDataFact]
    public async Task TheEnemyDestroysABuiltCannonAndItsOperatorGetsOff()
    {
        var (attack, defense, _) = await SiegeWithFirearm("D");
        await using var _ = attack;
        await using var __ = defense;
        await Enter(attack, 1); await Enter(defense, 2);
        long builder = Session(attack).Role!.Id;
        var at = Session(attack).Position.Clone();
        Assert.Equal(0, (await Prepay(attack, 1006001)).Result);
        Assert.Equal(0, (await Build(attack, at.X, at.Y, at.Z)).Result);
        var cannon = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 4900013))).Bases.Single(b => b.EntityTemplateID == 4900013);
        Assert.True(Property(cannon, 101) > 0);
        Assert.Equal(0, await Interact(attack, cannon.Uuid, 4900005));
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Gvgcannon);

        var unit = Machine(cannon.Uuid);
        Assert.True(unit.KeepsDamage && unit.StaysOutOfBattle);
        var pos = cannon.MovementInfo.Pos;
        await OnLoop(async () =>
        {
            unit.Hp = 1;
            await Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(pos.X + 1, pos.Y, pos.Z, 0));
        });
        await defense.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)cannon.Uuid });
        Assert.Equal(0, (await Next(defense, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(cannon.Uuid));
        Assert.Equal(ErrorCodes.NpcMissing, await Interact(attack, cannon.Uuid, 4900005)); // the wreck is gone

        // The siege record counts the destroyed machine and the damage dealt to it (DestroyMachinery, MachineryDamage).
        await defense.SendGameAsync(30117, new GetDungeonStatisticsInDungeonReq());
        var report = await Next(defense, 30117, GetDungeonStatisticsInDungeonResp.Parser);
        var record = report.DungeonStatistics.SceneStatistics.Values.Single().RoleStatistics[Session(defense).Role!.Id].ExtraData;
        Assert.Equal(1, record[1021002].LongValue);
        Assert.True(record[1021003].LongValue > 0);
    }
}
