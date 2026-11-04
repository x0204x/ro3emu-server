using Romsg;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>A step or a 31124 ends a swing, but a new one waits for the show's m_fInterruptTime_move: cancelling right
    /// after the hit must not beat the attack speed.</summary>
    [ClientDataFact]
    public async Task ACancelledSwingHoldsTheNextOneUntilAMoveMayEndIt()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var swing = new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 };
        await alice.SendGameAsync(CombatService.UseSkill, swing);
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // On one loop step, so a slow machine cannot let the lock run out between the two.
        long until = await Loop.InvokeAsync(async () =>
        {
            await combat.InterruptAsync(a);
            await combat.CastAsync(a, swing);
            return a.SwingLockUntilMs;
        });
        Assert.True(until > 0);
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < until) await Task.Delay(20);
        await alice.SendGameAsync(CombatService.UseSkill, swing);
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>A swing that killed its target holds nothing: walking on to the next monster must not be refused.</summary>
    [ClientDataFact]
    public async Task WalkingOnAfterAKillIsNotHeld()
    {
        await using var alice = await InWorld("Alice");
        var first = await Target("alice", stationary: true);
        var next = await Target("alice", stationary: true);
        var a = Session("alice");
        await Loop.InvokeAsync(() => { first.Hp = 1; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = first.Uid, ComboIndex = 1 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == first.Uid && d.IsDead));
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            await combat.InterruptAsync(a);
            await combat.CastAsync(a, new CastSkillReq { SkillId = 1000001, TargetId = next.Uid, ComboIndex = 1 });
        });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }
}
