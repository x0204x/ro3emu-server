using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The card effects' pure rules on invented rows, for the runs without client data. CardRulesTests plays them on the
/// real cards.</summary>
public sealed class CardEffectRuleTests
{
    private static AttributeConversionConfig Conversion(string pairs) => new(JsonDocument.Parse(
        $$"""{"_kAttribute1":{{pairs}},"_kAttribute2":[[999,1]]}""").RootElement);

    [Theory]
    [InlineData(150, 150)]
    [InlineData(-200, -200)] // a penalty stays one, unlike STR..LUK's conversions
    public void AGroupedAttributeCountsOnItsMembersWithItsSign(double value, double member)
    {
        Dictionary<int, double> raw = new() { [77] = value, [801] = 5 };
        CharacterService.AddGroupedConversion(raw, 77, Conversion("[[801,1],[802,1]]"));
        Assert.Equal(member + 5, raw[801]);
        Assert.Equal(member, raw[802]);
        Assert.Equal(value, raw[77]);
    }

    [Fact]
    public void WithoutTheGroupedAttributeItsMembersAreLeftAlone()
    {
        Dictionary<int, double> raw = new() { [801] = 5 };
        CharacterService.AddGroupedConversion(raw, 77, Conversion("[[801,1]]"));
        Assert.Equal(5, raw[801]);
        Assert.Single(raw);
    }

    [Theory]
    [InlineData(20150, 1000)]
    [InlineData(20153, 1000)]
    [InlineData(20223, 1001)]
    [InlineData(20533, 1002)]
    [InlineData(20291, 1003)]
    [InlineData(20272, 1004)]
    [InlineData(20280, 1005)]
    [InlineData(20154, 0)] // past the Orc Lady's four tiers
    [InlineData(20501, 0)] // the size cards
    [InlineData(20190, 0)]
    public void AFamilyCardsGroupNamesItsMonsterTag(long group, long tag) => Assert.Equal(tag, CardEffects.FamilyTag(group));

    [Theory]
    [InlineData(20190, 1, false, true)] // Bash, a Physical attack
    [InlineData(20200, 1, true, true)] // Jupitel Thunder, a Basic Attack is one
    [InlineData(20190, 2, false, false)] // a magic cast
    [InlineData(20200, 2, false, false)]
    [InlineData(20230, 1, false, false)] // Dark Priest's [0, row] is no autocast
    [InlineData(20210, 1, true, true)] // Double Attack, a Basic Attack
    [InlineData(20210, 1, false, false)] // a Physical skill
    public void AnAutocastAnswersItsOwnKindOfCast(long group, long damageType, bool normalAttack, bool triggers) =>
        Assert.Equal(triggers, CardEffects.TriggersAutocast(group, damageType, normalAttack));

    [Theory]
    [InlineData(7000, 30000, 21000)]
    [InlineData(1000, 30000, 3000)]
    [InlineData(1000, 15000, 1500)]
    public void AResetLocksTheSkillForAMultipleOfItsCooldown(long cooldownMs, long multiple, long lockout) =>
        Assert.Equal(lockout, CardEffects.ResetLockoutMs(cooldownMs, multiple));

    [Theory]
    [InlineData(1000, 50, 100000, 3000, 2500, 0, 0, 300)] // 30% of the damage
    [InlineData(1000, 50, 800, 3000, 2500, 0, 0, 200)] // up to 25% of the attacker's Max Life
    [InlineData(1000, 0, 100000, 3200, 3500, 10, 60, 320)] // no VIT
    [InlineData(1000, 100, 100000, 3200, 3500, 10, 60, 420)] // 100 VIT at 0.1% each
    [InlineData(1000, 1000, 100000, 3200, 3500, 10, 60, 600)] // the whole share stops at 60%
    [InlineData(1000, 500, 100000, 5600, 3500, 10, 60, 600)] // and a top star has little room left
    public void AReflectGivesBackItsShareUpToItsCaps(long damage, long vit, long maxHp, long share, long cap, long perVit, double maxShare, long reflected) =>
        Assert.Equal(reflected, CardEffects.ReflectedDamage(damage, vit, maxHp, [1, 3, perVit, 3, 99999, 159, share, 101, cap], maxShare));

    [Theory]
    [InlineData(1000, 20, 400, 200)]
    [InlineData(5000, 20, 400, 400)] // up to its most
    [InlineData(0, 20, 400, 0)]
    public void ASpBurnTakesItsShareUpToItsMost(long sp, long percent, long most, long burned) =>
        Assert.Equal(burned, CardEffects.SpBurned(sp, percent, most));

    private static double HuntingFactor(long mastery, long protection, DamageAttributes.Kind attacker, DamageAttributes.Kind target) => DamageAttributes.Factor(
        attacker == DamageAttributes.Kind.Player ? id => id == 231 ? mastery : 0 : null,
        target == DamageAttributes.Kind.Player ? id => id == 232 ? protection : 0 : null,
        new DamageAttributes.Hit(false, false, false, 1, 0, 0, 0, 0, 0, 0, attacker, target));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(70, 70 / 10070d)]
    [InlineData(10000, 0.5)]
    [InlineData(-5, 0)]
    public void AHuntingAttributeIsAShareOfItself(long value, double share) => Assert.Equal(share, DamageAttributes.PveShare(value), 10);

    [Fact]
    public void HuntingMasteryAndProtectionCountOnlyAgainstMonsters()
    {
        const DamageAttributes.Kind player = DamageAttributes.Kind.Player, monster = DamageAttributes.Kind.Normal, boss = DamageAttributes.Kind.Boss;
        Assert.Equal(1 + 70 / 10070d, HuntingFactor(70, 0, player, monster), 10);
        Assert.Equal(1 + 70 / 10070d, HuntingFactor(70, 0, player, boss), 10);
        Assert.Equal(1, HuntingFactor(70, 70, player, player), 10); // a duel
        Assert.Equal(1 - 70 / 10070d, HuntingFactor(0, 70, monster, player), 10);
    }

    [Theory]
    [InlineData(false, 10, 0)] // the attacker's Bonus ATK
    [InlineData(true, 0, 40)] // and magic reads the magic pair
    public void BonusAttackAndDefenseReadTheirKind(bool magic, long fromAttacker, long fromMagic) =>
        Assert.Equal(fromAttacker + fromMagic - (magic ? 3 : 7), DamageAttributes.FlatBonus(
            id => id switch { 221 => 10, 222 => 40, _ => 0 }, id => id switch { 223 => 7, 224 => 3, _ => 0 }, magic));

    [Fact]
    public void AMonsterSideHasNoBonus() => Assert.Equal(-7, DamageAttributes.FlatBonus(null, id => id == 223 ? 7 : 0, false));

    [Fact]
    public void AReflectRowOfAnotherShapeGivesNothing() => Assert.Equal(0, CardEffects.ReflectedDamage(1000, 0, 1000, [0, 3, 0, 1, 2000, 99, 1000], 60));

    [Fact]
    public void DivineLightHealsTheCasterAndTheFirstOtherOneHealed()
    {
        static DamageInfo Healed(long id) => new() { DstID = id, DamageType = DamageType.Heal };
        Assert.Equal([9], CardEffects.DivineLightTargets(9, [Healed(9)]));
        Assert.Equal([5, 9], CardEffects.DivineLightTargets(9, [Healed(5)]));
        Assert.Equal([5, 9], CardEffects.DivineLightTargets(9, [Healed(9), Healed(5), Healed(6)]));
        Assert.Equal([9], CardEffects.DivineLightTargets(9, [new DamageInfo { DstID = 5 }, Healed(9)])); // a monster it hit
    }
}
