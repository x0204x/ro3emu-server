using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>War machines in a running siege: reserving one from the guild warehouse (32270), giving it back (32272) and
/// building it (32271).</summary>
public sealed partial class SiegeTests
{
    private static async Task<GuildWartimeStorePrepayResp> Prepay(BotClient bot, int item)
    {
        await bot.SendGameAsync(GuildService.Prepay, new GuildWartimeStorePrepayReq { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        return await Next(bot, GuildService.Prepay, GuildWartimeStorePrepayResp.Parser);
    }

    private static async Task<int> Stocked(BotClient bot, int item, int count) =>
        (await Next(bot, GuildService.WartimeStore, GuildWartimeStoreItemNtf.Parser,
            n => n.WartimeItems.Any(i => i.ItemId == item && i.ItemCount == count))).WartimeItems.Single(i => i.ItemId == item).ItemCount;

    private async Task<(BotClient Attack, BotClient Defense, long Ga)> SiegeWithFirearm(string tag)
    {
        var attack = await Bot("SiegeGm"); // the fixture's GM account seeds the siege
        var defense = await Bot("SiegeWm" + tag);
        long ga = await Guild(attack, "SgWm" + tag), gd = await Guild(defense, "SgWd" + tag);
        await attack.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq
        { ToolType = WartimeToolType.Firearm, BagIndex = 1, RoleGuildId = Session(attack).Role!.Id });
        Assert.Equal(0, (await Next(attack, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser)).Result);
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + 25);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Base(attack, currentCity: 1001);
        return (attack, defense, ga);
    }

    [ClientDataFact]
    public async Task AFirearmOperatorReservesACannonInTheSiegeAndGivesItBack()
    {
        var (attack, defense, ga) = await SiegeWithFirearm("A");
        await using var _ = attack;
        await using var __ = defense;

        // Outside the siege nothing is reserved.
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Prepay(attack, 1006001)).Result);
        await Enter(attack, 1); await Enter(defense, 2);

        // One cannon leaves the warehouse; the answer starts the blueprint and every member sees the stock.
        var held = await Prepay(attack, 1006001);
        Assert.Equal(0, held.Result);
        Assert.Equal((1006001, 1), (held.PrepayItem.ItemId, held.PrepayItem.ItemCount));
        Assert.Equal(49, await Stocked(attack, 1006001, 49));
        GuildStore.State Guild() => Service<GuildStore>().Get(ga)!;
        Assert.Equal(1006001, Guild().WartimePrepay[Session(attack).Role!.Id]);

        // A second reservation replaces the first: the guild never holds two for one member.
        Assert.Equal(0, (await Prepay(attack, 1006002)).Result);
        await Stocked(attack, 1006001, 50);
        Assert.Equal(1006002, Guild().WartimePrepay[Session(attack).Role!.Id]);

        // Only cannons, only with the Firearm permission, only from stock.
        Assert.Equal(ErrorCodes.Parameter, (await Prepay(attack, 1006005)).Result);
        Assert.Equal(ErrorCodes.GuildPermission, (await Prepay(defense, 1006001)).Result);
        await OnLoop(() => { var g = Guild(); g.WartimeItems[1006004] = 0; Assert.True(Service<GuildStore>().Save(g)); return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.GuildSupplies, (await Prepay(attack, 1006004)).Result);
        Assert.Equal(1006002, Guild().WartimePrepay[Session(attack).Role!.Id]); // a refused one keeps the held one

        // Esc on the blueprint gives it back; with nothing held the answer is still 0.
        await attack.SendGameAsync(GuildService.CancelPrepay, new GuildWartimeStoreItemCancelUseReq());
        var back = await Next(attack, GuildService.CancelPrepay, GuildWartimeStoreItemCancelUseResp.Parser);
        Assert.Equal((0, 1006002), (back.Result, back.Item.ItemId));
        await Stocked(attack, 1006002, 50);
        Assert.Empty(Guild().WartimePrepay);
        await attack.SendGameAsync(GuildService.CancelPrepay, new GuildWartimeStoreItemCancelUseReq());
        Assert.Equal(0, (await Next(attack, GuildService.CancelPrepay, GuildWartimeStoreItemCancelUseResp.Parser)).Result);
    }

    private static async Task<GuildAttackCityBuildResp> Build(BotClient bot, float x, float y, float z)
    {
        await bot.SendGameAsync(GuildService.BuildCannon, new GuildAttackCityBuildReq { Pos = new Vec3 { X = x, Y = y, Z = z }, Face = 90 });
        return await Next(bot, GuildService.BuildCannon, GuildAttackCityBuildResp.Parser);
    }

    [ClientDataFact]
    public async Task ABuiltCannonStandsForItsBuilderAfterTheBuildTimeAndReplacesTheLastOne()
    {
        var (attack, defense, ga) = await SiegeWithFirearm("B");
        await using var _ = attack;
        await using var __ = defense;
        await Enter(attack, 1); await Enter(defense, 2);
        var me = Session(attack);
        long builder = me.Role!.Id;
        var at = me.Position.Clone();

        // Without a reserved machine there is nothing to build.
        Assert.Equal(ErrorCodes.Parameter, (await Build(attack, at.X, at.Y, at.Z)).Result);
        Assert.Equal(0, (await Prepay(attack, 1006001)).Result);
        // Far outside the build radius: 530 "Cannot place this here.", and the machine stays reserved.
        Assert.Equal(ErrorCodes.GuildCannonPlace, (await Build(attack, at.X + 40, at.Y, at.Z)).Result);

        var built = await Build(attack, at.X, at.Y, at.Z);
        Assert.Equal((0, 1006001), (built.Result, built.Item.ItemId));
        Assert.Empty(Service<GuildStore>().Get(ga)!.WartimePrepay);
        var working = await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.BuildingCannon);
        Assert.Equal(BuildCannonState.Types.EBuildProcess.Building, working.DataInfo.BuildCannonInfo.Progress);
        Assert.Equal(4900010, working.DataInfo.BuildCannonInfo.GvgCannonConfigId);
        Assert.InRange(working.DataInfo.BuildCannonInfo.CompleteTime - _clock.GetUtcNow().ToUnixTimeMilliseconds(), 2000, 3100);

        // After GVGCannonConfig _iBuildTime the cannon stands: an NPC owned by its builder, in the builder's camp.
        var view = await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 4900013));
        var cannon = view.Bases.Single(b => b.EntityTemplateID == 4900013);
        Assert.Equal((2, builder, 1L), (cannon.EntityType, cannon.Owner, cannon.EntityCamp));
        Assert.Equal(me.Role.Name, cannon.Name); // the nameplate reads "<builder>'s <cannon>" (UIWords 61000)
        Assert.Contains(cannon.BuffData.AddBuffs, b => b.ConfigId == 4909901);
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        // The slot and the builder's own permission name the cannon (roleUsingToolId).
        Assert.Equal((long)cannon.Uuid, (await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.RoleUsingToolId != 0)).RoleUsingToolId);
        await Next(attack, GuildService.ToolChange, GuildWartimeToolBelongInfoChangeNtf.Parser,
            n => n.InfoList.Any(i => i.ToolBelongInfo.RoleGuildId == builder && i.ToolBelongInfo.RoleUsingToolId == (long)cannon.Uuid));

        // A second cannon cannot stand on the first one; elsewhere it replaces it (UIWords 61001).
        Assert.Equal(0, (await Prepay(attack, 1006002)).Result);
        Assert.Equal(ErrorCodes.GuildCannonPlace, (await Build(attack, cannon.MovementInfo.Pos.X + 1, at.Y, cannon.MovementInfo.Pos.Z)).Result);
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(me, new MapPoint(at.X + 6, at.Y, at.Z, 0)));
        var spot = Session(attack).Position.Clone();
        Assert.Equal(0, (await Build(attack, spot.X, spot.Y, spot.Z)).Result);
        var gone = await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(cannon.Uuid));
        Assert.Contains(cannon.Uuid, gone.LeaveAoi);
        Assert.NotEqual((long)cannon.Uuid, (await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.RoleUsingToolId != 0 && n.RoleUsingToolId != (long)cannon.Uuid)).RoleUsingToolId);
    }

    private static async Task<GuildWartimeToolBelongOperRsp> Assign(BotClient bot, WartimeToolType type, int slot, long role)
    {
        await bot.SendGameAsync(GuildService.ToolAssign, new GuildWartimeToolBelongOperReq { ToolType = type, BagIndex = slot, RoleGuildId = role });
        return await Next(bot, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser);
    }

    [ClientDataFact]
    public async Task RevokingTheFirearmPermissionTakesTheCannonAwayAndNoVehiclePageShowsIt()
    {
        var (attack, defense, ga) = await SiegeWithFirearm("R");
        await using var _ = attack;
        await using var __ = defense;
        await Enter(attack, 1); await Enter(defense, 2);
        var me = Session(attack);
        long builder = me.Role!.Id;
        var at = me.Position.Clone();
        Assert.Equal(0, (await Prepay(attack, 1006001)).Result);
        Assert.Equal(0, (await Build(attack, at.X, at.Y, at.Z)).Result);
        var cannon = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 4900013))).Bases.Single(b => b.EntityTemplateID == 4900013);
        await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser, n => n.RoleUsingToolId == (long)cannon.Uuid);
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle); // the build is over
        Assert.Equal(0, await Interact(attack, cannon.Uuid, 4900005));
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Gvgcannon);
        Assert.Equal(0, (await Prepay(attack, 1006002)).Result); // a reserved one too
        Assert.Equal(1006002, Service<GuildStore>().Get(ga)!.WartimePrepay[builder]);

        // The slot is cleared while its holder mans the cannon: they get off, the cannon goes, the reservation comes back
        // and nothing shows the cannon any more (32285 None, 32286 the empty slot).
        Assert.Equal(0, (await Assign(attack, WartimeToolType.Firearm, 1, 0)).Result);
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(cannon.Uuid));
        await Next(attack, GuildService.ToolChange, GuildWartimeToolBelongInfoChangeNtf.Parser,
            n => n.InfoList.Any(i => i is { ToolType: WartimeToolType.Firearm, BagIndex: 1 } && i.ToolBelongInfo.RoleGuildId == 0
                && i.ToolBelongInfo.RoleUsingToolId == 0));
        await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.ToolType == WartimeToolType.None && n.RoleUsingToolId == 0);
        Assert.Empty(Service<GuildStore>().Get(ga)!.WartimePrepay);
        Assert.Equal(50, Service<GuildStore>().Get(ga)!.WartimeItems[1006002]); // the reservation came back
        Assert.Equal(49, Service<GuildStore>().Get(ga)!.WartimeItems[1006001]); // the destroyed cannon did not
        Assert.False(Service<GuildService>().WarMachines!.Standing((long)cannon.Uuid));

        // Given a Vehicle slot afterwards, the member uses nothing there.
        Assert.Equal(0, (await Assign(attack, WartimeToolType.Vehicle, 1, builder)).Result);
        var change = await Next(attack, GuildService.ToolChange, GuildWartimeToolBelongInfoChangeNtf.Parser,
            n => n.InfoList.Any(i => i.ToolType == WartimeToolType.Vehicle && i.ToolBelongInfo.RoleGuildId == builder));
        Assert.All(change.InfoList, i => Assert.Equal(0, i.ToolBelongInfo.RoleUsingToolId));
        var mine = await Next(attack, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser,
            n => n.ToolType == WartimeToolType.Vehicle);
        Assert.Equal(0, mine.RoleUsingToolId);
        await attack.SendGameAsync(GuildService.ToolList, new GetGuildWartimeToolBelongListReq());
        var list = await Next(attack, GuildService.ToolList, GetGuildWartimeToolBelongListRsp.Parser);
        Assert.All(list.ItemPermissionAllData.SelectMany(p => p.InfoList), i => Assert.Equal(0, i.RoleUsingToolId));
    }

    private static async Task<int> Interact(BotClient bot, ulong npc, int interaction)
    {
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = (long)npc, InteractionId = interaction });
        return (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result;
    }

    private static async Task<int> Button(BotClient bot, long button, CannonUseSkill? use = null)
    {
        var req = new UseInteractActionReq { ButtonId = button };
        if (use != null) req.CannonUseSkill = use;
        await bot.SendGameAsync(WorldInteractionService.Action, req);
        return (await Next(bot, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result;
    }

    /// <summary>One bullet almost straight up: it comes down about 6 m in front of the cannon after some 5 s.</summary>
    private static CannonUseSkill Shot(int skill) => new()
    {
        UseSkillParam = new CastSkillReq { SkillId = skill },
        BulletExtraParam = { new BulletExtraParam { Gravity = 1500, Speed = 4000, FaceVector3D = new Vec3 { Y = 1, Z = 0.02f } } },
    };

    [ClientDataFact]
    public async Task ItsBuilderMansTheCannonFiresABulletThatHitsTheEnemyWhereItLandsAndGetsOff()
    {
        var (attack, defense, _) = await SiegeWithFirearm("C");
        await using var _ = attack;
        await using var __ = defense;
        await Enter(attack, 1); await Enter(defense, 2);
        var me = Session(attack);
        long builder = me.Role!.Id;
        var at = me.Position.Clone();
        Assert.Equal(0, (await Prepay(attack, 1006001)).Result);
        Assert.Equal(0, (await Build(attack, at.X, at.Y, at.Z)).Result);
        var cannon = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 4900013))).Bases.Single(b => b.EntityTemplateID == 4900013);
        Assert.Contains(cannon.CustomDatas, d => d.Id == 4900014 && d.CustomParam.LongParam == 12);
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);

        // Only its builder, of its camp, mans it (interaction 4900005, CheckBuilder + CheckCamp).
        var pos = cannon.MovementInfo.Pos;
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(pos.X, pos.Y, pos.Z + 1, 0)));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(defense, cannon.Uuid, 4900005));
        Assert.Equal(0, await Interact(attack, cannon.Uuid, 4900005));
        var on = await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Gvgcannon);
        Assert.Equal(((long)cannon.Uuid, 4900010L), (on.DataInfo.EnterCannonInfo.CannonUId, on.DataInfo.EnterCannonInfo.GvgCannonConfigId));

        // The enemy stands where the shot comes down; one volley costs one of the 12 bullets and starts the cooldown.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(pos.X, pos.Y, pos.Z + 6.3f, 0)));
        long? before = Session(defense).CurrentHp;
        Assert.Equal(0, await Button(attack, 2, Shot(4900701)));
        await Next(attack, WorldCoreService.CustomChange, CustomDataChange.Parser,
            c => c.EntityId == (long)cannon.Uuid && c.CustomDatas.Any(d => d.Id == 4900014 && d.CustomParam.LongParam == 11));
        var bullet = (await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 4900101))).Bases.Single(b => b.EntityTemplateID == 4900101);
        Assert.Equal((10, MovementType.MoveTypeMove), (bullet.EntityType, bullet.MovementInfo.MoveType));
        Assert.InRange(bullet.MovementInfo.MoveDuration, 4500, 6500);
        Assert.Equal(-15, bullet.MovementInfo.MovementMove.AccVelocity.Y, 3);
        Assert.Equal(ErrorCodes.SkillCooldown, await Button(attack, 2, Shot(4900701)));
        await Next(attack, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(bullet.Uuid));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Session(defense).CurrentHp == before && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.NotEqual(before, Session(defense).CurrentHp);

        // The quit button (GameplayStatusButtonConfig 3, action 5004 QuitStatus) gets off.
        Assert.Equal(0, await Button(attack, 3));
        await Next(attack, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == builder && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        Assert.Equal(0, await Interact(attack, cannon.Uuid, 4900005)); // free again for its builder
    }
}
