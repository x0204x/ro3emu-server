using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class GuardianModifierTests
{
    private static readonly int[] BaseDamageCoefficient = [17600];
    private static readonly int[] TaggedBuff = [99011, 10000, 3456, 3001];
    private static readonly int[] TaggedShield = [99012, 10000, 4567, 3004];
    private static readonly int[] TaggedImmunity = [99013, 10000, 5678, 3006];
    private static readonly int[] TaggedDebuff = [99014, 10000, 6789, 3002];
    private static readonly int[] TaggedControl = [99015, 10000, 7890, 3005];
    private static readonly string[] BuffFields = ["_kBuff1", "_kBuff2"];

    [Fact]
    public void GuardianHealingDebuffAndSpSurchargeAffectTheirActualResources()
    {
        var source = new StatSheet { [207] = 1700, [1207] = 300, [1231] = 7, [1232] = 2300 };
        var target = new StatSheet { [208] = -2000, [1208] = -500 };
        Assert.Equal(900, CombatService.ModifiedHealing(source, target, 1000));
        Assert.Equal(132, CombatService.ModifiedSpCost(source, 100));
        Assert.Equal(0, CombatService.ModifiedSpCost(source, 0));
    }

    private readonly GameData _data = new(Options.Create(new ServerOptions { GameDataDir = SyntheticData.Dir }), NullLogger<GameData>.Instance);

    private static GuardianPetRules.Skill Skill(string script, int[] categories, double[] description, int slot = 2) => new(
        slot, 97 + slot, 1, 0, 0,
        JsonSerializer.SerializeToElement(new { _iID = 99001, _iSkillID = 990, _iCD = 12345 }),
        JsonSerializer.SerializeToElement(new
        {
            _iID = 99002, _iSkillID = 991, _iSystemType = 41, _kScriptLogic = script, _kSkillCategory = categories,
            _iTargetMax = 3, _iRangeType = 2, _kDamageParam1 = BaseDamageCoefficient,
            _kBuff1 = new[] { TaggedBuff, TaggedShield, TaggedImmunity },
            _kBuff2 = new[] { TaggedDebuff, TaggedControl },
        }),
        JsonSerializer.SerializeToElement(new { _iID = 99003, _kDescData = description }), GuardianPetRules.Kind.Damage);

    [Fact]
    public void GuardianDamageAndHealingUseSeparateAdditiveBonusesWithoutModifyingSharedConfig()
    {
        var primary = Skill("Pet_Salamander_23462", [3010, 3013], [99], 1);
        var prepared = GuardianPetRules.Prepare(_data, primary,
            [primary, Skill("Pet_Salamander_23462", [], [7]), Skill("xuesefengbao", [], [13]), Skill("Alice_Huzhu", [], [9])]);
        Assert.Equal(120, GuardianPetRules.DirectDamage(prepared.Effect, 100));
        Assert.Equal(122, GuardianPetRules.DirectHealing(prepared.Effect, 100));
        Assert.Equal(100, GuardianPetRules.DirectDamage(primary.Effect, 100));
        Assert.Equal(0, GuardianPetRules.DirectDamage(prepared.Effect, 0));
    }

    [Theory]
    [InlineData("PetSkill_2_2_QiaoKeHou", 3011, "_kBuff1", 0)]
    [InlineData("yemanhudun", 3014, "_kBuff1", 1)]
    [InlineData("PetSkill_2_2_GuZhongMo", 3016, "_kBuff1", 2)]
    [InlineData("KuangFengXiJuan", 3012, "_kBuff2", 0)]
    [InlineData("XieXinLianRen", 3012, "_kBuff2", 0)]
    [InlineData("GarmBaby_Huzhu", 3015, "_kBuff2", 1)]
    public void GuardianDurationRequiresBothCategoryAndTaggedEntry(string script, int category, string field, int index)
    {
        var primary = Skill("Pet_Salamander_23462", [category], [], 1);
        var utility = Skill(script, [], [1.375]);
        var prepared = GuardianPetRules.Prepare(_data, primary, [primary, utility]);
        foreach (string name in BuffFields)
            for (int i = 0; i < primary.Effect.GetProperty(name).GetArrayLength(); i++)
                Assert.Equal(primary.Effect.GetProperty(name)[i][2].GetInt32() + (name == field && i == index ? 1375 : 0),
                    prepared.Effect.GetProperty(name)[i][2].GetInt32());
        var other = Skill("Pet_Salamander_23462", [999], [], 1);
        var unchanged = GuardianPetRules.Prepare(_data, other, [utility]);
        Assert.Equal(other.Effect.GetProperty(field).GetRawText(), unchanged.Effect.GetProperty(field).GetRawText());
    }

    [Fact]
    public void GuardianCoefficientBonusIsNotAFinalDamageMultiplier()
    {
        var primary = Skill("Pet_Salamander_23462", [3010], [], 1);
        var prepared = GuardianPetRules.Prepare(_data, primary, [Skill("PetSkill_2_2_ShouRenZhanShiZhang", [], [23])]);
        Assert.Equal(19900, GuardianPetRules.Numbers(prepared.Effect, "_kDamageParam1")[0]);
        Assert.Equal(100, GuardianPetRules.DirectDamage(prepared.Effect, 100));
    }

    [Fact]
    public void GuardianMarionetteNeverTurnsTheUnusedDamagePlaceholderIntoDamage()
    {
        var skill = Skill("Pet_Marionette_23392", [3012], [], 1) with { Kind = GuardianPetRules.Kind.Debuff };
        var row = GuardianPetRules.DamageRow(skill, new StatSheet());
        Assert.Empty(GuardianPetRules.Numbers(row, "_kDamageParam1"));
        Assert.Empty(GuardianPetRules.Numbers(row, "_kDamageParam2"));
        Assert.Equal(2, row.GetProperty("_kBuff2").GetArrayLength());
    }
}
