using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Issue #121: a normal attack (_bCancelCast 1) waits for the swing before it at the same target, so a burst of clicks
/// does not hit at click speed; the next swing within CombatService.SwingJitterMs of the end is taken, as network jitter can bring a
/// legitimate one that early.
/// </summary>
public sealed partial class SkillCombatTests
{
    private const int NoviceAttack = 1000001;
    private const int ArcherAttack = 1300001;

    [ClientDataFact]
    public async Task ANormalAttackWaitsForTheSwingBeforeIt()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var session = Session("alice");

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long end = await Loop.InvokeAsync(() => Task.FromResult(
            session.SkillCast!.StartMs + session.SkillCast.ChantMs + session.SkillCast.ActionMs));

        // A second click in the middle of the swing is refused.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Just inside the slack before the swing ends, the next one is taken.
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the swing; nothing left to check");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Turning to another target during that swing is the client's own business (it plays the swing out).
        var other = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { other.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = other.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task AnArcherNormalAttackWaitsForTheSwingBeforeIt()
    {
        await using var archer = await InWorld("Archer");
        await Gm("archer", "!job 3100");
        var target = await Target("archer", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var session = Session("archer");

        await archer.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(archer, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long end = await Loop.InvokeAsync(() => Task.FromResult(
            session.SkillCast!.StartMs + session.SkillCast.ChantMs + session.SkillCast.ActionMs));

        // A second click in the middle of the swing is refused (SkillBusy 3022), even though HunterAttack has _iIsNormalAtk == 0.
        await archer.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(archer, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Just inside the slack before the swing ends, the next one is taken.
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the swing; nothing left to check");
        await archer.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(archer, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }
}
