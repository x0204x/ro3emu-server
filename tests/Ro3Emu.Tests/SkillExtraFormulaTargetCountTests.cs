using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class SkillExtraFormulaTargetCountTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1.03)]
    [InlineData(3, 1.09)]
    [InlineData(5, 1.12)]
    [InlineData(99, 1.12)]
    public void AuthoredTargetBonusHonoursTheTargetAndMultiplierCaps(int count, double expected)
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            _iTargetMax = 4, _kDamageFormulaExtra = new[] { "AXadd_HitCount_ExtraDmg" },
            _kDamageParamExtra = new long[][] { [300, 1500] },
        });
        var scoped = new SkillConfig(SkillExtraFormulaRules.WithHitTargetCount(row, count));
        Assert.Equal(expected, SkillExtraFormulaRules.HitTargetMultiplier(scoped), 8);
        Assert.Equal(1, SkillExtraFormulaRules.HitTargetMultiplier(new SkillConfig(row)));
    }

    [Theory]
    [InlineData("BlitzBeat", 2, -10, 1)]
    [InlineData("BlitzBeat", 2, 0, 1)]
    [InlineData("BlitzBeat", 2, 20, 1.01)]
    [InlineData("BlitzBeat", 2, 400, 1.2)]
    [InlineData("PileBunker", 1, 100, 1.05)]
    [InlineData("Unknown", 2, 100, 1)]
    public void OnlyCrossCheckedAttributeFormulaOwnersUseTheAuthoredCoefficient(string script, int attribute, int value, double expected)
    {
        var row = new SkillConfig(JsonSerializer.SerializeToElement(new
        {
            _kScriptLogic = script, _kDamageFormulaExtra = new[] { "T_AXadd0_etc" },
            _kDamageParamExtra = new long[][] { [1, 5, attribute, 1] },
        }));
        Assert.Equal(expected, SkillExtraFormulaRules.AttributeDamageMultiplier(row, new StatSheet { [attribute] = value }), 8);
    }
}
