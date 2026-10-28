using System.Buffers.Binary;
using Ro3Emu.Bot;

namespace Ro3Emu.Tests;

public sealed class GameMessageBatchTests
{
    [Fact]
    public async Task MultipleRecordsPreserveOrderAndAllowAnEmptyPayload()
    {
        await using var bot = NewBot();
        byte[] batch = Batch((31008u, []), (31081u, [1, 2, 3]));

        bot.UnpackServerBatch(batch);

        BotEvent[] events = bot.Inbox.Snapshot().ToArray();
        Assert.Collection(events,
            first =>
            {
                Assert.Equal(BotEventKind.Game, first.Kind);
                Assert.Equal(31008u, first.MsgId);
                Assert.Empty(first.Body);
            },
            second =>
            {
                Assert.Equal(BotEventKind.Game, second.Kind);
                Assert.Equal(31081u, second.MsgId);
                Assert.Equal([1, 2, 3], second.Body);
            });
    }

    [Fact]
    public async Task TruncatedSecondHeaderIsRejectedAfterTheCompleteFirstRecord()
    {
        await using var bot = NewBot();
        byte[] complete = Batch((31008u, [7]));
        byte[] truncated = [.. complete, 0, 0, 0];

        var error = Assert.Throws<InvalidDataException>(() => bot.UnpackServerBatch(truncated));

        Assert.Equal("truncated game-message batch header", error.Message);
        BotEvent only = Assert.Single(bot.Inbox.Snapshot());
        Assert.Equal(31008u, only.MsgId);
        Assert.Equal([7], only.Body);
    }

    [Fact]
    public async Task TruncatedSecondPayloadIsRejectedAfterTheCompleteFirstRecord()
    {
        await using var bot = NewBot();
        byte[] first = Batch((31008u, [7]));
        byte[] secondHeader = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(secondHeader, 2);
        BinaryPrimitives.WriteUInt32BigEndian(secondHeader.AsSpan(4), 31081u);
        byte[] truncated = [.. first, .. secondHeader, 1];

        var error = Assert.Throws<InvalidDataException>(() => bot.UnpackServerBatch(truncated));

        Assert.Equal("invalid game-message batch payload length", error.Message);
        BotEvent only = Assert.Single(bot.Inbox.Snapshot());
        Assert.Equal(31008u, only.MsgId);
        Assert.Equal([7], only.Body);
    }

    private static BotClient NewBot() => new("http://127.0.0.1/", "unused-in-this-test");

    private static byte[] Batch(params (uint Id, byte[] Payload)[] records)
    {
        byte[] batch = new byte[records.Sum(record => 8 + record.Payload.Length)];
        int offset = 0;
        foreach ((uint id, byte[] payload) in records)
        {
            BinaryPrimitives.WriteInt32BigEndian(batch.AsSpan(offset), payload.Length);
            BinaryPrimitives.WriteUInt32BigEndian(batch.AsSpan(offset + 4), id);
            payload.CopyTo(batch, offset + 8);
            offset += 8 + payload.Length;
        }
        return batch;
    }
}
