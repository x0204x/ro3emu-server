using Ro3Emu.ClientHooks;

namespace Ro3Emu.Setup.Tests;

public sealed class NativeVersionTextTests
{
    [Theory]
    [InlineData("9.8.7", "6.5.4", "old prefab text", "9.8.7_6.5.4")]
    [InlineData("9.8.7", "6.5.4", "9.8.7_6.5.4 Build 1234", "9.8.7_6.5.4 Build 1234")]
    [InlineData("9.8.7", "6.5.4", "9.8.7_6.5.40 Build 1234", "9.8.7_6.5.4")]
    [InlineData("9.8.7", "6.5.4", "9.8.70_6.5.4 Build 1234", "9.8.7_6.5.4")]
    public void UsesCurrentNativeVersionsAndPreservesOnlyTheirExactBuildPrefix(string app, string resource, string current, string expected)
    {
        string actual = NativeVersionText.Compose(app, resource, current);
        Assert.Equal(expected, actual);
        Assert.Equal(actual, NativeVersionText.Compose(app, resource, actual));
    }

    [Theory]
    [InlineData("9.8.7", "")]
    [InlineData("", "6.5.4")]
    [InlineData(null, "6.5.4")]
    [InlineData("9.8.7", null)]
    public void MissingNativeSourcePreservesCurrentTextWithoutInventingAValue(string? app, string? resource)
    {
        const string current = "native label with its original format";
        var compose = typeof(NativeVersionText).GetMethod("Compose", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Compose is unavailable");
        Assert.Equal(current, compose.Invoke(null, [app, resource, current]));
    }
}
