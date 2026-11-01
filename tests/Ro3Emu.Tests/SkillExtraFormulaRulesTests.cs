using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class SkillExtraFormulaRulesTests
{
    [Theory]
    [InlineData(-4, 0)]
    [InlineData(0, 0)]
    [InlineData(20, 260)]
    [InlineData(70, 910)]
    [InlineData(100, 910)]
    public void ResonanceCapsAttributePointsAndReturnsAnAdditiveRatio(long attribute, double expected)
    {
        var skill = Row("T_AXadd0_etc_withlimit", [1, 13, 2, 1, 70]);
        var stats = new StatSheet { [2] = attribute };
        Assert.Equal(expected, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats));
        Assert.Equal(1, SkillExtraFormulaRules.AttackSpeedMultiplier(skill, stats));
    }

    [Fact]
    public void ShortHealingResonanceUsesTheSameCapAndDoesNotBecomeDamageMultiplier()
    {
        var skill = Row("T_AXadd0_etc_withlimit", [1, 7, 5, 40]);
        var stats = new StatSheet { [5] = 60 };
        Assert.Equal(280, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats));
    }

    [Theory]
    [InlineData(-20, 1)]
    [InlineData(0, 1)]
    [InlineData(100, 1.7)]
    [InlineData(240, 2.68)]
    [InlineData(500, 2.68)]
    public void AttackSpeedMultiplierUsesPercentageUnitsAndCapsOnlyTheAuthoredInput(long speed, double expected)
    {
        var skill = Row("AXaddBplus1_withlimit", [70, 115, 10000, 240]);
        var stats = new StatSheet { [115] = speed };
        Assert.Equal(expected, SkillExtraFormulaRules.AttackSpeedMultiplier(skill, stats), 8);
        Assert.Equal(0, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats));
    }

    [Fact]
    public void UnknownFormulaAndNonAttackSpeedAttributesNeverGainGuessedDamage()
    {
        var stats = new StatSheet { [1] = 100, [101] = 9000, [115] = 200 };
        foreach (var skill in new[] { Row("unknown", [1, 30, 1, 1, 100]), Row("T_AXadd0_etc", [1, 5000, 101, 3]),
            Row("AXaddBplus1", [30, 101, 10000]), Row("T_AXadd0_etc_withlimit", [2, 30, 1, 1, 100]) })
        {
            Assert.Equal(0, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats));
            Assert.Equal(1, SkillExtraFormulaRules.AttackSpeedMultiplier(skill, stats));
        }
    }

    [Fact]
    public void BranchesUseTheirOwnFormulaAndDescribedHitsCannotApplyTheParentExtraTwice()
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            _kDamageFormulaExtra = new[] { "AXaddBplus1", "T_AXadd0_etc_withlimit" },
            _kDamageParamExtra = new long[][] { [40, 115, 10000], [1, 11, 3, 1, 50] },
        });
        var skill = new SkillConfig(row);
        var stats = new StatSheet { [3] = 60, [115] = 100 };
        Assert.Equal(1.4, SkillExtraFormulaRules.AttackSpeedMultiplier(skill, stats));
        Assert.Equal(0, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats));
        Assert.Equal(550, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats, 1));
        Assert.Equal(0, SkillExtraFormulaRules.ResonanceRatioBonus(skill, stats, 2));
        var described = new SkillConfig(skill.AsDescribedHit(9000, 200, 1));
        Assert.Equal(0, SkillExtraFormulaRules.ResonanceRatioBonus(described, stats, 1));
        Assert.Equal(1, SkillExtraFormulaRules.AttackSpeedMultiplier(described, stats));
    }

    private static SkillConfig Row(string formula, long[] tuple) => new(JsonSerializer.SerializeToElement(new
    { _kDamageFormulaExtra = new[] { formula }, _kDamageParamExtra = new[] { tuple } }));
}
