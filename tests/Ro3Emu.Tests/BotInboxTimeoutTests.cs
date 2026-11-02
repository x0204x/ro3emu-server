using Ro3Emu.Bot;

namespace Ro3Emu.Tests;

/// <summary>A test that times out waiting for a server message says which message it waited for.</summary>
public sealed class BotInboxTimeoutTests
{
    [Fact]
    public async Task AnExpectationsTimeoutNamesItsFilter()
    {
        using var reply = new BotInbox().Expect(e => e.IsGame(4242));
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => reply.WaitAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Contains("e.IsGame(4242)", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaitsTimeoutNamesItsFilter()
    {
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => new BotInbox().WaitAsync(e => e.IsGame(4243), TimeSpan.FromMilliseconds(20)));
        Assert.Contains("e.IsGame(4243)", timeout.Message, StringComparison.Ordinal);
    }
}
