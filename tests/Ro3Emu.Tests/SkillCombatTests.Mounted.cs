using System.Diagnostics;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int MountedBash = 1100101;

    /// <summary>A Swordsman at level 14 owning and riding the 60030010 travel mount, with Bash learned.</summary>
    private async Task<BotClient> MountedSwordsman()
    {
        var alice = await InWorld("Alice");
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
        return alice;
    }

    /// <summary>
    /// The client never asks to get off a travel mount before it attacks (only interaction skills send OffSystem,
    /// UI_Sys_MainRoleSkillWidgetCtrl.lua:481-488), so the hit itself must land and the server ends the ride.
    /// </summary>
    [ClientDataFact]
    public async Task ASkillCastFromATravelMountHitsAndEndsTheRide()
    {
        await using var alice = await MountedSwordsman();
        var player = Session("alice");
        var target = await Target("alice", stationary: true);
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MountedBash, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((target.Hp == before || MountService.Rider(player) != 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(target.Hp < before, "the mounted Bash dealt no damage");
        Assert.Equal(0, MountService.Rider(player));
    }

    /// <summary>
    /// A mounted attack first gets the rider off (31049 CurRider 0, which the client plays as the dismount), and only
    /// then the cast starts (31100) and hits (31101): the client holds a skill while its unit rides a system mount
    /// (LC_State_Unit_Skill.InitSkillState). The cast is not refused, and the dismount lead is MountService.DismountMs.
    /// </summary>
    [ClientDataFact]
    public async Task AMountedAttackGetsOffTheMountFirstAndThenHits()
    {
        await using var alice = await MountedSwordsman();
        var player = Session("alice");
        var target = await Target("alice", stationary: true);
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MountedBash, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await Next(alice, 31049, EntityBase.Parser, e => e.NotifyType == EntityBase.Types.NotifyType.Rider && e.CurRider == 0);
        var sinceDismount = Stopwatch.StartNew();
        await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill), Wait);
        Assert.True(sinceDismount.ElapsedMilliseconds >= MountService.DismountMs * 0.8,
            $"the cast started {sinceDismount.ElapsedMilliseconds} ms after the dismount");
        await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit), Wait);

        var events = alice.Inbox.Snapshot();
        int off = events.ToList().FindIndex(e => e.IsGame(31049) && EntityBase.Parser.ParseFrom(e.Body) is
        { NotifyType: EntityBase.Types.NotifyType.Rider, CurRider: 0 });
        int start = events.ToList().FindIndex(e => e.IsGame(CombatService.UseInSkill));
        int hit = events.ToList().FindIndex(e => e.IsGame(CombatService.SkillHit));
        Assert.True(off >= 0 && off < start && start < hit, $"order: dismount {off}, cast {start}, hit {hit}");
        Assert.Equal(0, MountService.Rider(player));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (target.Hp == before && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(target.Hp < before, "the mounted Bash dealt no damage");
    }

    /// <summary>An attack from the ground starts at once: nobody gets off, so there is no dismount to wait for.</summary>
    [ClientDataFact]
    public async Task AnAttackFromTheGroundStartsWithoutADismountLead()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 14");
        await Gm("alice", "!job 1100"); await Teach("alice", 11001);
        var target = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MountedBash, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var sinceReply = Stopwatch.StartNew();
        await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill), Wait);
        Assert.True(sinceReply.ElapsedMilliseconds < MountService.DismountMs, "an unmounted cast waited like a dismount");
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(31049)
            && EntityBase.Parser.ParseFrom(e.Body) is { NotifyType: EntityBase.Types.NotifyType.Rider });
    }
}
