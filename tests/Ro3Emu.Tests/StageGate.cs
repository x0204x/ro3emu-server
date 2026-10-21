using Google.Protobuf;
using Ro3Emu.Bot;

namespace Ro3Emu.Tests;

/// <summary>Asserts for messages the dispatcher's stage gate refuses (see LoginGateTests).</summary>
internal static class StageGate
{
    /// <summary>
    /// Sends <paramref name="request"/> and a heartbeat: when the Pong arrives everything sent before it has been
    /// handled, and no answer <paramref name="answerId"/> (default: the request's own id) may have come.
    /// </summary>
    public static async Task AssertUnansweredAsync(BotClient bot, uint msgId, IMessage request, uint? answerId = null)
    {
        uint answer = answerId ?? msgId;
        int before = bot.Inbox.Snapshot().Count(e => e.IsGame(answer));
        await bot.SendGameAsync(msgId, request);
        await bot.HeartbeatAsync();
        Assert.Equal(before, bot.Inbox.Snapshot().Count(e => e.IsGame(answer)));
    }
}
