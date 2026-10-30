using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class TitleModalGeometryTests
{
    [Theory]
    [InlineData(1280, 720, 1228, 680)]
    [InlineData(800, 600, 1228, 680)]
    [InlineData(1920, 1080, 1228, 680)]
    [InlineData(720, 1280, 1228, 680)]
    [InlineData(3840, 2160, 240, 320)]
    public void NativeContentFitsWithoutStretchingOrLeavingTheScreen(float width, float height, float nativeWidth, float nativeHeight)
    {
        Assert.True(TitleModalGeometry.TryFit(width, height, nativeWidth, nativeHeight, out var fit));
        Assert.InRange(fit.Scale, 0.000001f, 1);
        Assert.True(fit.Width <= width - 2 * TitleModalGeometry.Margin + 0.01f);
        Assert.True(fit.Height <= height - 2 * TitleModalGeometry.Margin + 0.01f);
        Assert.Equal(nativeWidth / nativeHeight,
            (fit.Width - 2 * TitleModalGeometry.Padding) / (fit.Height - 2 * TitleModalGeometry.Padding - TitleModalGeometry.Header), 4);
    }

    [Theory]
    [InlineData(0, 720, 1228, 680)]
    [InlineData(1280, -1, 1228, 680)]
    [InlineData(1280, 720, 0, 680)]
    [InlineData(1280, 720, 1228, -1)]
    [InlineData(70, 110, 1228, 680)]
    [InlineData(float.NaN, 720, 1228, 680)]
    [InlineData(1280, float.PositiveInfinity, 1228, 680)]
    public void InvalidOrTooSmallBoundsAreRejected(float width, float height, float nativeWidth, float nativeHeight)
        => Assert.False(TitleModalGeometry.TryFit(width, height, nativeWidth, nativeHeight, out _));
}
