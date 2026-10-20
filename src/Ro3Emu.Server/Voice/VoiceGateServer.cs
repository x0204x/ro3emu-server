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
/// set (ServerHost.Create) and serving only when <see cref="ServerOptions.VoiceServed"/>. Every frame after Auth is
/// sealed with the ticket's secret (<see cref="VoiceConnection.Cipher"/>), so it serves off loopback as well. Binds in <see cref="StartAsync"/>
/// and drains in <see cref="StopAsync"/>, like <see cref="Net.GateListener"/>.
/// </summary>
public sealed class VoiceGateServer(IOptions<ServerOptions> options, VoiceTicketRegistry tickets, VoiceRooms rooms,
    SessionRegistry sessions, GameLoop loop, GmCommands gm, ILogger<VoiceGateServer> log)
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
        // Test aid: the connection ends as a network drop would (no Kicked frame); the client reconnects on its own.
        gm.Register("voicedrop", "(voice relay test aid: drops your voice connection; the client reconnects)", async (s, _) =>
        {
            var connection = s.Account == null ? null : rooms.ConnectionOf(s.Account);
            connection?.Close();
            await GmCommands.ReplyAsync(s, connection == null ? "No voice connection." : "Voice connection dropped.");
        });
        log.LogInformation("VoiceGate listening on {Address}:{Port}", address, opts.Voice.Port);
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
            ControlLimiter = new VoiceRateLimiter(voice.ControlPerSecond, voice.ControlBurst),
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
            // Kicked (flood, replaced, removed): read on until the client closes after its Kicked frame (at most
            // VoiceConnection.KickedCloseDelay after that last frame is written), but act on nothing, so a kicked
            // connection cannot rejoin a room or flood the logs meanwhile. Not touched either: a client that stops
            // reading, so its Kicked frame never goes out, still ends at the idle timeout.
            if (connection.Kicked) continue;
            connection.Touch();
            // Everything but Audio (its own budget, in HandleAudio) shares one control budget: a Mic reaches the
            // whole room and a Join costs a game-loop hop, so neither may be sent at network speed.
            if (frame.Type != VoiceUp.Audio && !TakeControl(connection)) continue;
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
        if (payload.Length != VoiceUp.AuthSize || payload[0] != VoiceUp.AuthVersion) return false;
        long roleId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1, 8));
        byte[] token = payload[9..25];
        var claim = tickets.Consume(roleId, token, payload[25..]);
        if (claim == null) return false;
        string account = claim.Account;
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
        connection.Cipher = Ro3Emu.ClientHooks.VoiceCipher.ForServer(claim.Secret); // AuthOk is the first sealed frame
        connection.SendAuthOk();
        return true;
    }

    private async Task HandleJoinAsync(VoiceConnection connection, byte[] payload)
    {
        string channel = ReadLengthPrefixedAscii(payload, out bool wellFormed);
        if (!wellFormed) return;
        if (!VoiceRoomKey.TryParse(channel, out var key))
        {
            connection.SendJoinResult(VoiceJoinCode.UnsupportedRoom, channel);
            return;
        }
        long requested = key.Value;
        // A repeated Join of the room it is in: confirmed again, but no loop hop and nothing to the others.
        if (rooms.IsIn(connection, requested))
        {
            connection.SendJoinResult(VoiceJoinCode.Ok, channel);
            return;
        }
        var (code, forbidden) = await loop.InvokeAsync(() =>
        {
            bool alive = sessions.All().Any(s => s.Role?.Id == connection.RoleId);
            byte result = alive && rooms.Belongs(connection.RoleId, key) ? VoiceJoinCode.Ok : VoiceJoinCode.NotMember;
            return Task.FromResult((result, rooms.MicHeldOff(connection.RoleId, requested)));
        });
        if (code != VoiceJoinCode.Ok)
        {
            connection.SendJoinResult(code, channel);
            return;
        }
        var outcome = rooms.Join(connection, requested, forbidden);
        connection.SendJoinResult(outcome.Code, channel);
        if (outcome.Code != VoiceJoinCode.Ok || outcome.AlreadyIn) return;
        foreach (var other in outcome.PreviousRoomOthers) other.SendMemberChange(connection.RoleId, false);
        foreach (var existing in outcome.ExistingMembers)
        {
            connection.SendMemberChange(existing.RoleId, true);
            connection.SendMicState(existing.RoleId, existing.MicOn);
            existing.SendMemberChange(connection.RoleId, true);
            if (outcome.MicOn) existing.SendMicState(connection.RoleId, true);
        }
        // A mic the player turned on before joining (or kept from the room it switched from) is live here too.
        if (outcome.MicOn) connection.SendMicState(connection.RoleId, true);
    }

    private void HandleLeave(VoiceConnection connection)
    {
        var others = rooms.Leave(connection);
        foreach (var other in others) other.SendMemberChange(connection.RoleId, false);
    }

    private void HandleMic(VoiceConnection connection, byte[] payload)
    {
        if (payload.Length < 1) return;
        // Unchanged: nothing sent. Refused (the leader's mute): MicState off to the sender alone.
        VoiceRooms.BroadcastMic(connection, rooms.SetMic(connection, payload[0] != 0));
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
            KickForFlood(connection, "audio");
            return;
        }
        if (!limiter.TryTake()) return; // over its instantaneous budget: drop, do not disconnect
        // ChatForbidden as well as MicOn: the leader's mute holds from the tick that saw it, whatever Mic says.
        if (!connection.MicOn || connection.ChatForbidden || connection.RoomId == 0 || payload.Length < 2) return;
        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        var opus = payload.AsSpan(2);
        if (rooms.MarkSpeaking(connection))
            foreach (var member in rooms.MembersOf(connection.RoomId)) member.SendVad(connection.RoleId, true);
        var listeners = rooms.ListenersFor(connection);
        if (listeners.Length == 0) return;
        byte[] audio = VoiceConnection.BuildAudioPayload(connection.RoleId, seq, opus);
        foreach (var listener in listeners) listener.SendAudio(audio);
    }

    /// <summary>The control budget: false drops the frame; a sustained flood also disconnects and bans, as for Audio.</summary>
    private bool TakeControl(VoiceConnection connection)
    {
        var limiter = connection.ControlLimiter!;
        if (limiter.RecordAndCheckFlood())
        {
            KickForFlood(connection, "control");
            return false;
        }
        return limiter.TryTake();
    }

    private void KickForFlood(VoiceConnection connection, string what)
    {
        // Out of its room at once (no more frames reach the others), then Kicked; the serve task's own cleanup
        // finds nothing left to remove.
        Cleanup(connection);
        connection.Kick(VoiceKickCode.Flood);
        Ban(connection.RemoteAddress);
        log.LogWarning("VoiceGate: {Remote} disconnected for a sustained {What} flood", connection.RemoteAddress, what);
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
