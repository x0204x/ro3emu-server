using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// AttributeConfig 118 (SP Recovery on the character panel) reads "Recovers ${1} MP every @{1} sec." with no combat
    /// condition; only 121 says "while out of combat". A caster at 1 SP in a fight gets its recovery.
    /// </summary>
    [ClientDataFact]
    public async Task SpRecoveryRestoresSpEveryFiveSecondsInCombat()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100");
        await Gm("alice", "!lvl 40");
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            s.StatusBits |= 1L << 15; // fighting, and staying so for the whole test
            s.LastCombatMs = MonsterService.NowMs + 60000;
            await Character.SetHpSpAsync(s, null, 1);
        });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(7);
        long Sp() => Character.Stats(Session("alice"))[2001];
        while (DateTime.UtcNow < deadline && await Loop.InvokeAsync(() => Task.FromResult(Sp())) == 1)
            await Task.Delay(50);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Sp())) > 1);
        Assert.NotEqual(0, await Loop.InvokeAsync(() => Task.FromResult(Session("alice").StatusBits & (1L << 15))));
    }
}
