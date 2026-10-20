using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ro3Emu.ClientHooks;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// A raw voice-gate client for tests: speaks <see cref="VoiceFrame"/> directly over a plain TcpClient (the in-game
/// client lives in Ro3Emu.ClientHooks, which needs the game; this plays its part). Frames are buffered as they arrive; <see
/// cref="WaitAsync"/> lets a test pick a specific one out of order, the way BotClient's Inbox does for the game gate.
/// After Auth every frame both ways is sealed with <see cref="VoiceCipher"/>, like the real client; the raw bytes sent
/// and received are kept (<see cref="RawSent"/>, <see cref="RawReceived"/>) so a test can look at the wire itself.
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
    private volatile VoiceCipher? _cipher;
    private readonly MemoryStream _rawSent = new(), _rawReceived = new();

    public VoiceTestClient(int port) => _port = port;

    public string Tag { get; set; } = "?";
    public bool Closed { get; private set; }

    public byte[] RawSent { get { lock (_rawSent) return _rawSent.ToArray(); } }
    public byte[] RawReceived { get { lock (_rawReceived) return _rawReceived.ToArray(); } }

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
                var frame = await VoiceFrame.ReadAsync(new Recording(_stream!, _rawReceived), 4096, _cts.Token).ConfigureAwait(false);
                var cipher = _cipher;
                if (cipher != null)
                {
                    byte[]? plain = cipher.Open(frame.Type, frame.Payload);
                    if (plain == null) return; // a frame that fails its tag: the link is over, as for the real client
                    frame = (frame.Type, plain);
                }
                lock (_sync) _buffer.Add(frame);
                _signal.Release();
            }
        }
        catch { /* the socket closed or the test is tearing down: nothing more to read */ }
        finally { Closed = true; }
    }

    private Task SendAsync(byte type, byte[] payload)
    {
        var cipher = _cipher;
        return SendRawAsync(type, cipher == null ? payload : cipher.Seal(type, payload));
    }

    /// <summary>One frame exactly as given, not sealed: for tests that forge or replay wire bytes.</summary>
    public Task SendRawAsync(byte type, byte[] payload) => SendBytesAsync(VoiceFrame.Write(type, payload));

    public Task SendBytesAsync(byte[] bytes)
    {
        lock (_rawSent) _rawSent.Write(bytes);
        return _stream!.WriteAsync(bytes, _cts.Token).AsTask();
    }

    /// <summary>Auth with the ticket's token; every later frame is sealed with the ticket's secret (a random one when
    /// none is given: such a client can still be refused or accepted, but it cannot read the reply).</summary>
    public Task AuthAsync(long roleId, byte[] token, byte[]? secret = null, byte version = VoiceUp.AuthVersion)
    {
        secret ??= VoiceCipher.NewSecret();
        var payload = new byte[VoiceUp.AuthSize];
        payload[0] = version;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1, 8), roleId);
        token.AsSpan(0, 16).CopyTo(payload.AsSpan(9, 16));
        VoiceCipher.AuthProof(secret, roleId, token).CopyTo(payload, 25);
        // Before the Auth goes out: the reader must already open AuthOk with it.
        _cipher = VoiceCipher.ForClient(secret);
        return SendRawAsync(VoiceUp.Auth, payload);
    }

    public Task AuthAsync(long roleId, TestVoiceKey key) => AuthAsync(roleId, key.Token, key.Secret);

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
            throw new InvalidOperationException($"expected no matching voice frame, but type 0x{type:x2} arrived");
        }
        catch (TimeoutException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_reader != null) await _reader; } catch { }
        _stream?.Dispose();
        _tcp?.Dispose();
        _cipher?.Dispose();
        _signal.Dispose();
        _cts.Dispose();
    }
}

/// <summary>A voice ticket's token and session secret, as a test hands them to <see cref="VoiceTestClient.AuthAsync(long, TestVoiceKey)"/>.</summary>
public sealed record TestVoiceKey(byte[] Token, byte[] Secret);

/// <summary>Copies every byte read through it into <paramref name="log"/> (the raw wire, for the plaintext checks).</summary>
internal sealed class Recording(Stream inner, MemoryStream log) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        lock (log) log.Write(buffer.Span[..n]);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A TimeProvider the test controls, for ticket expiry (TicketSeconds) without a real wait.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
