using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// One voice-gate TCP connection: its socket, outbound queues and the state the relay (<see cref="VoiceRooms"/>)
/// tracks for it. Reads happen on the connection's own serve task; writes go through <see cref="RunWriterAsync"/>
/// so only one task ever touches the socket's send side (control and audio frames both end up here, queued).
/// </summary>
public sealed class VoiceConnection : IDisposable
{
    /// <summary>Audio frames queued for one slow listener before the oldest is dropped.</summary>
    public const int MaxQueuedAudio = 64;

    /// <summary>Control frames queued before the connection is considered stuck and closed.</summary>
    public const int MaxQueuedControl = 256;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly Queue<byte[]> _control = new();
    private readonly Queue<byte[]> _audio = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _closed;
    private volatile bool _closeAfterDrain;

    public VoiceConnection(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        RemoteAddress = ((IPEndPoint)socket.RemoteEndPoint!).Address;
        LastActivityAtMs = Environment.TickCount64;
    }

    public IPAddress RemoteAddress { get; }
    public CancellationToken StopToken => _cts.Token;

    /// <summary>Set once the Auth frame is accepted; everything but Auth is refused before that.</summary>
    public bool Authed { get; set; }
    public string? Account { get; set; }
    public long RoleId { get; set; }
    public VoiceRateLimiter? RateLimiter { get; set; }

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
    public long LastActivityAtMs { get; set; }

    public void Touch() => LastActivityAtMs = Environment.TickCount64;

    public Task<(byte Type, byte[] Payload)> ReadFrameAsync(int maxPayload, CancellationToken ct) =>
        VoiceFrame.ReadAsync(_stream, maxPayload, ct);

    // --- outgoing frames: encoding lives next to the queue it feeds, so the wire layout has one home ---

    public void SendAuthOk() => Enqueue(VoiceFrame.Write(VoiceDown.AuthOk, []));

    public void SendJoinResult(byte code, string channel)
    {
        var chanBytes = Encoding.ASCII.GetBytes(channel.Length > 255 ? channel[..255] : channel);
        var payload = new byte[2 + chanBytes.Length];
        payload[0] = code;
        payload[1] = (byte)chanBytes.Length;
        chanBytes.CopyTo(payload, 2);
        Enqueue(VoiceFrame.Write(VoiceDown.JoinResult, payload));
    }

    public void SendMemberChange(long roleId, bool joined)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, roleId);
        payload[8] = joined ? (byte)1 : (byte)0;
        Enqueue(VoiceFrame.Write(VoiceDown.MemberChange, payload));
    }

    public void SendMicState(long roleId, bool on)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, roleId);
        payload[8] = on ? (byte)1 : (byte)0;
        Enqueue(VoiceFrame.Write(VoiceDown.MicState, payload));
    }

    public void SendVad(long roleId, bool speaking)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, roleId);
        payload[8] = speaking ? (byte)1 : (byte)0;
        Enqueue(VoiceFrame.Write(VoiceDown.Vad, payload));
    }

    public void SendPong() => Enqueue(VoiceFrame.Write(VoiceDown.Pong, []));

    /// <summary>True once <see cref="Kick"/> ran: the connection is on its way out and its frames are ignored.</summary>
    public bool Kicked => _closeAfterDrain;

    /// <summary>Queues a Kicked frame, then closes the connection once the writer has flushed it.</summary>
    public void Kick(byte code)
    {
        // Flag first: the writer may dequeue the Kicked frame the moment it is queued and must already see it.
        _closeAfterDrain = true;
        Enqueue(VoiceFrame.Write(VoiceDown.Kicked, [code]));
    }

    /// <summary>Builds one Audio-down frame (speaker i64 | seq u16 | opus) for the caller to fan out to every
    /// listener; built once per received frame, not once per listener.</summary>
    public static byte[] BuildAudioFrame(long speaker, ushort seq, ReadOnlySpan<byte> opus)
    {
        var payload = new byte[10 + opus.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, speaker);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), seq);
        opus.CopyTo(payload.AsSpan(10));
        return VoiceFrame.Write(VoiceDown.Audio, payload);
    }

    /// <summary>Queues an already-built Audio-down frame; the oldest queued audio frame is dropped first.</summary>
    public void SendAudioFrame(byte[] frame)
    {
        lock (_sync)
        {
            if (_closed) return;
            if (_audio.Count >= MaxQueuedAudio) _audio.Dequeue();
            _audio.Enqueue(frame);
        }
        _signal.Release();
    }

    private void Enqueue(byte[] frame)
    {
        lock (_sync)
        {
            if (_closed) return;
            if (_control.Count >= MaxQueuedControl) { CloseLocked(); return; }
            _control.Enqueue(frame);
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
                byte[]? frame;
                bool drained;
                lock (_sync)
                {
                    frame = _control.Count > 0 ? _control.Dequeue() : _audio.Count > 0 ? _audio.Dequeue() : null;
                    drained = _control.Count == 0 && _audio.Count == 0;
                }
                if (frame != null) await _stream.WriteAsync(frame, _cts.Token);
                if (drained && _closeAfterDrain) { Close(); return; }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
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
    }
}
