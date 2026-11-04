using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>The plan the automatic attack counts from. The pace itself was measured in the client: a wall-clock test of it
    /// failed under a loaded run while passing alone.</summary>
    [Theory]
    [InlineData(1000, 1000, 333, 1333)] // served on time
    [InlineData(1000, 1017, 333, 1333)] // the next tick served it: the plan stands, so the tick does not add up
    [InlineData(1000, 3000, 333, 3283)] // held up by another skill: no catching up
    public void TheNextSwingCountsFromThePlan(long planned, long now, int skillMs, long next) =>
        Assert.Equal(next, CombatService.NextSwing(planned, now, skillMs));
}
