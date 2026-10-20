using Google.Protobuf;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

public sealed class LoadTestReplyStateTests
{
    [Fact]
    public void Last_typed_replies_are_bounded_replaced_and_truncated()
    {
        var reducer = new WorldReducer();
        for (uint id = 1; id <= 32; id++)
            reducer.RecordReply(new BotEvent(BotEventKind.Game) { MsgId = id, Body = Enumerable.Repeat((byte)id, 300).ToArray() }, id);

        var first = reducer.Snapshot();
        Assert.Equal(32, first.LastReplies.Count);
        Assert.Equal(300, first.LastReplies[1].OriginalLength);
        Assert.Equal(256, first.LastReplies[1].BodyPrefix.Length);
        Assert.True(first.LastReplies[1].Truncated);
        Assert.Equal(1, first.LastReplies[1].ElapsedMs);

        reducer.RecordReply(new BotEvent(BotEventKind.Game) { MsgId = 1, Body = [7, 8] }, 42);
        reducer.RecordReply(new BotEvent(BotEventKind.Game) { MsgId = 33, Body = [9] }, 43);
        var last = reducer.Snapshot();
        Assert.Equal(32, last.LastReplies.Count);
        Assert.False(last.LastReplies.ContainsKey(2)); // Replaced id 1 became newest, so id 2 is evicted.
        Assert.Equal(new byte[] { 7, 8 }, last.LastReplies[1].BodyPrefix);
        Assert.Equal(2, last.LastReplies[1].OriginalLength);
        Assert.False(last.LastReplies[1].Truncated);
        Assert.Equal(42, last.LastReplies[1].ElapsedMs);
        Assert.Equal(new byte[] { 9 }, last.LastReplies[33].BodyPrefix);

        // A report snapshot owns its prefix even after the reducer replaces that ID again.
        reducer.RecordReply(new BotEvent(BotEventKind.Game) { MsgId = 1, Body = [10] }, 44);
        Assert.Equal(new byte[] { 7, 8 }, last.LastReplies[1].BodyPrefix);
    }

    [Fact]
    public void Begin_measurement_drops_reply_samples_but_keeps_causal_bases()
    {
        var reducer = new WorldReducer();
        var basePacket = new AoIViewChangeData();
        basePacket.Bases.Add(new EntityBase { Uuid = 42, EntityType = 3 });
        reducer.Observe(new BotEvent(BotEventKind.Game)
        { MsgId = GameAoiSink.SyncAoiView, Body = basePacket.ToByteArray() });
        reducer.RecordReply(new BotEvent(BotEventKind.Game) { MsgId = 100, Body = [1, 2, 3] }, 3);
        reducer.RecordReply(new BotEvent(BotEventKind.Rpc) { Name = "scene-ready", Body = [4] }, 4);
        Assert.Single(reducer.Snapshot().LastReplies); // RPC events have no game message ID.

        reducer.BeginMeasurement();
        var measured = reducer.Snapshot();
        Assert.Empty(measured.LastReplies);
        Assert.Empty(measured.ByIdCount);
        Assert.Equal(1, measured.CurrentEntities);
        Assert.Equal(1, measured.MaxVisiblePlayers);
    }
}
