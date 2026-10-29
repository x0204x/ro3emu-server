using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// The client never asks to get off a travel mount before it attacks (only interaction skills send OffSystem,
    /// UI_Sys_MainRoleSkillWidgetCtrl.lua:481-488), so the hit itself must land and the server ends the ride.
    /// </summary>
    [ClientDataFact]
    public async Task ASkillCastFromATravelMountHitsAndEndsTheRide()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 14");
        await Gm("alice", "!job 1100"); await Teach("alice", 11001);
        await Gm("alice", "!item 1501013 1");
        var player = Session("alice");
        long guid = player.Items.Single(i => i.Entry == 1501013).Guid;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        await alice.SendGameAsync(MountService.SetConfig, new PlayerMountsSetCfgReq { MountId = 60030010 });
        await Next(alice, MountService.SetConfig, PlayerMountsSetCfgResp.Parser);
        await alice.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnSystem });
        Assert.Equal(0, (await Next(alice, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.NotEqual(0, MountService.Rider(player));

        var target = await Target("alice", stationary: true);
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((target.Hp == before || MountService.Rider(player) != 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(target.Hp < before, "the mounted Bash dealt no damage");
        Assert.Equal(0, MountService.Rider(player));
    }
}
