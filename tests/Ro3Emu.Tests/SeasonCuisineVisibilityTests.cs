using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class SeasonCuisineVisibilityTests
{
    [Fact]
    public void ClosingTheChildPanelAllowsTheNextParentClickToOpenItImmediately()
    {
        bool visible = true, nativeVisible = true;
        int openingClicks = 0;
        void Show(bool value) => SeasonCuisineVisibility.Apply(value,
            shown => nativeVisible = shown, shown => visible = shown);

        // 自造原生子側欄的關閉回呼；沒有另造 panel、解鎖或食用動作。
        Show(false);
        Assert.False(visible);
        Assert.False(nativeVisible);

        // 父控制器原生切換分支：殘留 true 會只隱藏並 return，必須第二次才開啟。
        if (visible) Show(false);
        else { openingClicks++; Show(true); }
        Assert.Equal(1, openingClicks);
        Assert.True(visible);
        Assert.True(nativeVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedNativeVisibilityChangeDoesNotPublishTheRequestedState(bool requested)
    {
        bool visible = !requested;
        int stateWrites = 0;
        Assert.Throws<IOException>(() => SeasonCuisineVisibility.Apply(requested,
            _ => throw new IOException("自造原生側欄顯示故障"),
            shown => { visible = shown; stateWrites++; }));
        Assert.Equal(!requested, visible);
        Assert.Equal(0, stateWrites);
    }

    [Fact]
    public void NativeShowRunsOnceBeforeItsSuccessfulStateIsPublished()
    {
        var calls = new List<string>();
        SeasonCuisineVisibility.Apply(false,
            value => calls.Add("native:" + value), value => calls.Add("state:" + value));
        Assert.Equal(new[] { "native:False", "state:False" }, calls);
    }
}
