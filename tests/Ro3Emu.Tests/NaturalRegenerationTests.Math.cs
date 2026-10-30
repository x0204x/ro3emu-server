using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class NaturalRegenerationMathTests
{
    [Theory]
    [InlineData(100L, 2L, 500L, 7L)]
    [InlineData(99L, 0L, 101L, 1L)]
    [InlineData(1210L, 300L, 400L, 349L)]
    [InlineData(100L, 0L, 0L, 0L)]
    [InlineData(100L, -10L, 500L, 0L)]
    [InlineData(100L, -2L, 500L, 3L)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(long.MaxValue, 0L, 10000L, long.MaxValue)]
    public void FormulaUsesFinalFlatAndPercentWithoutOverflowOrPhantomMinimum(long maximum, long flat, long percent, long expected)
    {
        var sheet = new StatSheet();
        sheet[AttributeId.MaxHp] = maximum; sheet[AttributeId.MaxSp] = maximum;
        sheet[AttributeId.ReHp] = flat; sheet[AttributeId.ReSp] = flat;
        sheet[AttributeId.PerReHp] = percent; sheet[AttributeId.PerReSp] = percent;
        Assert.Equal((expected, expected), CombatService.Regeneration(sheet));
    }

    [Theory]
    [InlineData(90L, 100L, 7L, 97L)]
    [InlineData(99L, 100L, 7L, 100L)]
    [InlineData(50L, 100L, 0L, 50L)]
    [InlineData(50L, 100L, -1L, 50L)]
    [InlineData(long.MaxValue - 5, long.MaxValue, long.MaxValue, long.MaxValue)]
    public void AdditionSaturatesAtCapWithoutOverflow(long before, long maximum, long amount, long expected) =>
        Assert.Equal(expected, CombatService.RegeneratedValue(before, maximum, amount));
}
