using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>War vehicles in a running siege: one from the guild warehouse (32281), its seats (32700, 32708), driving and
/// getting off.</summary>
public sealed partial class SiegeTests
{
    private const int TankItem = 1006008, TankNpc = 4900301;

    private static async Task<int> UseVehicle(BotClient bot, int item)
    {
        await bot.SendGameAsync(GuildService.VehicleUse, new GuildVehicleUseReq { VehicleItem = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        return (await Next(bot, GuildService.VehicleUse, GuildVehicleUseRsp.Parser)).Result;
    }

    private async Task<(BotClient Attack, BotClient Defense, long Ga)> SiegeWithVehicle(string tag)
    {
        var attack = await Bot("SiegeGm");
        var defense = await Bot("SiegeVh" + tag);
        long ga = await Guild(attack, "SgVh" + tag), gd = await Guild(defense, "SgVd" + tag);
        await attack.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
        { ToolType = WartimeToolType.Vehicle, BagIndex = 1, RoleGuildId = Session(attack).Role!.Id });
        Assert.Equal(0, (await Next(attack, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser)).Result);
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + 25);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Base(attack, currentCity: 1001);
        return (attack, defense, ga);
    }

    [ClientDataFact]
    public async Task AVehicleOperatorDrivesATankFromTheWarehouseAndGetsOff()
    {
        var (attack, defense, ga) = await SiegeWithVehicle("A");
        await using var _ = attack;
        await using var __ = defense;
        Assert.Equal(ErrorCodes.FeatureUnavailable, await UseVehicle(attack, TankItem)); // not in a siege yet
        await Enter(attack, 1); await Enter(defense, 2);
        long driver = Session(attack).Role!.Id;

        // The starting warehouse holds only cannons; vehicles need stock, the Vehicle permission and a vehicle item.
        Assert.Equal(ErrorCodes.GuildSupplies, await UseVehicle(attack, TankItem));
        Assert.Equal(ErrorCodes.Parameter, await UseVehicle(attack, 1006001));
        Assert.Equal(ErrorCodes.GuildPermission, await UseVehicle(defense, TankItem));
        await OnLoop(() =>
        {
            var g = Service<GuildStore>().Get(ga)!;
            g.WartimeItems = new(Service<GuildService>().WartimeStock(g)) { [TankItem] = 2 };
            g.WartimeStocked = true;
            Assert.True(Service<GuildStore>().Save(g));
            return Task.CompletedTask;
        });

        // The tank stands beside its owner: an NPC of its camp naming it, and the owner's slot shows it.
        Assert.Equal(0, await UseVehicle(attack, TankItem));
        var tank = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == TankNpc))).Bases.Single(b => b.EntityTemplateID == TankNpc);
        Assert.Equal((2, driver, 1L), (tank.EntityType, tank.Owner, tank.EntityCamp));
        Assert.Equal((long)tank.Uuid, (await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.RoleUsingToolId != 0)).RoleUsingToolId);
        await Next(attack, GuildService.WartimeStore, GuildWartimeStoreItemNtf.Parser,
            n => n.WartimeItems.Any(i => i.ItemId == TankItem && i.ItemCount == 1));

        // The enemy may not take its seats (CheckCamp, CheckGVGPermission); its owner drives (seat 1, interaction 4900022).
        var pos = tank.MovementInfo.Pos;
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(pos.X + 1, pos.Y, pos.Z, 0)));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(defense, tank.Uuid, 4900022));
        Assert.Equal(0, await Interact(attack, tank.Uuid, 4900022));
        var seated = await Next(attack, WorldCoreService.AoiInfo, AoIInfoTotal.Parser,
            t => t.AoiData.Any(a => a.EntityUuid == (ulong)driver && a.CarryInfo?.BeCarried?.Id == tank.Uuid));
        var carried = seated.AoiData.Single(a => a.EntityUuid == (ulong)driver).CarryInfo.BeCarried;
        Assert.Equal((0, 0), (carried.CarrySlot, carried.CarryType));
        Assert.Contains(seated.AoiData, a => a.EntityUuid == tank.Uuid && a.CarryInfo.AddCarries.Any(c => c.Id == (ulong)driver));
        var state = await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == driver && n.DataInfo.EType == InteractInfo.Types.EState.Vehicle);
        Assert.Equal(((long)tank.Uuid, 3L, 4900022), (state.DataInfo.ActiveObjId, state.DataInfo.StateId, state.DataInfo.InteractConfigId));
        Assert.Equal(9, (await Next(attack, WorldInteractionService.Change3C, Change3CNtf.Parser)).CfgId);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(attack, tank.Uuid, 4900023)); // one seat at a time

        // The driver's walk is the tank's: the enemy sees the tank move.
        await attack.WalkAsync(pos.X, pos.Y, pos.Z + 2);
        var moved = await Next(defense, GameAoiSink.MovementChange, MovementChangeNtf.Parser, m => m.Uid == (long)tank.Uuid);
        Assert.Equal(pos.Z + 2, moved.MovementInfo.Pos.Z, 2);

        // The driver seat's skills are castable without learning them (VehicleSeatConfig _kMainSkills).
        await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 4906601 });
        Assert.NotEqual(ErrorCodes.SkillNotLearned, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Another seat (32708, 0-based): the gunner's (seat 2, enum_PassengerType Killer) with its own camera.
        await attack.SendGameAsync(WorldInteractionService.SeatChange, new ChangeVehicleSeatReq { SeatIdx = 1 });
        Assert.Equal((1, 0), await SeatChanged(attack));
        await Next(attack, WorldCoreService.AoiInfo, AoIInfoTotal.Parser,
            t => t.AoiData.Any(a => a.EntityUuid == (ulong)driver && a.CarryInfo?.BeCarried is { CarrySlot: 1, CarryType: 1 }));

        // The exit action (4900025) gets off: Idle, then the ride ends in the carry info.
        Assert.Equal(0, await Interact(attack, tank.Uuid, 4900025));
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == driver && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(attack, WorldCoreService.AoiInfo, AoIInfoTotal.Parser,
            t => t.AoiData.Any(a => a.EntityUuid == (ulong)driver && a.CarryInfo?.BeCarried is { Id: 0 }));

        // A second tank replaces the first one.
        Assert.Equal(0, await UseVehicle(attack, TankItem));
        await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(tank.Uuid));
    }

    private static async Task<(int Seat, int Result)> SeatChanged(BotClient bot)
    {
        var rsp = await Next(bot, WorldInteractionService.SeatChange, ChangeVehicleSeatRes.Parser);
        return (rsp.SeatIdx, rsp.Result);
    }
}
