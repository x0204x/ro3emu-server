using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>War machines in the territory battles of DungeonConfig _iGamePlayType 18: built, ridden and destroyed by guild.</summary>
public sealed partial class TerritoryWarsTests
{
    private const int TankItem = 1006008, TankNpc = 4900301, CannonNpc = 4900013;

    /// <summary>Two guilds bid for <paramref name="zone"/> in the round 2 auction and fight its capital battle.</summary>
    private async Task<(BotClient A, BotClient B, long Ga, long Gb)> CapitalBattle(string tag, int zone = 2, long scene = 3030401)
    {
        var a = await Bot("TwMa" + tag);
        var b = await Bot("TwMb" + tag);
        long ga = await Guild(a, "TmA" + tag), gb = await Guild(b, "TmB" + tag);
        await Bid(a, 1000, zone: zone); await Bid(b, 1100, zone: zone);
        _clock.Set(1779622201); await OnLoop(Service<TerritoryWarsService>().ReconcileAsync); // round 2 capital battle
        await Enter(a, scene); await Enter(b, scene);
        return (a, b, ga, gb);
    }

    /// <summary>The guild leader takes slot 1 of <paramref name="type"/> (and its 32285, so later waits see only new ones).</summary>
    private async Task<int> AssignSelf(BotClient bot, WartimeToolType type)
    {
        await bot.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
        { ToolType = type, BagIndex = 1, RoleGuildId = Session(bot).Role!.Id });
        int result = (await Next(bot, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser)).Result;
        if (result == 0) await Next(bot, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser, n => n.ToolType == type);
        return result;
    }

    private static async Task<int> Prepay(BotClient bot, int item)
    {
        await bot.SendGameAsync(GuildService.Prepay, new GuildWartimeStorePrepayReq { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        return (await Next(bot, GuildService.Prepay, GuildWartimeStorePrepayResp.Parser)).Result;
    }

    private static async Task<int> Build(BotClient bot, Vec3 at)
    {
        await bot.SendGameAsync(GuildService.BuildCannon, new GuildAttackCityBuildReq { Pos = at.Clone(), Face = 90 });
        return (await Next(bot, GuildService.BuildCannon, GuildAttackCityBuildResp.Parser)).Result;
    }

    private static async Task<int> UseVehicle(BotClient bot, int item)
    {
        await bot.SendGameAsync(GuildService.VehicleUse, new GuildVehicleUseReq { VehicleItem = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        return (await Next(bot, GuildService.VehicleUse, GuildVehicleUseRsp.Parser)).Result;
    }

    private static async Task<int> Interact(BotClient bot, ulong npc, int interaction)
    {
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = (long)npc, InteractionId = interaction });
        return (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result;
    }

    private static async Task<EntityBase> Appeared(BotClient bot, int npc) =>
        (await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.EntityTemplateID == (ulong)npc)))
            .Bases.Single(e => e.EntityTemplateID == (ulong)npc);

    private MonsterUnit Machine(ulong uid) => Service<World>().AllUnits().OfType<MonsterUnit>().Single(u => u.Uid == (long)uid);

    private void Stock(long guild, int item, int count)
    {
        var store = Service<GuildStore>();
        var g = store.Get(guild)!;
        g.WartimeItems = new(Service<GuildService>().WartimeStock(g)) { [item] = count };
        g.WartimeStocked = true;
        Assert.True(store.Save(g));
    }

    [ClientDataFact]
    public async Task AFirearmOperatorBuildsACannonInACapitalBattleThatOnlyAnotherGuildDestroys()
    {
        var (a, b, ga, _) = await CapitalBattle("C");
        await using var _ = a;
        await using var __ = b;
        var wars = Service<TerritoryWarsService>();
        long builder = Session(a).Role!.Id, enemy = Session(b).Role!.Id;
        Assert.Equal(0, await AssignSelf(a, WartimeToolType.Firearm));

        // The battle bag of a type-18 battle reserves and builds as the siege's does.
        var at = Session(a).Position.Clone();
        Assert.Equal(0, await Prepay(a, 1006001));
        Assert.Equal(0, await Build(a, at));
        var cannon = await Appeared(a, CannonNpc);
        // Every territory player is camp 1: the cannon names its builder's guild (guildId, custom data 104 BelongGuild).
        Assert.Equal((2, builder, 1L, ga), (cannon.EntityType, cannon.Owner, cannon.EntityCamp, cannon.GuildId));
        Assert.Contains(cannon.CustomDatas, d => d.Id == 104 && d.CustomParam.LongParam == ga);
        Assert.Equal(Session(a).Role!.Name, cannon.Name);
        await Next(a, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser, n => n.RoleUsingToolId == (long)cannon.Uuid);
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);

        // The other guild's member may not man it (CheckBuilder, and CheckCamp by guild); its builder does.
        var pos = cannon.MovementInfo.Pos;
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(b), new MapPoint(pos.X, pos.Y, pos.Z + 1, 0)));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(b, cannon.Uuid, 4900005));
        Assert.Equal(0, await Interact(a, cannon.Uuid, 4900005));
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Gvgcannon);

        // Its shot hits the enemy guild's member where it lands.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(b), new MapPoint(pos.X, pos.Y, pos.Z + 6.3f, 0)));
        long? before = Session(b).CurrentHp;
        await a.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq
        {
            ButtonId = 2,
            CannonUseSkill = new CannonUseSkill
            {
                UseSkillParam = new CastSkillReq { SkillId = 4900701 },
                BulletExtraParam = { new BulletExtraParam { Gravity = 1500, Speed = 4000, FaceVector3D = new Vec3 { Y = 1, Z = 0.02f } } },
            },
        });
        Assert.Equal(0, (await Next(a, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Session(b).CurrentHp == before && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.NotEqual(before, Session(b).CurrentHp);

        // Only another guild hits it; its last blow destroys it, puts the operator on foot and clears the slot.
        var unit = Machine(cannon.Uuid);
        Assert.False(wars.CanHitObjective(Session(a), unit));
        Assert.True(wars.CanHitObjective(Session(b), unit));
        await OnLoop(async () =>
        {
            unit.Hp = 1;
            await Service<SceneService>().TeleportInMapAsync(Session(b), new MapPoint(pos.X + 1, pos.Y, pos.Z, 0));
        });
        await b.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)cannon.Uuid });
        Assert.Equal(0, (await Next(b, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(a, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.ToolType == WartimeToolType.Firearm && n.RoleUsingToolId == 0);

        // The personal record counts the machine destroyed and the damage dealt to it (1021002, 1021003).
        GetDungeonStatisticsInDungeonResp report = null!;
        deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            await b.SendGameAsync(30117, new GetDungeonStatisticsInDungeonReq());
            report = await Next(b, 30117, GetDungeonStatisticsInDungeonResp.Parser);
        }
        while (report.DungeonStatistics.SceneStatistics.Values.Single().RoleStatistics[enemy].ExtraData[1021002].LongValue == 0
               && DateTime.UtcNow < deadline);
        var mine = report.DungeonStatistics.SceneStatistics.Values.Single().RoleStatistics[enemy].ExtraData;
        Assert.Equal(1, mine[1021002].LongValue);
        Assert.True(mine[1021003].LongValue > 0);
        Assert.True(mine[1001001].LongValue >= mine[1021003].LongValue);
    }

    [ClientDataFact]
    public async Task AnotherGuildCannotBoardATankAndTheSettlementTakesItAway()
    {
        var (a, b, ga, _) = await CapitalBattle("V");
        await using var _ = a;
        await using var __ = b;
        var wars = Service<TerritoryWarsService>();
        long driver = Session(a).Role!.Id;
        Assert.Equal(0, await AssignSelf(a, WartimeToolType.Vehicle));
        Assert.Equal(0, await AssignSelf(b, WartimeToolType.Vehicle)); // the other guild's own Vehicle operator
        await OnLoop(() => { Stock(ga, TankItem, 1); return Task.CompletedTask; });

        Assert.Equal(0, await UseVehicle(a, TankItem));
        var tank = await Appeared(a, TankNpc);
        Assert.Equal((driver, 1L, ga), (tank.Owner, tank.EntityCamp, tank.GuildId));
        Assert.Contains(tank.CustomDatas, d => d.Id == 104 && d.CustomParam.LongParam == ga);
        await Next(a, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser, n => n.RoleUsingToolId == (long)tank.Uuid);

        // Same camp, other guild: an enemy vehicle (VehicleHasEmptySeat, CheckCamp), so its seats are refused.
        var pos = tank.MovementInfo.Pos;
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(b), new MapPoint(pos.X + 1, pos.Y, pos.Z, 0)));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(b, tank.Uuid, 4900022));
        Assert.True(wars.CanHitObjective(Session(b), Machine(tank.Uuid)));
        Assert.False(wars.CanHitObjective(Session(a), Machine(tank.Uuid)));
        Assert.Equal(0, await Interact(a, tank.Uuid, 4900022));
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == driver && n.DataInfo.EType == InteractInfo.Types.EState.Vehicle);

        // The settlement takes the tank away: the driver gets off and the slot shows nothing in use.
        _clock.Set(1779625801); await OnLoop(wars.ReconcileAsync);
        var result = await Next(a, TerritoryWarsService.ResultNotify, TerritoryWarsDungeonResultNotify.Parser);
        var record = result.CommonResultInfo.SceneStatistics.Values.Single().RoleStatistics[driver].ExtraData;
        Assert.Equal((0L, 0L), (record[1021002].LongValue, record[1021003].LongValue));
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == driver && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(a, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(tank.Uuid));
        await Next(a, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.ToolType == WartimeToolType.Vehicle && n.RoleUsingToolId == 0);
        Assert.False(Service<GuildService>().WarMachines!.Standing((long)tank.Uuid));
    }

    [ClientDataFact]
    public async Task MachinesAreRefusedInATerritoryBattleWithoutTheMachineUi()
    {
        // Zone 13 fights in 3032001, DungeonConfig _iGamePlayType 6: no Instrument button, no machines.
        var (a, b, _, gb) = await CapitalBattle("S", zone: 13, scene: 3032001);
        await using var _ = a;
        await using var __ = b;
        Assert.Equal(0, await AssignSelf(a, WartimeToolType.Firearm));
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Prepay(a, 1006001));
        Assert.Equal(0, await AssignSelf(b, WartimeToolType.Vehicle));
        await OnLoop(() => { Stock(gb, TankItem, 1); return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.FeatureUnavailable, await UseVehicle(b, TankItem));
    }
}
