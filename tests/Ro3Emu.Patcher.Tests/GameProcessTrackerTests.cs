using Ro3Emu.Launcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class GameProcessTrackerTests
{
    [Fact]
    public void EmptyTracker_AllowsLaunchAndInstallationChanges()
    {
        var tracker = new GameProcessTracker<object>();

        Assert.Equal(0, tracker.State.ActiveCount);
        Assert.True(tracker.State.CanStartAnother);
        Assert.True(tracker.State.CanChangeInstallation);
    }

    [Fact]
    public void FirstGame_AllowsSecondButBlocksInstallationChanges()
    {
        var tracker = new GameProcessTracker<object>();

        Assert.True(tracker.TryAdd(101, new object()));

        Assert.Equal(1, tracker.State.ActiveCount);
        Assert.True(tracker.State.CanStartAnother);
        Assert.False(tracker.State.CanChangeInstallation);
    }

    [Fact]
    public void SecondGame_FillsCapacityAndThirdIsRefused()
    {
        var tracker = new GameProcessTracker<object>();

        Assert.True(tracker.TryAdd(101, new object()));
        Assert.True(tracker.TryAdd(202, new object()));

        Assert.Equal(GameProcessState.Capacity, tracker.State.ActiveCount);
        Assert.False(tracker.State.CanStartAnother);
        Assert.False(tracker.TryAdd(303, new object()));
    }

    [Fact]
    public void DuplicatePid_IsRefusedWithoutReplacingTheTrackedInstance()
    {
        var tracker = new GameProcessTracker<object>();
        var first = new object();
        Assert.True(tracker.TryAdd(101, first));

        Assert.False(tracker.TryAdd(101, new object()));
        Assert.Equal(1, tracker.State.ActiveCount);
        Assert.True(tracker.TryRemove(101, first));
    }

    [Fact]
    public void RemovingOneGame_DoesNotAffectTheOther()
    {
        var tracker = new GameProcessTracker<object>();
        var first = new object();
        var second = new object();
        Assert.True(tracker.TryAdd(101, first));
        Assert.True(tracker.TryAdd(202, second));

        Assert.True(tracker.TryRemove(101, first));

        Assert.Equal(1, tracker.State.ActiveCount);
        Assert.True(tracker.State.AnyRunning);
        Assert.True(tracker.State.CanStartAnother);
        Assert.False(tracker.State.CanChangeInstallation);
        Assert.False(tracker.TryRemove(101, first));
        Assert.True(tracker.TryRemove(202, second));
    }

    [Fact]
    public void LateObserver_CannotRemoveReusedPid()
    {
        var tracker = new GameProcessTracker<object>();
        var oldProcess = new object();
        var newProcess = new object();
        Assert.True(tracker.TryAdd(101, oldProcess));
        Assert.True(tracker.TryRemove(101, oldProcess));
        Assert.True(tracker.TryAdd(101, newProcess));

        Assert.False(tracker.TryRemove(101, oldProcess));
        Assert.Equal(1, tracker.State.ActiveCount);
        Assert.True(tracker.TryRemove(101, newProcess));
    }
}
