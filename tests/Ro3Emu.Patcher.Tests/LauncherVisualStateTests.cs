using Ro3Emu.Launcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class LauncherVisualStateTests
{
    [Fact]
    public void Progress_UnknownTotalDoesNotDisplayFakeZeroOrCompletion()
    {
        LauncherProgressState pending = LauncherProgressState.Create("checking", 0, 0, 0, 0);
        Assert.Null(pending.Percent);
        Assert.Equal("核對正式機版本與檔案清單", pending.Label);
        Assert.Empty(pending.Files);
    }

    [Fact]
    public void Progress_LargeFileReportsByteProgressBeforeAnyFileCompletes()
    {
        LauncherProgressState progress = LauncherProgressState.Create("scanning", 5_000_000_000, 10_000_000_000, 0, 1);
        Assert.Equal(50, progress.Percent);
        Assert.Equal("檔案 0 / 1", progress.Files);
        Assert.True(progress.IsVerification);
    }

    [Fact]
    public void Progress_MetadataInventoryUsesFileCountUntilBytesAreKnown()
    {
        LauncherProgressState progress = LauncherProgressState.Create("inspecting", 0, 0, 125, 500);
        Assert.Equal(25, progress.Percent);
        Assert.Equal("檔案 125 / 500", progress.Files);
        Assert.True(progress.IsVerification);
    }

    [Theory]
    [InlineData("scanning", 100)]
    [InlineData("verifying", 100)]
    [InlineData("committing", 100)]
    [InlineData("downloading", 99)]
    [InlineData("assembling", 99)]
    public void Progress_CompletedScanIsACompletedStepWhileDownloadStillNeedsFinalProcessing(string stage, double expected)
    {
        Assert.Equal(expected, LauncherProgressState.Create(stage, 100, 100, 1, 1).Percent);
    }

    [Theory]
    [InlineData(-1, 100, 0)]
    [InlineData(101, 100, 100)]
    [InlineData(9999, 10000, 99.9)]
    public void Progress_PercentageIsBoundedAndNeverRoundsAnUnfinishedScanTo100(long done, long total, double expected)
    {
        Assert.Equal(expected, LauncherProgressState.Create("scanning", done, total, 0, 0).Percent);
    }

    [Theory]
    [InlineData(false, true, true, true, false, true, 0)]
    [InlineData(true, false, true, true, false, true, 0)]
    [InlineData(true, true, false, true, false, true, 0)]
    [InlineData(true, true, true, false, false, true, 0)]
    [InlineData(true, true, true, true, true, true, 0)]
    [InlineData(true, true, true, true, false, false, 0)]
    [InlineData(true, true, true, true, false, true, 1)]
    public void LosingAnyPlaybackQualification_StopsOnceAndDoesNotRecreateClocks(bool requested, bool loaded, bool visible, bool active, bool minimized, bool home, int games)
    {
        var state = new LauncherVisualState();
        Assert.Equal(MotionTransition.Start, state.Update(true, true, true, true, false, true, 0));
        Assert.Equal(MotionTransition.Stop, state.Update(requested, loaded, visible, active, minimized, home, games));
        Assert.False(state.IsRunning);
        Assert.Equal(MotionTransition.None, state.Update(requested, loaded, visible, active, minimized, home, games));
    }

    [Fact]
    public void ReturningToForeground_ResumesOnlyWhenPlayersSettingStillAllowsIt()
    {
        var state = new LauncherVisualState();
        Assert.Equal(MotionTransition.Start, state.Update(true, true, true, true, false, true, 0));
        Assert.Equal(MotionTransition.None, state.Update(true, true, true, true, false, true, 0));
        Assert.Equal(MotionTransition.Stop, state.Update(true, true, true, false, false, true, 0));
        Assert.Equal(MotionTransition.None, state.Update(false, true, true, true, false, true, 0));
        Assert.Equal(MotionTransition.Start, state.Update(true, true, true, true, false, true, 0));
    }

    [Fact]
    public void BannerCache_ReusesDecodedImageAndEvictsAtItsFixedLimit()
    {
        var cache = new LauncherImageCache<object>(3);
        int created = 0;
        object Create(Uri _) { created++; return new(); }
        Uri[] uris = Enumerable.Range(1, 4).Select(i => new Uri($"https://example.invalid/{i}.png")).ToArray();
        object first = cache.GetOrCreate(uris[0], Create);
        Assert.Same(first, cache.GetOrCreate(uris[0], Create));
        Assert.Equal(1, created);
        foreach (Uri uri in uris.Skip(1)) cache.GetOrCreate(uri, Create);
        Assert.Equal(4, created);
        Assert.NotSame(first, cache.GetOrCreate(uris[0], Create));
        Assert.Equal(5, created);
    }
}
