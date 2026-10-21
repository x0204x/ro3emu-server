using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>The connection layer on a real loopback socket pair: framing, limits and a reader that stopped reading.</summary>
public sealed class PhonestChannelTests : IAsyncLifetime
{
    private Socket _client = null!, _server = null!;

    public async Task InitializeAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        _client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptAsync();
        await _client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        _server = await accept;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static byte[] Frame(ushort index, int bodyLength, byte fill = 7)
    {
        var frame = new byte[6 + bodyLength];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bodyLength + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), index);
        frame.AsSpan(6).Fill(fill);
        return frame;
    }

    [Fact]
    public async Task ManyFramesInOneChunkAndASplitFrameArriveWholeAndInOrder()
    {
        await using var channel = new PhonestChannel(_server, NullLogger.Instance);
        var seen = new List<(ushort, int)>();
        var done = new TaskCompletionSource();
        var run = channel.RunAsync((_, index, body) =>
        {
            seen.Add((index, body.Length));
            if (seen.Count == 202) done.TrySetResult();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        // 200 small frames in one write, then one frame split across two writes.
        var burst = Enumerable.Range(0, 200).SelectMany(i => Frame((ushort)i, i % 17)).ToArray();
        await _client.SendAsync(burst, SocketFlags.None);
        var big = Frame(1000, 100_000);
        await _client.SendAsync(big.AsMemory(0, 3), SocketFlags.None);
        await Task.Delay(50);
        await _client.SendAsync(big.AsMemory(3), SocketFlags.None);
        await _client.SendAsync(Frame(1001, 0), SocketFlags.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => ((ushort)i, i % 17)), seen.Take(200));
        Assert.Equal(((ushort)1000, 100_000), seen[200]);
        Assert.Equal(((ushort)1001, 0), seen[201]);
        _client.Shutdown(SocketShutdown.Send);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OversizedFrameIsRefused()
    {
        await using var channel = new PhonestChannel(_server, NullLogger.Instance);
        var header = new byte[6];
        BinaryPrimitives.WriteInt32LittleEndian(header, 2 * 1024 * 1024);
        await _client.SendAsync(header, SocketFlags.None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            channel.RunAsync((_, _, _) => ValueTask.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ClientThatStopsReadingIsClosedInsteadOfQueuingForever()
    {
        await using var channel = new PhonestChannel(_server, NullLogger.Instance);
        _client.ReceiveBufferSize = 4096;
        // 32 MB offered to a client that reads nothing: the queue stays bounded and the connection ends.
        for (int i = 0; i < 320; i++)
        {
            await channel.SendRawAsync(1, new byte[100_000]);
            Assert.True(channel.QueuedBytes <= 8L * 1024 * 1024);
        }
        // The client finally reads: it gets what the kernel buffered, then the end of the stream (not 32 MB).
        var buf = new byte[64 * 1024];
        long total = 0;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            int n;
            try { n = await _client.ReceiveAsync(buf, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (SocketException) { break; } // a reset is an end too
            if (n == 0) break;
            total += n;
        }
        Assert.True(total < 32_000_000, $"read {total} bytes");
        Assert.True(DateTime.UtcNow < until, "the connection never ended");
    }
}
