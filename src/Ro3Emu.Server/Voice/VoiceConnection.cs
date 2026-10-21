using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// One voice-gate TCP connection: its socket, outbound queues and the state the relay (<see cref="VoiceRooms"/>)
/// tracks for it. Reads happen on the connection's own serve task; writes go through <see cref="RunWriterAsync"/>
/// so only one task ever touches the socket's send side (control and audio frames both end up here, queued).
/// Frames are queued in the clear and sealed by that one writer, in wire order, once <see cref="Cipher"/> is set
/// (after Auth); reads are opened with it, and a frame that fails to open ends the connection.
/// </summary>
public sealed class VoiceConnection(Socket socket) : IDisposable
{
    /// <summary>Audio frames queued for one slow listener before the oldest is dropped.</summary>
    public const int MaxQueuedAudio = 64;

    /// <summary>Control frames queued before the connection is considered stuck and closed.</summary>
    public const int MaxQueuedControl = 256;

    private readonly Socket _socket = socket;
    private readonly NetworkStream _stream = new NetworkStream(socket, ownsSocket: false);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly Queue<(byte Type, byte[] Payload)> _control = new();
    private readonly Queue<(byte Type, byte[] Payload)> _audio = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _closed;
    private volatile bool _closeAfterDrain;

    public IPAddress RemoteAddress { get; } = ((IPEndPoint)socket.RemoteEndPoint!).Address;
    public CancellationToken StopToken => _cts.Token;

    /// <summary>Set once the Auth frame is accepted; everything but Auth is refused before that.</summary>
    public bool Authed { get; set; }
    public string? Account { get; set; }
    public long RoleId { get; set; }
    public VoiceRateLimiter? RateLimiter { get; set; }

    /// <summary>The stream's cipher, set by the serve task when Auth is accepted and before AuthOk is queued; the
    /// writer reads the volatile field after the semaphore signal for that frame, so it always sees it.</summary>
    public Ro3Emu.ClientHooks.VoiceCipher? Cipher { get => _cipher; set => _cipher = value; }
    private volatile Ro3Emu.ClientHooks.VoiceCipher? _cipher;

    /// <summary>The budget for everything after Auth that is not Audio (Join, Leave, Mic, Speaker, Ping).</summary>
    public VoiceRateLimiter? ControlLimiter { get; set; }

    /// <summary>0 = not in a room.</summary>
    public long RoomId { get; set; }

    /// <summary>What the client last asked for with Mic; kept while the leader's mute overrides it, so lifting the
    /// mute gives the player back the mic they had.</summary>
    public bool MicWanted { get; set; }

    /// <summary>The mic the room sees and the relay forwards audio for: <see cref="MicWanted"/> unless
    /// <see cref="ChatForbidden"/>. Only VoiceRooms changes it, under its lock.</summary>
    public bool MicOn { get; set; }

    /// <summary>The team leader's forbidden-chat on this role, as the game loop last saw it (VoiceRooms refreshes it
    /// every tick and on Join). Read by the connection's own task on every Audio frame, hence volatile.</summary>
    public bool ChatForbidden { get => _chatForbidden; set => _chatForbidden = value; }
    private volatile bool _chatForbidden;
    public bool SpeakerOn { get; set; } = true;

    /// <summary>VAD state (VoiceRooms owns the transitions; the connection just stores the current value).</summary>
    public bool Speaking { get; set; }

    // Environment.TickCount64, not the injected TimeProvider: VAD silence, the idle watchdog and the rate
    // limiter all need real elapsed time, and the injected clock is also the one VoiceTicketRegistry tests
    // advance by hand to expire a ticket without a real wait; sharing it here would freeze these too.
    public long LastAudioAtMs { get; set; }
    public long LastActivityAtMs { get; set; } = Environment.TickCount64;

    public void Touch() => LastActivityAtMs = Environment.TickCount64;

    /// <summary>The next frame, opened with <see cref="Cipher"/> once it is set (<paramref name="maxPayload"/> is the
    /// cap on the plaintext; the sealing overhead is added here).</summary>
    public async Task<(byte Type, byte[] Payload)> ReadFrameAsync(int maxPayload, CancellationToken ct)
    {
        var cipher = _cipher;
        var frame = await VoiceFrame.ReadAsync(_stream, maxPayload + (cipher == null ? 0 : Ro3Emu.ClientHooks.VoiceCipher.Overhead), ct);
        if (cipher == null) return frame;
        byte[]? plain = cipher.Open(frame.Type, frame.Payload);
        if (plain == null) throw new VoiceProtocolException("frame failed authentication");
        return (frame.Type, plain);
    }

    // --- outgoing frames: encoding lives next to the queue it feeds, so the wire layout has one home ---

    public void SendAuthOk() => Enqueue(VoiceDown.AuthOk, []);

    public void SendJoinResult(byte code, string channel)
    {
        var chanBytes = Encoding.ASCII.GetBytes(channel.Length > 255 ? channel[..255] : channel);
        var payload = new byte[2 + chanBytes.Length];
        payload[0] = code;
        payload[1] = (byte)chanBytes.Length;
        chanBytes.CopyTo(payload, 2);
        Enqueue(VoiceDown.JoinResult, payload);
    }

    public void SendMemberChange(long roleId, bool joined)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, roleId);
        payload[8] = joined ? (byte)1 : (byte)0;
        Enqueue(VoiceDown.MemberChange, payload);
    }

    public void SendMicState(long roleId, bool on)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, roleId);
        payload[8] = on ? (byte)1 : (byte)0;
        Enqueue(VoiceDown.MicState, payload);
    }

    public void SendVad(long roleId, bool speaking)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, roleId);
        payload[8] = speaking ? (byte)1 : (byte)0;
        Enqueue(VoiceDown.Vad, payload);
    }

    public void SendPong() => Enqueue(VoiceDown.Pong, []);

    /// <summary>True once <see cref="Kick"/> ran: the connection is on its way out and its frames are ignored.</summary>
    public bool Kicked => _closeAfterDrain;

    /// <summary>Queues a Kicked frame as the connection's last: queued audio is dropped, nothing is queued after it,
    /// and the connection ends once the writer has flushed it.</summary>
    public void Kick(byte code)
    {
        lock (_sync)
        {
            if (_closed || _closeAfterDrain) return;
            _audio.Clear();
            _control.Enqueue((VoiceDown.Kicked, [code]));
            // Under the same lock as the frame: the writer that dequeues it also sees the flag.
            _closeAfterDrain = true;
        }
        _signal.Release();
    }

    /// <summary>Builds one Audio-down payload (speaker i64 | seq u16 | opus) for the caller to fan out to every
    /// listener; built once per received frame, sealed per listener by its writer (never changed after this).</summary>
    public static byte[] BuildAudioPayload(long speaker, ushort seq, ReadOnlySpan<byte> opus)
    {
        var payload = new byte[10 + opus.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, speaker);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), seq);
        opus.CopyTo(payload.AsSpan(10));
        return payload;
    }

    /// <summary>Queues an Audio-down payload; the oldest queued audio frame is dropped first.</summary>
    public void SendAudio(byte[] payload)
    {
        lock (_sync)
        {
            if (_closed || _closeAfterDrain) return;
            if (_audio.Count >= MaxQueuedAudio) _audio.Dequeue();
            _audio.Enqueue((VoiceDown.Audio, payload));
        }
        _signal.Release();
    }

    private void Enqueue(byte type, byte[] payload)
    {
        lock (_sync)
        {
            if (_closed || _closeAfterDrain) return;
            if (_control.Count >= MaxQueuedControl) { CloseLocked(); return; }
            _control.Enqueue((type, payload));
        }
        _signal.Release();
    }

    /// <summary>The single writer for this connection's socket; control frames drain ahead of audio.</summary>
    public async Task RunWriterAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token);
                (byte Type, byte[] Payload)? next;
                bool drained;
                lock (_sync)
                {
                    next = _control.Count > 0 ? _control.Dequeue() : _audio.Count > 0 ? _audio.Dequeue() : null;
                    drained = _control.Count == 0 && _audio.Count == 0;
                }
                if (next is var (type, payload))
                {
                    // Sealed here, by the only writer, so the counters go out in the order the frames do.
                    var cipher = _cipher;
                    await _stream.WriteAsync(VoiceFrame.Write(type, cipher == null ? payload : cipher.Seal(type, payload)), _cts.Token);
                }
                if (drained && _closeAfterDrain) { EndAfterKick(); return; }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
    }

    /// <summary>How long a kicked client has to close its side after its Kicked frame before the socket closes.</summary>
    public static readonly TimeSpan KickedCloseDelay = TimeSpan.FromSeconds(2);

    // FIN right behind the Kicked frame; the serve task reads on (and drops) until the client closes its side, and
    // the socket closes KickedCloseDelay later at the latest. Closing at once sends RST while the client's own
    // frames are still unread here, and a Windows client then loses the Kicked frame before it reads it.
    private void EndAfterKick()
    {
        try { _socket.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { Close(); return; }
        _ = CloseLaterAsync(_cts.Token);

        async Task CloseLaterAsync(CancellationToken closed)
        {
            try { await Task.Delay(KickedCloseDelay, closed); }
            catch (OperationCanceledException) { return; } // the client closed first, or the server stopped
            Close();
        }
    }

    public void Close()
    {
        lock (_sync) CloseLocked();
    }

    private void CloseLocked()
    {
        if (_closed) return;
        _closed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _socket.Close(); } catch { /* already gone */ }
    }

    public void Dispose()
    {
        Close();
        _stream.Dispose();
        _socket.Dispose();
        _cts.Dispose();
        _signal.Dispose();
        _cipher?.Dispose();
    }
}
