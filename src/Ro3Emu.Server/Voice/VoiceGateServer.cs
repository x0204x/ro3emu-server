using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// The voice relay's own TCP listener (VoiceFrame framing, not Phonest/PhonestChannel: the gate carries no
/// client identity of its own, only the 77015/77016 ticket). Registered when <see cref="VoiceOptions.Port"/> is
/// set (ServerHost.Create) and serving only when <see cref="ServerOptions.VoiceServed"/>: the stream is plaintext,
/// so off loopback it needs <see cref="VoiceOptions.AllowPlaintextOffLoopback"/>. Binds in <see cref="StartAsync"/>
/// and drains in <see cref="StopAsync"/>, like <see cref="Net.GateListener"/>.
/// </summary>
public sealed class VoiceGateServer(IOptions<ServerOptions> options, VoiceTicketRegistry tickets, VoiceRooms rooms,
    TeamService teams, SessionRegistry sessions, GameLoop loop, ILogger<VoiceGateServer> log)
    : BackgroundService
{
    private const long IpBanDurationMs = 60_000;
    private const int BanMapPurgeSize = 256;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    // Environment.TickCount64, not the injected TimeProvider: see the note on VoiceRateLimiter.
    private readonly ConcurrentDictionary<IPAddress, long> _bannedUntilMs = new();
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly ConcurrentDictionary<IPAddress, int> _failedAuth = new();
    private readonly ConcurrentDictionary<VoiceConnection, Task> _serves = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _connectionCount;
    private Socket? _listener;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (opts.Voice.Port <= 0) return base.StartAsync(cancellationToken);
        var address = opts.ListenAddress();
        if (!opts.VoiceServed())
        {
            log.LogWarning("VoiceGate not started: the voice stream is not encrypted and {Address} is not loopback "
                + "(set Voice:AllowPlaintextOffLoopback to serve it anyway)", address);
            return base.StartAsync(cancellationToken);
        }
        var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(address, opts.Voice.Port));
            listener.Listen(128);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        if (IPAddress.IsLoopback(address)) log.LogInformation("VoiceGate listening on {Address}:{Port}", address, opts.Voice.Port);
        else log.LogWarning("VoiceGate listening on {Address}:{Port} WITHOUT encryption (AllowPlaintextOffLoopback)", address, opts.Voice.Port);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_listener == null) return; // relay off
        using var listener = _listener;
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException e)
            {
                // A transient accept failure (handle exhaustion, reset) must not stop the host.
                log.LogWarning("VoiceGate accept failed: {Error}", e.SocketErrorCode);
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            socket.NoDelay = true;
            Admit(socket);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        await base.StopAsync(cancellationToken);
        foreach (var connection in _serves.Keys) connection.Close();
        var pending = Task.WhenAll(_serves.Values);
        if (await Task.WhenAny(pending, Task.Delay(DrainTimeout, cancellationToken)) != pending)
            log.LogWarning("VoiceGate drain timed out with {Count} connection(s) left", _serves.Count);
    }

    private void Admit(Socket socket)
    {
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        var voice = options.Value.Voice;
        bool overIp = _perIp.AddOrUpdate(remote, 1, (_, n) => n + 1) > voice.MaxConnectionsPerIp;
        bool overAll = Interlocked.Increment(ref _connectionCount) > voice.MaxConnections;
        if (IsBanned(remote) || overIp || overAll || _stopping.IsCancellationRequested)
        {
            Release(remote);
            socket.Dispose();
            return;
        }
        var connection = new VoiceConnection(socket)
        {
            RateLimiter = new VoiceRateLimiter(voice.FramesPerSecond, voice.Burst),
        };
        // Tracked before it starts, so a connection that ends at once still removes its own entry.
        var serve = new Task<Task>(() => RunAsync(connection, remote));
        _serves[connection] = serve.Unwrap();
        serve.Start(TaskScheduler.Default);
    }

    private async Task RunAsync(VoiceConnection connection, IPAddress remote)
    {
        var writer = Task.Run(connection.RunWriterAsync);
        try
        {
            await ServeAsync(connection);
        }
        catch (Exception e) when (e is VoiceProtocolException or IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e)
        {
            log.LogError(e, "VoiceGate: {Remote} failed", remote);
        }
        finally
        {
            Cleanup(connection);
            connection.Close();
            try { await writer; }
            catch (Exception e) { log.LogError(e, "VoiceGate: writer for {Remote} faulted", remote); }
            connection.Dispose();
            if (connection.Authed) _failedAuth.TryRemove(remote, out _);
            else if (!_stopping.IsCancellationRequested) CountFailedAuth(remote);
            Release(remote);
            _serves.TryRemove(connection, out _);
        }
    }

    private async Task ServeAsync(VoiceConnection connection)
    {
        var opts = options.Value.Voice;
        int maxPayload = opts.MaxPayload + 64; // header room on top of the opus budget
        using var idle = StartIdleWatchdog(connection, TimeSpan.FromSeconds(opts.IdleTimeoutSeconds));
        using var live = CancellationTokenSource.CreateLinkedTokenSource(connection.StopToken, _stopping.Token);
        (byte Type, byte[] Payload) frame;
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(live.Token))
        {
            first.CancelAfter(TimeSpan.FromSeconds(opts.FirstFrameTimeoutSeconds));
            try { frame = await connection.ReadFrameAsync(maxPayload, first.Token); }
            catch (OperationCanceledException) { return; } // no Auth within 5 s, or the server is stopping
        }
        connection.Touch();
        if (frame.Type != VoiceUp.Auth || !await HandleAuthAsync(connection, frame.Payload)) return;

        while (!live.IsCancellationRequested)
        {
            frame = await connection.ReadFrameAsync(maxPayload, live.Token);
            connection.Touch();
            switch (frame.Type)
            {
                case VoiceUp.Join: await HandleJoinAsync(connection, frame.Payload); break;
                case VoiceUp.Leave: HandleLeave(connection); break;
                case VoiceUp.Mic: HandleMic(connection, frame.Payload); break;
                case VoiceUp.Speaker: HandleSpeaker(connection, frame.Payload); break;
                case VoiceUp.Audio: HandleAudio(connection, frame.Payload); break;
                case VoiceUp.Ping: connection.SendPong(); break;
                // An unrecognized or repeated Auth is ignored rather than closed: harmless, and a reconnecting
                // client should not be punished for racing a frame it already sent.
                default: break;
            }
        }
    }

    private async Task<bool> HandleAuthAsync(VoiceConnection connection, byte[] payload)
    {
        if (payload.Length != 25 || payload[0] != 1) return false;
        long roleId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1, 8));
        byte[] token = payload[9..25];
        string? account = tickets.Consume(roleId, token);
        if (account == null) return false;
        bool alive = await loop.InvokeAsync(() => Task.FromResult(sessions.All().Any(s =>
            string.Equals(s.Account, account, StringComparison.Ordinal) && s.Role?.Id == roleId)));
        if (!alive) return false;
        var previous = rooms.Authenticate(account, roleId, connection);
        if (previous != null)
        {
            // Out of its room at once, not only after its Kicked frame drains.
            Cleanup(previous);
            previous.Kick(VoiceKickCode.ReplacedByNewAuth);
        }
        connection.SendAuthOk();
        return true;
    }

    private async Task HandleJoinAsync(VoiceConnection connection, byte[] payload)
    {
        string channel = ReadLengthPrefixedAscii(payload, out bool wellFormed);
        if (!wellFormed) return;
        if (!long.TryParse(channel, out long requested))
        {
            connection.SendJoinResult(VoiceJoinCode.UnsupportedRoom, channel);
            return;
        }
        byte code = await loop.InvokeAsync(() =>
        {
            bool alive = sessions.All().Any(s => s.Role?.Id == connection.RoleId);
            var (teamId, _) = teams.TeamOf(connection.RoleId);
            byte result = alive && teamId != 0 && teamId == requested ? VoiceJoinCode.Ok : VoiceJoinCode.NotMember;
            return Task.FromResult(result);
        });
        if (code != VoiceJoinCode.Ok)
        {
            connection.SendJoinResult(code, channel);
            return;
        }
        var outcome = rooms.Join(connection, requested);
        connection.SendJoinResult(outcome.Code, channel);
        if (outcome.Code != VoiceJoinCode.Ok) return;
        foreach (var other in outcome.PreviousRoomOthers) other.SendMemberChange(connection.RoleId, false);
        foreach (var existing in outcome.ExistingMembers)
        {
            connection.SendMemberChange(existing.RoleId, true);
            connection.SendMicState(existing.RoleId, existing.MicOn);
            existing.SendMemberChange(connection.RoleId, true);
        }
    }

    private void HandleLeave(VoiceConnection connection)
    {
        var others = rooms.Leave(connection);
        foreach (var other in others) other.SendMemberChange(connection.RoleId, false);
    }

    private void HandleMic(VoiceConnection connection, byte[] payload)
    {
        if (payload.Length < 1) return;
        bool on = payload[0] != 0;
        VoiceRooms.BroadcastMic(connection, rooms.SetMic(connection, on), on);
    }

    private void HandleSpeaker(VoiceConnection connection, byte[] payload)
    {
        if (payload.Length < 1) return;
        rooms.SetSpeaker(connection, payload[0] != 0);
    }

    private void HandleAudio(VoiceConnection connection, byte[] payload)
    {
        var limiter = connection.RateLimiter!;
        if (limiter.RecordAndCheckFlood())
        {
            connection.Kick(VoiceKickCode.Flood);
            Ban(connection.RemoteAddress);
            log.LogWarning("VoiceGate: {Remote} disconnected for sustained flood", connection.RemoteAddress);
            return;
        }
        if (!limiter.TryTake()) return; // over its instantaneous budget: drop, do not disconnect
        if (!connection.MicOn || connection.RoomId == 0 || payload.Length < 2) return;
        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        var opus = payload.AsSpan(2);
        if (rooms.MarkSpeaking(connection))
            foreach (var member in rooms.MembersOf(connection.RoomId)) member.SendVad(connection.RoleId, true);
        var listeners = rooms.ListenersFor(connection);
        if (listeners.Length == 0) return;
        byte[] frame = VoiceConnection.BuildAudioFrame(connection.RoleId, seq, opus);
        foreach (var listener in listeners) listener.SendAudioFrame(frame);
    }

    private void Cleanup(VoiceConnection connection)
    {
        var others = rooms.Disconnect(connection);
        foreach (var other in others) other.SendMemberChange(connection.RoleId, false);
    }

    private void Release(IPAddress remote)
    {
        Interlocked.Decrement(ref _connectionCount);
        if (_perIp.AddOrUpdate(remote, 0, (_, n) => n - 1) <= 0) _perIp.TryRemove(new KeyValuePair<IPAddress, int>(remote, 0));
    }

    private void CountFailedAuth(IPAddress remote)
    {
        if (_failedAuth.AddOrUpdate(remote, 1, (_, n) => n + 1) < options.Value.Voice.MaxFailedAuthPerIp) return;
        _failedAuth.TryRemove(remote, out _);
        Ban(remote);
        log.LogWarning("VoiceGate: {Remote} refused for a minute after repeated connections without a valid Auth", remote);
    }

    private void Ban(IPAddress address)
    {
        long now = Environment.TickCount64;
        if (_bannedUntilMs.Count >= BanMapPurgeSize)
            foreach (var entry in _bannedUntilMs)
                if (entry.Value <= now) _bannedUntilMs.TryRemove(entry);
        _bannedUntilMs[address] = now + IpBanDurationMs;
    }

    private bool IsBanned(IPAddress address)
    {
        if (!_bannedUntilMs.TryGetValue(address, out long until)) return false;
        if (Environment.TickCount64 < until) return true;
        _bannedUntilMs.TryRemove(address, out _);
        return false;
    }

    private static IDisposable StartIdleWatchdog(VoiceConnection connection, TimeSpan idleTimeout)
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(100, idleTimeout.TotalMilliseconds / 5));
        long idleTimeoutMs = (long)idleTimeout.TotalMilliseconds;
        return new Timer(_ =>
        {
            if (Environment.TickCount64 - connection.LastActivityAtMs >= idleTimeoutMs) connection.Close();
        }, null, period, period);
    }

    private static string ReadLengthPrefixedAscii(byte[] payload, out bool wellFormed)
    {
        if (payload.Length < 1 || payload.Length < 1 + payload[0]) { wellFormed = false; return ""; }
        wellFormed = true;
        return System.Text.Encoding.ASCII.GetString(payload, 1, payload[0]);
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }
}
