using System.Net;
using System.Net.Sockets;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// How a kicked voice connection ends, on a real loopback socket: the Kicked frame is the last frame the client reads,
/// followed by an orderly close (FIN), not a reset that would discard it, and the server's socket closes at most
/// <see cref="VoiceConnection.KickedCloseDelay"/> later even if the client never closes its side.
/// </summary>
public sealed class VoiceConnectionKickTests
{
    private static async Task<(Socket Server, Socket Client)> ConnectedPairAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accepted = listener.AcceptAsync();
        await client.ConnectAsync(listener.LocalEndPoint ?? throw new InvalidOperationException("listener not bound"));
        return (await accepted, client);
    }

    /// <summary>Everything the client receives until the server's FIN; a reset throws instead.</summary>
    private static async Task<List<(byte Type, byte[] Payload)>> ReadUntilClosedAsync(Socket client)
    {
        using var received = new MemoryStream();
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int n;
        while ((n = await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token)) > 0) received.Write(buffer, 0, n);
        received.Position = 0;
        var frames = new List<(byte, byte[])>();
        while (received.Position < received.Length)
            frames.Add(await VoiceFrame.ReadAsync(received, 1024, CancellationToken.None));
        return frames;
    }

    [Fact]
    public async Task KickedIsTheLastFrameAndAnOrderlyCloseFollowsIt()
    {
        var (server, client) = await ConnectedPairAsync();
        using var clientSide = client;
        using var connection = new VoiceConnection(server);
        // A frame the server never reads: closing with unread input is what sends a reset.
        await client.SendAsync(VoiceFrame.Write(VoiceUp.Ping, []), SocketFlags.None);
        // Queued before the writer starts, so the order on the wire is fixed.
        connection.SendPong();
        connection.SendAudio(VoiceConnection.BuildAudioPayload(7, 1, [1, 2, 3])); // dropped by the kick
        connection.Kick(VoiceKickCode.Flood);
        connection.SendAudio(VoiceConnection.BuildAudioPayload(7, 2, [4, 5, 6])); // refused after the kick
        connection.SendPong();                                                      // refused after the kick
        connection.Kick(VoiceKickCode.IdleTimeout);                                 // only the first kick counts
        var writer = connection.RunWriterAsync();

        var frames = await ReadUntilClosedAsync(client);

        Assert.Equal([VoiceDown.Pong, VoiceDown.Kicked], frames.Select(f => f.Type));
        Assert.Equal([VoiceKickCode.Flood], frames[1].Payload);
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheSocketClosesAfterTheDelayWhenTheClientNeverDoes()
    {
        var (server, client) = await ConnectedPairAsync();
        using var clientSide = client;
        using var connection = new VoiceConnection(server);
        connection.Kick(VoiceKickCode.RemovedFromRoom);
        var writer = connection.RunWriterAsync();
        await ReadUntilClosedAsync(client); // the FIN; the client keeps its own side open

        var deadline = DateTime.UtcNow + VoiceConnection.KickedCloseDelay + TimeSpan.FromSeconds(5);
        while (server.Connected && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.False(server.Connected);
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
