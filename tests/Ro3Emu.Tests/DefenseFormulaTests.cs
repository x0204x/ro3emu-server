using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class DefenseFormulaTests
{
    [Theory]
    [InlineData(50, 0, 1.0)]
    [InlineData(50, 1000, 0.5)] // k = 1000
    [InlineData(100, 1000, 2.0 / 3)] // a higher attacker gets through more
    [InlineData(25, 1000, 1.0 / 3)]
    [InlineData(0, 20, 0.5)] // a level of 0 counts as 1
    [InlineData(50, -100, 1.0)] // negative defense does not raise damage
    public void DefenseScalesWithTheAttackersLevel(long level, double defense, double factor) =>
        Assert.Equal(factor, CombatService.DefenseFactor(level, defense), 10);
}
