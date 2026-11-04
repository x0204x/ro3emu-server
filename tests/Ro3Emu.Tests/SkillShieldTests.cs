using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The shield a player skill's buff carries, on invented BuffConfig rows.</summary>
public sealed class SkillShieldTests
{
    private static readonly long[] ShieldTag = [30];
    private static readonly long[] ShieldAndSupportTags = [30, 79];
    private static readonly long[] OtherTag = [23];
    private static readonly int[] MagicRatio = [2500];
    private static readonly string[] AttributeFormula = ["T_AXadd0_etc"];
    private static readonly string[] NoFormula = [""];

    private static BuffConfig MaxHpShield(long[] categories, int source = 0, long group = 70001) =>
        new(JsonSerializer.SerializeToElement(new
        {
            _iID = group * 100 + 1, _iBuffID = group, _iType = 1, _iDmgType = 0, _kBuffCategory = categories,
            _kDamageFormulaExtra = AttributeFormula, _kDamageParamExtra = new[] { new[] { source, 1500, 101, 3 } },
        }));

    private static BuffConfig MagicShield(long[] categories, long group = 70002) =>
        new(JsonSerializer.SerializeToElement(new
        {
            _iID = group * 100 + 1, _iBuffID = group, _iType = 1, _iDmgType = 2, _kBuffCategory = categories,
            _kDamageFormulaExtra = NoFormula, _kData1 = MagicRatio,
        }));

    private static readonly StatSheet Caster = new() { [101] = 4000, [104] = 1234 };
    private static readonly StatSheet Recipient = new() { [101] = 9000, [104] = 77 };

    [Fact]
    public void ShieldTaggedMaxHpRowsCarryTheirAttributeShare()
    {
        Assert.Equal(600, SkillShieldRules.Amount(MaxHpShield(ShieldTag), Caster, Recipient));
        Assert.Equal(1350, SkillShieldRules.Amount(MaxHpShield(ShieldTag, source: 2), Caster, Recipient));
    }

    [Fact]
    public void ShieldTaggedMagicRowsCarryTheCastersMagicAtkRatio()
    {
        // floor(1234 * 2500 / 10000) = floor(308.5)
        Assert.Equal(308, SkillShieldRules.Amount(MagicShield(ShieldAndSupportTags), Caster, Recipient));
    }

    [Fact]
    public void RowsWithoutTheShieldTagCarryNoShield()
    {
        Assert.Equal(0, SkillShieldRules.Amount(MaxHpShield(OtherTag), Caster, Recipient));
        Assert.Equal(0, SkillShieldRules.Amount(MagicShield([]), Caster, Recipient));
        Assert.Equal(600, SkillShieldRules.Amount(
            MaxHpShield(OtherTag, group: SkillShieldRules.HighEnergyShieldGroup), Caster, Recipient));
    }

    [Theory]
    [InlineData(0, 100, 1, 100)]
    [InlineData(80, 100, 1, 100)]
    [InlineData(40, 100, 2, 140)]
    [InlineData(250, 100, 3, 300)]
    [InlineData(50, 0, 2, 0)]
    public void EachPileAddsOneShieldUpToThePileCount(long held, long perPile, int pile, long expected) =>
        Assert.Equal(expected, SkillShieldRules.Stacked(held, perPile, pile));

    [Fact]
    public void OnlyKyrieIgnoresMagicHits()
    {
        Assert.True(SkillShieldRules.AbsorbsPhysicalOnly(MagicShield(ShieldAndSupportTags, SkillShieldRules.KyrieEleisonGroup)));
        Assert.False(SkillShieldRules.AbsorbsPhysicalOnly(MagicShield(ShieldAndSupportTags)));
        Assert.True(SkillShieldRules.IsMagic(JsonSerializer.SerializeToElement(new { _iID = 1, _iDmgType = 2 })));
        Assert.False(SkillShieldRules.IsMagic(JsonSerializer.SerializeToElement(new { _iID = 1, _iDmgType = 1 })));
        Assert.False(SkillShieldRules.IsMagic(null));
    }
}
