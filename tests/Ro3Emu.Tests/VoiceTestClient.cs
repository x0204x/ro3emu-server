using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// A raw voice-gate client for tests: speaks <see cref="VoiceFrame"/> directly over a plain TcpClient (the in-game
/// client lives in Ro3Emu.ClientHooks, which needs the game; this plays its part). Frames are buffered as they arrive; <see
/// cref="WaitAsync"/> lets a test pick a specific one out of order, the way BotClient's Inbox does for the game gate.
/// </summary>
public sealed class VoiceTestClient : IAsyncDisposable
{
    private readonly int _port;
    private readonly List<(byte Type, byte[] Payload)> _buffer = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _reader;

    public VoiceTestClient(int port) => _port = port;

    public string Tag { get; set; } = "?";
    public bool Closed { get; private set; }

    public async Task ConnectAsync()
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(IPAddress.Loopback, _port);
        _stream = _tcp.GetStream();
        _reader = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await VoiceFrame.ReadAsync(_stream!, 4096, _cts.Token).ConfigureAwait(false);
                lock (_sync) _buffer.Add(frame);
                _signal.Release();
            }
        }
        catch { /* the socket closed or the test is tearing down: nothing more to read */ }
        finally { Closed = true; }
    }

    private Task SendAsync(byte type, byte[] payload) => _stream!.WriteAsync(VoiceFrame.Write(type, payload), _cts.Token).AsTask();

    public Task AuthAsync(long roleId, byte[] token)
    {
        var payload = new byte[25];
        payload[0] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1, 8), roleId);
        token.AsSpan(0, 16).CopyTo(payload.AsSpan(9, 16));
        return SendAsync(VoiceUp.Auth, payload);
    }

    public Task JoinAsync(string channel)
    {
        var chan = Encoding.ASCII.GetBytes(channel);
        var payload = new byte[1 + chan.Length];
        payload[0] = (byte)chan.Length;
        chan.CopyTo(payload, 1);
        return SendAsync(VoiceUp.Join, payload);
    }

    public Task LeaveAsync() => SendAsync(VoiceUp.Leave, []);
    public Task MicAsync(bool on) => SendAsync(VoiceUp.Mic, [on ? (byte)1 : (byte)0]);
    public Task SpeakerAsync(bool on) => SendAsync(VoiceUp.Speaker, [on ? (byte)1 : (byte)0]);
    public Task PingAsync() => SendAsync(VoiceUp.Ping, []);

    public Task SendAudioAsync(ushort seq, byte[] opus)
    {
        var payload = new byte[2 + opus.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), seq);
        opus.CopyTo(payload, 2);
        return SendAsync(VoiceUp.Audio, payload);
    }

    /// <summary>The next already-buffered frame matching <paramref name="predicate"/>, waiting for new ones until
    /// <paramref name="timeout"/>; non-matching frames stay buffered for a later call.</summary>
    public async Task<(byte Type, byte[] Payload)> WaitAsync(Func<byte, byte[], bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_sync)
            {
                for (int i = 0; i < _buffer.Count; i++)
                    if (predicate(_buffer[i].Type, _buffer[i].Payload))
                    {
                        var found = _buffer[i];
                        _buffer.RemoveAt(i);
                        return found;
                    }
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("expected voice frame did not arrive in time");
            try { await _signal.WaitAsync(remaining, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new TimeoutException("expected voice frame did not arrive in time"); }
        }
    }

    public Task<(byte Type, byte[] Payload)> WaitTypeAsync(byte type, TimeSpan timeout) => WaitAsync((t, _) => t == type, timeout);

    /// <summary>Confirms no frame matching <paramref name="predicate"/> turns up within <paramref name="timeout"/>.</summary>
    public async Task ExpectNoneAsync(Func<byte, byte[], bool> predicate, TimeSpan timeout)
    {
        try
        {
            var (type, _) = await WaitAsync(predicate, timeout);
            throw new Exception($"expected no matching voice frame, but type 0x{type:x2} arrived");
        }
        catch (TimeoutException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_reader != null) await _reader; } catch { }
        _stream?.Dispose();
        _tcp?.Dispose();
        _signal.Dispose();
        _cts.Dispose();
    }
}

/// <summary>A TimeProvider the test controls, for ticket expiry (TicketSeconds) without a real wait.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
