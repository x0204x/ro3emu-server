using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Tencent.IM;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:ChatFlood (ChatFloodGuard, ChatService.Flood.cs): line budgets, duplicates, the mute and its notice.</summary>
public sealed class ChatFloodTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ChatFloodGuard Guard, Clock Clock) Guard(Action<ChatFloodOptions>? configure = null)
    {
        var options = new ServerOptions { ChatFlood = { Enabled = true, WindowSeconds = 10, OpenLines = 3, PrivateLines = 5, DuplicateSeconds = 30, MuteSeconds = 60 } };
        configure?.Invoke(options.ChatFlood);
        var clock = new Clock();
        return (new ChatFloodGuard(Options.Create(options), clock), clock);
    }

    [Fact]
    public void OffByDefaultEveryLinePasses()
    {
        var guard = new ChatFloodGuard(Options.Create(new ServerOptions()), new Clock());
        for (int i = 0; i < 50; i++) Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_1_1", open: true, "same").Verdict);
    }

    [Fact]
    public void PastTheOpenBudgetTheAccountIsMutedUntilTheMuteEnds()
    {
        var (guard, clock) = Guard();
        for (int i = 0; i < 3; i++) Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_1_1", true, "line " + i).Verdict);
        var (verdict, until) = guard.Check("a", "@TGS#_1_1", true, "line 3");
        Assert.Equal(ChatFloodGuard.Verdict.MuteStarted, verdict);
        Assert.Equal(clock.Now.AddSeconds(60), until);
        // Muted on every channel, whispers included; another account is not.
        Assert.Equal(ChatFloodGuard.Verdict.Muted, guard.Check("a", "42", false, "psst").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("b", "@TGS#_1_1", true, "hello").Verdict);
        clock.Now = until;
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_1_1", true, "back").Verdict);
    }

    [Fact]
    public void TheWindowSlidesAndPrivateChatHasItsOwnLooserBudget()
    {
        var (guard, clock) = Guard();
        for (int i = 0; i < 3; i++) Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_1_1", true, "w" + i).Verdict);
        for (int i = 0; i < 5; i++) Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "2_7", false, "g" + i).Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.MuteStarted, guard.Check("a", "3_9", false, "t").Verdict);

        var (other, otherClock) = Guard();
        for (int i = 0; i < 3; i++) Assert.Equal(ChatFloodGuard.Verdict.Pass, other.Check("a", "@TGS#_1_1", true, "w" + i).Verdict);
        otherClock.Now = otherClock.Now.AddSeconds(10);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, other.Check("a", "@TGS#_1_1", true, "later").Verdict);
    }

    [Fact]
    public void ARepeatedLineToTheSameChannelIsDroppedAndStillCounts()
    {
        var (guard, clock) = Guard(o => o.OpenLines = 4);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_1_1", true, "Buy  my stuff").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.Duplicate, guard.Check("a", "@TGS#_1_1", true, " buy my STUFF ").Verdict);
        // Another channel or another whisper target is a new line.
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_8_1", true, "buy my stuff").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "42", false, "ok").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "43", false, "ok").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.Duplicate, guard.Check("a", "42", false, "ok").Verdict);
        clock.Now = clock.Now.AddSeconds(31);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, guard.Check("a", "@TGS#_1_1", true, "buy my stuff").Verdict);

        // Resending a dropped line uses the budget: the fourth open line within the window mutes.
        var (resend, _) = Guard(o => o.OpenLines = 2);
        Assert.Equal(ChatFloodGuard.Verdict.Pass, resend.Check("a", "@TGS#_1_1", true, "spam").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.Duplicate, resend.Check("a", "@TGS#_1_1", true, "spam").Verdict);
        Assert.Equal(ChatFloodGuard.Verdict.MuteStarted, resend.Check("a", "@TGS#_1_1", true, "spam").Verdict);
    }

    [Theory]
    [InlineData("@TGS#_1_1", 1)]
    [InlineData("2_77", 2)]
    [InlineData("4_1001", 4)]
    [InlineData("5_3", 5)]
    [InlineData("12345", 0)]
    [InlineData("x_1", 0)]
    public void TheChannelTypeIsTheGroupIdsPrefix(string group, int type) => Assert.Equal(type, ChatService.ChannelType(group));

    [Fact]
    public async Task AFloodingPlayerIsMutedWithTheClientsNoticeAndAGmIsNot()
    {
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GmAccounts = ["floodgm"];
            o.ChatFlood = new ChatFloodOptions { Enabled = true, OpenLines = 2, WindowSeconds = 600, MuteSeconds = 600 };
        });
        await using var spammer = await host.InWorldAsync("Spammer");
        await using var reader = await host.InWorldAsync("Reader");
        await using var gm = await host.InWorldAsync("FloodGm");
        const string world = "@TGS#_1_1";
        foreach (var bot in new[] { spammer, reader, gm }) await bot.JoinChatAsync(world);

        await spammer.SendChatAsync(world, 2, "one");
        await reader.Inbox.WaitAsync(e => Line(e, "one"), Wait);
        await spammer.SendChatAsync(world, 2, "two");
        await reader.Inbox.WaitAsync(e => Line(e, "two"), Wait);
        await spammer.SendChatAsync(world, 2, "three");
        var notice = SystemBoardCastMsgNtf.Parser.ParseFrom((await spammer.Inbox.WaitAsync(
            e => e.IsGame(1022) && SystemBoardCastMsgNtf.Parser.ParseFrom(e.Body).ChatId == ChatService.MutedChat, Wait)).Body);
        Assert.Equal(ChatParamType.ChatParamTimestamp, notice.ParamList[1].Type);
        Assert.True(long.Parse(notice.ParamList[1].Params[0], System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Contains(spammer.Inbox.Snapshot(), e => SendResult(e) == false);

        // The GM's lines all go out; by the time the last arrives the spammer's refused line would have too.
        for (int i = 0; i < 5; i++) await gm.SendChatAsync(world, 2, "gm " + i);
        await reader.Inbox.WaitAsync(e => Line(e, "gm 4"), Wait);
        Assert.DoesNotContain(reader.Inbox.Snapshot(), e => Line(e, "three"));
    }

    private static bool Line(BotEvent e, string text) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8
        && BitConverter.ToUInt32(e.Body, 0) == 4 && NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg.Content == text;

    private static bool? SendResult(BotEvent e) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8 && BitConverter.ToUInt32(e.Body, 0) == 3
        ? SendTextChatMessageResponse.Parser.ParseFrom(e.Body.AsSpan(8)).ChatResult : null;
}
