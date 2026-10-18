using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>A connection that never encrypts or signs in cannot hold a gate slot, and one address cannot take them all.</summary>
public sealed class GateLimitTests
{
    private sealed class Probe(int port) : GateListener(IPAddress.Loopback, port, NullLogger.Instance)
    {
        protected override string Name => "probe";
        protected override ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body) => ValueTask.CompletedTask;
        protected override bool IsSignedIn(PhonestChannel channel) => false;
    }

    [Fact]
    public async Task ASilentConnectionIsClosedAtTheHandshakeDeadline()
    {
        int port = TestPorts.FreePort();
        var gate = new Probe(port) { EncryptDeadline = TimeSpan.FromMilliseconds(300) };
        await gate.StartAsync(CancellationToken.None);
        try
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);
            var buffer = new byte[16];
            int read = await client.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, read); // the server closed it
            await WaitAsync(() => gate.OutstandingServeTasks == 0);
        }
        finally { await gate.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OneAddressHoldsAtMostItsShare()
    {
        int port = TestPorts.FreePort();
        var gate = new Probe(port) { MaxPerAddress = 2, ExemptLoopback = false };
        await gate.StartAsync(CancellationToken.None);
        var clients = new List<Socket>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, port);
            }
            int read = await clients[2].ReceiveAsync(new byte[16], SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, read); // the third was refused
            Assert.Equal(2, gate.OutstandingServeTasks);
            clients[0].Dispose();
            await WaitAsync(() => gate.OutstandingServeTasks == 1);
            var again = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clients.Add(again);
            await again.ConnectAsync(IPAddress.Loopback, port);
            await WaitAsync(() => gate.OutstandingServeTasks == 2); // a freed slot is usable again
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            await gate.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done());
    }
}
