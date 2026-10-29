using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The war warehouse (32273) and the Tactics panel's war machine permissions (32282/32283/32284, pushes 32285/32286),
/// GuildService.Wartime.
/// </summary>
public sealed partial class GuildTests
{
    private static async Task<GetGuildWartimeToolBelongListRsp> ToolList(BotClient bot)
    {
        await bot.SendGameAsync(GuildService.ToolList, new GetGuildWartimeToolBelongListReq());
        return await Next(bot, GuildService.ToolList, GetGuildWartimeToolBelongListRsp.Parser);
    }

    private static async Task<int> AssignTool(BotClient bot, WartimeToolType type, int index, long role)
    {
        await bot.SendGameAsync(GuildService.ToolAssign,
            new GuildWartimeToolBelongOperReq { ToolType = type, BagIndex = index, RoleGuildId = role, DungeonId = 2230101 });
        var rsp = await Next(bot, GuildService.ToolAssign, GuildWartimeToolBelongOperRsp.Parser);
        Assert.Equal((type, index, role), (rsp.ToolType, rsp.BagIndex, rsp.RoleGuildId));
        return rsp.Result;
    }

    private static long[] Page(GetGuildWartimeToolBelongListRsp list, WartimeToolType type) =>
        [.. list.ItemPermissionAllData.Single(p => p.ToolType == type).InfoList.Select(i => i.RoleGuildId)];

    [ClientDataFact]
    public async Task WarMachinePermissionsAreGuildSlotsTheManagerAssignsSwapsAndRevokes()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await Level("alice");
        await Level("bob");
        long guildId = await CreateGuild(alice, "Astra");
        long aliceId = (long)alice.Self!.Uuid, bobId = (long)bob.Self!.Uuid, carolId = (long)carol.Self!.Uuid;

        // The warehouse a new guild holds: PublicParamConfig SiegeInitItemList, 50 of each cannon.
        var store = await Next(alice, GuildService.WartimeStore, GuildWartimeStoreItemNtf.Parser);
        Assert.Equal([(1006001, 50), (1006002, 50), (1006003, 50), (1006004, 50)],
            store.WartimeItems.Select(i => (i.ItemId, i.ItemCount)));

        // Outside a guild: 6001, as the siege module answered before.
        Assert.Equal(ErrorCodes.TerritoryGuild, (await ToolList(carol)).Result);

        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
        Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        Assert.Equal(4, (await Next(bob, GuildService.WartimeStore, GuildWartimeStoreItemNtf.Parser)).WartimeItems.Count);

        // Both pages with their empty slots, readable by any member.
        var empty = await ToolList(bob);
        Assert.Equal(0, empty.Result);
        Assert.Equal(Enumerable.Repeat(0L, GuildService.FirearmSlots), Page(empty, WartimeToolType.Firearm));
        Assert.Equal(Enumerable.Repeat(0L, GuildService.VehicleSlots), Page(empty, WartimeToolType.Vehicle));

        // GuildPermissionConfig 13 War Machine Management.
        Assert.Equal(ErrorCodes.GuildPermission, await AssignTool(bob, WartimeToolType.Firearm, 1, bobId));

        Assert.Equal(0, await AssignTool(alice, WartimeToolType.Firearm, 3, bobId));
        var placed = await Next(bob, GuildService.ToolChange, GuildWartimeToolBelongInfoChangeNtf.Parser);
        var slot = Assert.Single(placed.InfoList);
        Assert.Equal((WartimeToolType.Firearm, 3, bobId), (slot.ToolType, slot.BagIndex, slot.ToolBelongInfo.RoleGuildId));
        Assert.Equal(WartimeToolType.Firearm,
            (await Next(bob, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser)).ToolType);

        // One slot per member, members only, slots 1..N.
        Assert.Equal(ErrorCodes.Parameter, await AssignTool(alice, WartimeToolType.Vehicle, 1, bobId));
        Assert.Equal(ErrorCodes.GuildMemberMissing, await AssignTool(alice, WartimeToolType.Vehicle, 1, carolId));
        Assert.Equal(ErrorCodes.Parameter, await AssignTool(alice, WartimeToolType.Firearm, GuildService.FirearmSlots + 1, aliceId));
        Assert.Equal(ErrorCodes.Parameter, await AssignTool(alice, WartimeToolType.None, 1, aliceId));

        // Dragging onto another slot swaps (an empty one moves the name); the permission type stays.
        await alice.SendGameAsync(GuildService.ToolExchange,
            new GuildWartimeToolExchangeOperReq { ToolType = WartimeToolType.Firearm, SrcBagIndex = 3, TargetBagIndex = 5 });
        Assert.Equal(0, (await Next(alice, GuildService.ToolExchange, GuildWartimeToolExchangeOperRsp.Parser)).Result);
        var swapped = await Next(bob, GuildService.ToolChange, GuildWartimeToolBelongInfoChangeNtf.Parser, n => n.InfoList.Count == 2);
        Assert.Equal([(3, 0L), (5, bobId)], swapped.InfoList.Select(i => (i.BagIndex, i.ToolBelongInfo.RoleGuildId)));
        var list = await ToolList(alice);
        Assert.Equal(bobId, Page(list, WartimeToolType.Firearm)[4]);
        Assert.Equal(0, Page(list, WartimeToolType.Firearm)[2]);

        // Revoking: the member's own type goes back to None.
        Assert.Equal(0, await AssignTool(alice, WartimeToolType.Firearm, 5, 0));
        Assert.Equal(WartimeToolType.None, (await Next(bob, GuildService.ToolPermission,
            GuildWartimeToolRolePermissionNtf.Parser, n => n.ToolType == WartimeToolType.None)).ToolType);

        // A member who leaves frees the slot and loses the warehouse view.
        Assert.Equal(0, await AssignTool(alice, WartimeToolType.Vehicle, 2, bobId));
        await bob.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(bob, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        await Next(bob, GuildService.WartimeStore, GuildWartimeStoreItemNtf.Parser, n => n.WartimeItems.Count == 0);
        await Next(bob, GuildService.ToolPermission, GuildWartimeToolRolePermissionNtf.Parser, n => n.ToolType == WartimeToolType.None);
        var freed = await Next(alice, GuildService.ToolChange, GuildWartimeToolBelongInfoChangeNtf.Parser,
            n => n.InfoList.Any(i => i.ToolType == WartimeToolType.Vehicle && i.BagIndex == 2 && i.ToolBelongInfo.RoleGuildId == 0));
        Assert.Single(freed.InfoList);
        Assert.Equal(Enumerable.Repeat(0L, GuildService.VehicleSlots), Page(await ToolList(alice), WartimeToolType.Vehicle));
    }
}
