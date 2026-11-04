using Romsg;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Silence (902301) forbids SkillCast (SpecialStatusConfig 20), so it ends a running cast; Stun (901601, state 18)
    /// forbids neither phase, so the cast goes on (LC_State_Unit_Skill).</summary>
    [ClientDataFact]
    public async Task SilenceEndsARunningCastAndStunDoesNot()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // On one loop step: a slow machine could otherwise let the swing end by itself in between.
        var (afterStun, afterSilence) = await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, 901601, 5000, 7);
            bool running = a.SkillCast != null;
            await buffs.AddAsync(a, 902301, 5000, 7);
            return (running, a.SkillCast != null);
        });
        Assert.True(afterStun);
        Assert.False(afterSilence);
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, n => n.Type == SkillEndNotify.Types.SkillEndType.Interrupted);
        Assert.Equal(SkillInterruptedInfo.Types.SkillInterruptType.State, end.InterruptedInfo.Type);
    }
}
