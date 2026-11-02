using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>When a running cast gives way to the next (CombatService.CastYields, SkillShow.InterruptOffset). The ids are invented.</summary>
public sealed class CastInterruptTests
{
    private static JsonElement Show(double interrupt, params double[] times) =>
        JsonSerializer.SerializeToElement(new { m_fInterruptTime = interrupt, m_kState = new { Time = times } });

    private static JsonElement ShowWithoutInterrupt(params double[] times) =>
        JsonSerializer.SerializeToElement(new { m_kState = new { Time = times } });

    [Fact]
    public void TypeOneYieldsToAnotherSkillAtOnceButNotToItself()
    {
        Assert.True(CombatService.CastYields(1, sameSkill: false, elapsedMs: 0, interruptOffsetMs: null));
        Assert.False(CombatService.CastYields(1, sameSkill: true, elapsedMs: 5000, interruptOffsetMs: 100));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(349, false)] // before the slack
    [InlineData(350, true)] // the slack before 500
    [InlineData(600, true)]
    public void TypeTwoYieldsFromItsInterruptTime(long elapsed, bool yields)
    {
        Assert.Equal(yields, CombatService.CastYields(2, sameSkill: false, elapsed, 500));
        Assert.Equal(yields, CombatService.CastYields(2, sameSkill: true, elapsed, 500));
    }

    [Fact]
    public void TypeTwoWithoutAnInterruptTimeHoldsToTheEnd() =>
        Assert.False(CombatService.CastYields(2, sameSkill: false, elapsedMs: 60_000, interruptOffsetMs: null));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    public void OtherTypesHoldToTheEnd(long type) =>
        Assert.False(CombatService.CastYields(type, sameSkill: false, elapsedMs: 60_000, interruptOffsetMs: 0));

    [Fact]
    public void TheInterruptTimeFollowsTheScaledAction()
    {
        // The show's action runs 0.5..1.5 s, the cast's takes 2000 ms after a 400 ms chant: halfway is 400 + 1000.
        Assert.Equal(1400, SkillShow.InterruptOffset(Show(1, 0, 0.5, 1.5), 400, 2000));
        Assert.Equal(500, SkillShow.InterruptOffset(Show(0.5, 0, 0, 1), 0, 1000));
    }

    [Fact]
    public void AnInterruptTimeInsideTheChantCountsAsItsEnd() =>
        Assert.Equal(400, SkillShow.InterruptOffset(Show(0.2, 0, 0.5, 1.5), 400, 2000));

    [Fact]
    public void AShowWithoutAnInterruptTimeHasNoOffset()
    {
        Assert.Null(SkillShow.InterruptOffset(null, 0, 1000));
        Assert.Null(SkillShow.InterruptOffset(Show(0, 0, 0, 1), 0, 1000));
        Assert.Null(SkillShow.InterruptOffset(ShowWithoutInterrupt(0, 0, 1), 0, 1000));
    }
}
