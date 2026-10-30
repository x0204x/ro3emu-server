using Ro3Emu.Launcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class LauncherVisualStateTests
{
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
