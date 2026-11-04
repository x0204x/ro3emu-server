using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class DamageAttributesTests
{
    private static Func<int, long> Sheet(params (int Id, long Value)[] values) => id => values.Where(v => v.Id == id).Sum(v => v.Value);
    private static readonly DamageAttributes.Hit PhysicalMelee = new(false, false, true, 1, 2, 2, 1, 3, 1, 4);

    [Fact]
    public void WithoutAttributesAHitStaysAsItWas() => Assert.Equal(1, DamageAttributes.Factor(null, null, PhysicalMelee));

    [Fact]
    public void AttackersAndDefendersOfOneGroupAddUp() => Assert.Equal(1.15, DamageAttributes.Factor(
        Sheet((215, 1000), (211, 1000)), Sheet((216, 500)), PhysicalMelee), 6);

    [Fact]
    public void TheBuffGroupMultipliesTheGearGroup() => Assert.Equal(1.1 * 1.2, DamageAttributes.Factor(
        Sheet((215, 1000), (1215, 2000)), null, PhysicalMelee), 6);

    [Theory]
    [InlineData(211, 212, false, false)] // physical
    [InlineData(213, 214, true, false)] // magic
    [InlineData(227, 228, false, false)] // melee physical
    [InlineData(229, 230, false, true)] // ranged physical
    [InlineData(250, 251, true, false)] // melee magic
    [InlineData(252, 253, true, true)] // ranged magic
    public void AHitReadsTheKindItIs(int dealt, int taken, bool magic, bool ranged)
    {
        var hit = PhysicalMelee with { MagicHit = magic, Ranged = ranged };
        Assert.Equal(1.2, DamageAttributes.Factor(Sheet((dealt, 2000)), null, hit), 6);
        Assert.Equal(0.8, DamageAttributes.Factor(null, Sheet((taken, 2000)), hit), 6);
    }

    [Fact]
    public void AnotherKindsAttributeDoesNothing() => Assert.Equal(1, DamageAttributes.Factor(Sheet((213, 2000), (229, 2000)), null, PhysicalMelee), 6);

    [Fact]
    public void ANormalAttackLeavesTheSkillGroupOut()
    {
        Assert.Equal(1.5, DamageAttributes.Factor(Sheet((225, 5000)), null, PhysicalMelee), 6);
        Assert.Equal(1, DamageAttributes.Factor(Sheet((225, 5000)), null, PhysicalMelee with { SkillHit = false }), 6);
    }

    [Fact]
    public void RaceElementAndSizeGoByTheirOwnNumbers()
    {
        // Target race 3, attacker race 2, dealt element 1, target element 4, attacker element 1, target size 1, attacker size 2.
        Assert.Equal(1.1, DamageAttributes.Factor(Sheet((403, 1000)), null, PhysicalMelee), 6);
        Assert.Equal(0.9, DamageAttributes.Factor(null, Sheet((412, 1000)), PhysicalMelee), 6);
        Assert.Equal(1.1, DamageAttributes.Factor(Sheet((301, 1000)), null, PhysicalMelee), 6);
        Assert.Equal(0.9, DamageAttributes.Factor(null, Sheet((311, 1000)), PhysicalMelee), 6);
        Assert.Equal(1.1, DamageAttributes.Factor(Sheet((324, 1000)), null, PhysicalMelee), 6);
        Assert.Equal(0.9, DamageAttributes.Factor(null, Sheet((331, 1000)), PhysicalMelee), 6);
        Assert.Equal(1.1, DamageAttributes.Factor(Sheet((501, 1000)), null, PhysicalMelee), 6);
        Assert.Equal(0.9, DamageAttributes.Factor(null, Sheet((504, 1000)), PhysicalMelee), 6);
    }

    [Fact]
    public void APlayerTargetHasNoMonsterElement() => Assert.Equal(1, DamageAttributes.Factor(
        Sheet((321, 5000)), null, PhysicalMelee with { TargetElement = 0 }), 6);

    [Fact]
    public void TheFactorStaysWithinTheClientsLimits()
    {
        Assert.Equal(10, DamageAttributes.Factor(Sheet((215, 1_000_000)), null, PhysicalMelee));
        Assert.Equal(0.1, DamageAttributes.Factor(null, Sheet((216, 100_000)), PhysicalMelee));
    }

    [Theory]
    [InlineData(1000, 100, 0, 900)] // flat first
    [InlineData(1000, 0, 2500, 750)]
    [InlineData(1000, 200, 5000, 400)]
    [InlineData(100, 500, 0, 0)] // not below zero
    public void PenetrationTakesFlatThenShare(double defense, long flat, long percent, double left) =>
        Assert.Equal(left, DamageAttributes.DefenseAfterPenetration(defense, flat, percent), 6);

    [Theory]
    [InlineData(0, 0, 1.5)]
    [InlineData(5000, 0, 2.0)]
    [InlineData(0, 2000, 1.3)]
    [InlineData(0, 20000, 1)] // a crit never does less than a normal hit
    public void CritDamageStartsFromTheClientsParameter(long dealt, long taken, double multiplier) =>
        Assert.Equal(multiplier, DamageAttributes.CritMultiplier(dealt, taken), 6);
}
