using Ro3Emu.Patcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class RestartHandoffTests
{
    const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void NormalStartDoesNotWait()
    {
        Assert.False(RestartHandoff.HasMarker([]));
        Assert.True(RestartHandoff.WaitForPreviousProcess([], TimeSpan.Zero));
    }

    [Fact]
    public void ValidHandoffParsesProcessAndToken()
    {
        string[] args = [RestartHandoff.PreviousProcessOption, "123", RestartHandoff.TokenOption, Token];

        Assert.True(RestartHandoff.TryParse(args, out int processId, out string? token));
        Assert.Equal(123, processId);
        Assert.Equal(Token, token);
    }

    [Theory]
    [InlineData("0", "0123456789abcdef0123456789abcdef")]
    [InlineData("abc", "0123456789abcdef0123456789abcdef")]
    [InlineData("123", "too-short")]
    [InlineData("123", "0123456789abcdef0123456789abcdeg")]
    public void InvalidHandoffIsRejected(string processId, string token)
    {
        string[] args = [RestartHandoff.PreviousProcessOption, processId, RestartHandoff.TokenOption, token];

        Assert.False(RestartHandoff.TryParse(args, out _, out _));
        Assert.False(RestartHandoff.WaitForPreviousProcess(args, TimeSpan.Zero));
    }

    [Fact]
    public void MissingPreviousProcessIsAlreadyHandedOff()
    {
        string[] args = [RestartHandoff.PreviousProcessOption, int.MaxValue.ToString(), RestartHandoff.TokenOption, Token];

        Assert.True(RestartHandoff.WaitForPreviousProcess(args, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void LivePreviousProcessHonorsTimeout()
    {
        string[] args = [RestartHandoff.PreviousProcessOption, Environment.ProcessId.ToString(), RestartHandoff.TokenOption, Token];

        Assert.False(RestartHandoff.WaitForPreviousProcess(args, TimeSpan.FromMilliseconds(10)));
    }
}
