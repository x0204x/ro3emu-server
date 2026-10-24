namespace Ro3Emu.Tests;

public sealed class ChatSubscriptionTests
{
    [Theory]
    [InlineData("4_1")]
    [InlineData("@TGS#_1_0")]
    public async Task RepeatedChannelJoinDoesNotReplayAlreadyReceivedMessages(string channel)
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await alice.JoinChatAsync(channel);
        await alice.SendChatAsync(channel, 2, "before Bob joins");
        await alice.NextChatLineAsync();
        await bob.JoinChatAsync(channel);
        Assert.Equal("before Bob joins", (await bob.NextChatLineAsync()).Content);

        await alice.SendChatAsync(channel, 2, "already delivered live");
        await alice.NextChatLineAsync();
        Assert.Equal("already delivered live", (await bob.NextChatLineAsync()).Content);

        // A duplicate subscription still receives its ACK, but must not inject old lines again.
        await bob.JoinChatAsync(channel);
        await alice.SendChatAsync(channel, 2, "next live message");
        Assert.Equal("next live message", (await bob.NextChatLineAsync()).Content);
        bob.Timeout = TimeSpan.FromMilliseconds(300);
        await Assert.ThrowsAsync<TimeoutException>(() => bob.NextChatLineAsync());
    }
}
