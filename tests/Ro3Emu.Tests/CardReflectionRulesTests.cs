using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class CardReflectionRulesTests
{
    [Fact]
    public void ReflectionCapsAgainstAttackerHealthAndAdvancedRatioIndependently()
    {
        Assert.Equal(250, CardEffects.ReflectionAmount([50, 25, 3], 10000, 1000, 999));
        Assert.Equal(350, CardEffects.ReflectionAmount([56, 35, 1, .1, 60, 3], 10000, 1000, 999));
        Assert.Equal(600, CardEffects.ReflectionAmount([56, 35, 1, .1, 60, 3], 1000, 100000, 999));
        Assert.Equal(320, CardEffects.ReflectionAmount([32, 35, 1, .1, 60, 3], 1000, 100000, 0));
        Assert.Equal(0, CardEffects.ReflectionAmount([32, 35, 0, .1, 60, 3], 1000, 100000, 100));
        Assert.Equal(0, CardEffects.ReflectionAmount([50, 25, 3], 0, 1000, 100));
        Assert.Equal(long.MaxValue / 4, CardEffects.ReflectionAmount([50, 25, 3], long.MaxValue, long.MaxValue, 0));
    }
}
