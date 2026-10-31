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
        Assert.Equal(current, compose.Invoke(null, [app, resource, current, null]));
    }

    [Theory]
    [InlineData("12345", "9.8.7_6.5.4 Build 12345")]
    [InlineData("00123", "9.8.7_6.5.4 Build 123")]
    [InlineData("0", "9.8.7_6.5.4")]
    [InlineData("-123", "9.8.7_6.5.4")]
    [InlineData("update42", "9.8.7_6.5.4")]
    [InlineData("123\n", "9.8.7_6.5.4")]
    public void OnlyAValidatedPositiveOfficialSequenceSuppliesTheBuildLabel(string hint, string expected)
    {
        Assert.Equal(expected, NativeVersionText.Compose("9.8.7", "6.5.4", "old prefab", hint));
    }

    [Fact]
    public void VerifiedIdentityReplacesAStaleBuildWithoutInventingMissingApplicationVersions()
    {
        Assert.Equal("9.8.7_6.5.4 Build 12345", NativeVersionText.Compose("9.8.7", "6.5.4", "9.8.7_6.5.4 Build 99", "12345"));
        Assert.Equal("native", NativeVersionText.Compose("", "6.5.4", "native", "12345"));
    }
}
