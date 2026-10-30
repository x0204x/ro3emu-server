using Ro3Emu.Launcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class GameProcessDiscoveryTests
{
    [Fact]
    public void MatchingExecutable_IgnoresPathCaseAndRelativeSegments()
    {
        string expected = Path.Combine(Path.GetTempPath(), "launcher-client", "ro3.exe");
        string actual = Path.Combine(Path.GetTempPath(), "launcher-client", "subdir", "..", "RO3.EXE");
        Assert.True(GameProcessDiscovery.MatchesExecutable(expected, actual));
    }

    [Fact]
    public void SameProcessNameInAnotherInstallation_DoesNotClaimItsSlot()
    {
        string root = Path.GetTempPath();
        Assert.False(GameProcessDiscovery.MatchesExecutable(Path.Combine(root, "one", "ro3.exe"), Path.Combine(root, "two", "ro3.exe")));
        Assert.False(GameProcessDiscovery.MatchesExecutable(Path.Combine(root, "one", "ro3.exe"), null));
    }

    [Fact]
    public void ReopenedTracker_ImportsExistingFourAndRefusesFifthUntilOneExits()
    {
        var tracker = new GameProcessTracker<object>();
        object[] existing = [new(), new(), new(), new()];
        for (int i = 0; i < existing.Length; i++) Assert.True(tracker.TryAdd(i + 1, existing[i]));
        Assert.False(tracker.State.CanStartAnother);
        Assert.False(tracker.State.CanChangeInstallation);
        Assert.False(tracker.TryAdd(5, new object()));
        Assert.True(tracker.TryRemove(2, existing[1]));
        Assert.True(tracker.TryAdd(5, new object()));
        Assert.Equal(4, tracker.State.ActiveCount);
    }
}
