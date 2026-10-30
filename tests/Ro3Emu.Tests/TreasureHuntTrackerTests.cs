using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class TreasureHuntTrackerTests
{
    [Theory]
    [InlineData(0, 501, 501)]
    [InlineData(-1, 501, 501)]
    [InlineData(9001, 0, 501)]
    public void MissingOrUnknownPointCannotBecomeATaskOrNavigation(long point, long target, long current)
    {
        var plan = TreasureHuntTrackerPlan.Read(point, target, current, 0);
        Assert.False(plan.Show);
        Assert.False(plan.CanResume);
    }

    [Fact]
    public void AnotherMapShowsItsNameButDoesNotOfferLocalNavigation()
    {
        var plan = TreasureHuntTrackerPlan.Read(9001, 501, 502, 0);
        Assert.True(plan.Show);
        Assert.False(plan.CanResume);
        Assert.Contains("Imaginary field", plan.Description("Imaginary field", text => text));
        Assert.Contains("Go to this map", plan.Description("Imaginary field", text => text));
        Assert.True(TreasureHuntTrackerPlan.Read(9001, 501, 501, 0).CanResume);
    }

    [Fact]
    public void ExplicitCompatibleTrackerWinsWithoutSuppressingAnotherPoint()
    {
        Assert.False(TreasureHuntTrackerPlan.Read(9001, 501, 501, 9001).Show);
        Assert.True(TreasureHuntTrackerPlan.Read(9001, 501, 501, 9002).Show);
    }

    [Fact]
    public void ReReadingTheCurrentCursorDisablesAClearedOrChangedTarget()
    {
        Assert.True(TreasureHuntTrackerPlan.Read(9001, 501, 501, 0).CanResume);
        Assert.False(TreasureHuntTrackerPlan.Read(0, 0, 501, 0).CanResume);
        var changed = TreasureHuntTrackerPlan.Read(9002, 502, 501, 0);
        Assert.Equal(9002, changed.PointId);
        Assert.False(changed.CanResume);
    }

    [Fact]
    public void LabelsAreResolvedAgainAfterLanguageChange()
    {
        var plan = TreasureHuntTrackerPlan.Read(9001, 501, 501, 0);
        string language = "one";
        string Label(string text) => language + ":" + text;
        string first = plan.Description("Imaginary field", Label);
        language = "two";
        Assert.NotEqual(first, plan.Description("Imaginary field", Label));
    }

    [Fact]
    public void ClosingDuringDeferredLoadCannotResurrectTheWidget()
    {
        var lease = new TreasureHuntTrackerLease();
        int beforeClose = lease.Begin();
        lease.Cancel();
        Assert.False(lease.Complete(beforeClose));
        Assert.False(lease.Ready);
        int reopened = lease.Begin();
        Assert.False(lease.Complete(beforeClose));
        Assert.True(lease.Pending);
        Assert.True(lease.Complete(reopened));
        Assert.False(lease.Complete(reopened));
        Assert.True(lease.Ready);
    }
}
