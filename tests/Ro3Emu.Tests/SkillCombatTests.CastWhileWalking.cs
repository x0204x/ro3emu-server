using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>In-game 3.11: a skill pressed in mid-walk gets one more step report at once and further ones while it plays.
    /// None of them ends it, and its hit lands.</summary>
    [ClientDataFact]
    public async Task ASkillPressedInMidWalkPlaysOutWhileTheWalkGoesOn()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        var start = a.Position.Clone();
        Vec3 Step(float metres) => new() { X = start.X, Y = start.Y, Z = start.Z + metres };

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = Step(0.1f), Face = 0, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = Step(0.4f), Face = 0, SpeedRate = 1 });

        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.SrcID == a.Role!.Id);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillEnd)
            && SkillEndNotify.Parser.ParseFrom(e.Body).InterruptedInfo?.Type == SkillInterruptedInfo.Types.SkillInterruptType.Move);
    }
}
