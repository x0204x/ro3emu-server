using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Romsg;
using Ro3Emu.ClientHooks;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Bot;

/// <summary>The 77016 voice ticket: u16 port | u8 hostLen | host ASCII | i64 roleId | 16-byte token | u32 ttlSeconds |
/// 32-byte stream secret. Port 0 means the relay is off on that server.</summary>
public sealed record BotVoiceTicket(ushort Port, string Host, long RoleId, byte[] Token, uint TtlSeconds, byte[] Secret)
{
    public static BotVoiceTicket Parse(byte[] body)
    {
        if (body.Length < 3) throw new InvalidDataException("voice ticket too short");
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2));
        int hostLen = body[2];
        int at = 3 + hostLen;
        if (body.Length < at + 8 + 16 + 4 + VoiceCipher.SecretSize) throw new InvalidDataException("voice ticket too short");
        string host = Encoding.ASCII.GetString(body, 3, hostLen);
        long roleId = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(at, 8));
        byte[] token = body[(at + 8)..(at + 24)];
        uint ttl = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 24, 4));
        byte[] secret = body[(at + 28)..(at + 28 + VoiceCipher.SecretSize)];
        return new BotVoiceTicket(port, host, roleId, token, ttl, secret);
    }

    /// <summary>Asks the game gate for a ticket (77015) and waits for the 77016 reply.</summary>
    public static async Task<BotVoiceTicket> RequestAsync(BotClient bot, TimeSpan timeout)
    {
        using var expected = bot.Inbox.Expect(e => e.IsGame(VoiceTicketService.Ticket));
        await bot.SendGameBytesAsync(VoiceTicketService.TicketReq, []);
        return Parse((await expected.WaitAsync(timeout)).Body);
    }
}

/// <summary>
/// A bot's connection to the voice gate (<see cref="VoiceFrame"/> framing over TCP, sealed after Auth with the ticket secret). One reader (the caller of
/// <see cref="ReadFrameAsync"/>); writes from several tasks (audio, ping) are serialized here.
/// </summary>
public sealed class BotVoiceClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private volatile VoiceCipher? _cipher; // set by AuthAsync: every later frame both ways is sealed with it

    /// <summary><paramref name="host"/> as the ticket gives it; a bracketed IPv6 literal is unwrapped.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host.Trim('[', ']'), port, ct);
        _stream = _tcp.GetStream();
    }

    /// <summary>The next frame, opened with the ticket's secret after Auth; one that fails to open throws.</summary>
    public async Task<(byte Type, byte[] Payload)> ReadFrameAsync(CancellationToken ct)
    {
        var frame = await VoiceFrame.ReadAsync(_stream!, ushort.MaxValue, ct);
        var cipher = _cipher;
        if (cipher == null) return frame;
        return (frame.Type, cipher.Open(frame.Type, frame.Payload) ?? throw new VoiceProtocolException("frame failed authentication"));
    }

    /// <summary>Auth (sent as is), then everything is sealed with <paramref name="secret"/>.</summary>
    public async Task AuthAsync(long roleId, byte[] token, byte[] secret, CancellationToken ct)
    {
        var payload = new byte[VoiceUp.AuthSize];
        payload[0] = VoiceUp.AuthVersion;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1, 8), roleId);
        token.AsSpan(0, 16).CopyTo(payload.AsSpan(9, 16));
        VoiceCipher.AuthProof(secret, roleId, token).CopyTo(payload, 25);
        var cipher = VoiceCipher.ForClient(secret);
        await _writeLock.WaitAsync(ct);
        try
        {
            _cipher = cipher; // before the write: the reply is sealed and may be read the moment Auth is out
            await _stream!.WriteAsync(VoiceFrame.Write(VoiceUp.Auth, payload), ct);
        }
        finally { _writeLock.Release(); }
    }

    public Task JoinAsync(string channel, CancellationToken ct)
    {
        var chan = Encoding.ASCII.GetBytes(channel);
        var payload = new byte[1 + chan.Length];
        payload[0] = (byte)chan.Length;
        chan.CopyTo(payload, 1);
        return SendAsync(VoiceUp.Join, payload, ct);
    }

    public Task MicAsync(bool on, CancellationToken ct) => SendAsync(VoiceUp.Mic, [on ? (byte)1 : (byte)0], ct);

    public Task PingAsync(CancellationToken ct) => SendAsync(VoiceUp.Ping, [], ct);

    public Task SendAudioAsync(ushort seq, ReadOnlySpan<byte> opus, CancellationToken ct)
    {
        var payload = new byte[2 + opus.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), seq);
        opus.CopyTo(payload.AsSpan(2));
        return SendAsync(VoiceUp.Audio, payload, ct);
    }

    private async Task SendAsync(byte type, byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Sealed under the write lock, so the counters reach the wire in order.
            var cipher = _cipher;
            await _stream!.WriteAsync(VoiceFrame.Write(type, cipher == null ? payload : cipher.Seal(type, payload)), ct);
        }
        finally { _writeLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _writeLock.Dispose();
        _cipher?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public enum BotVoiceEchoEndKind { Kicked, Closed, Cancelled, JoinRefused }

/// <summary>Why <see cref="BotVoiceEcho.RunAsync"/> returned; <see cref="KickCode"/> is the Kicked or JoinResult
/// code when there was one.</summary>
public sealed record BotVoiceEchoEnd(BotVoiceEchoEndKind Kind, byte KickCode = 0);

/// <summary>
/// The voice echo bot for manual in-game tests: it joins its party's voice room with its mic on and sends every
/// other member's audio back as its own after a fixed delay, so one player can hear the relay round trip.
/// </summary>
public static class BotVoiceEcho
{
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InvitePoll = TimeSpan.FromMinutes(1);

    /// <summary>Waits (until <paramref name="ct"/>) for a party invite, accepts it and returns the team id.</summary>
    public static async Task<long> AcceptPartyInviteAsync(BotClient bot, Action<string> log, CancellationToken ct)
    {
        log("waiting for a party invite");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            BotEvent e;
            try { e = await bot.Inbox.WaitAsync(x => x.IsGame(TeamService.TeamNotifyInvitee), InvitePoll).WaitAsync(ct); }
            catch (TimeoutException) { continue; }
            var invite = NotifyInvitee.Parser.ParseFrom(e.Body);
            long teamId = invite.TeamTotalSimpleInfo?.Info?.TeamId ?? 0;
            log($"invite from {invite.InviteGuid} to team {teamId}");
            using var reply = bot.Inbox.Expect(x => x.IsGame(TeamService.TeamInviteeResp));
            await bot.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp { TeamId = teamId, IsAgree = true, InviteGuid = invite.InviteGuid });
            var resp = InviteeRespResp.Parser.ParseFrom((await reply.WaitAsync(ReplyTimeout, ct)).Body);
            if (resp.Result != 0)
            {
                log($"joining team {teamId} failed (result {resp.Result}); waiting for another invite");
                continue;
            }
            log($"joined team {resp.TeamId}");
            return resp.TeamId;
        }
    }

    /// <summary>The party's voice room, as <see cref="RunAsync(BotClient, long, string, TimeSpan, Action{string}, CancellationToken)"/>.</summary>
    public static Task<BotVoiceEchoEnd> RunAsync(BotClient bot, long roleId, long teamId, TimeSpan delay,
        Action<string> log, CancellationToken ct) =>
        RunAsync(bot, roleId, teamId.ToString(System.Globalization.CultureInfo.InvariantCulture), delay, log, ct);

    /// <summary>
    /// Ticket, connect, Auth, Join <paramref name="room"/> (a team id, or a guild voice channel "2_&lt;guildId&gt;_&lt;n&gt;"
    /// the bot sits in), mic on, then echo until Kicked (for example removed from the room after leaving the party or
    /// the channel), the voice gate closes the connection, or <paramref name="ct"/>. Audio bytes are never logged.
    /// </summary>
    public static async Task<BotVoiceEchoEnd> RunAsync(BotClient bot, long roleId, string room, TimeSpan delay,
        Action<string> log, CancellationToken ct)
    {
        var ticket = await BotVoiceTicket.RequestAsync(bot, ReplyTimeout);
        if (ticket.Port == 0) throw new InvalidOperationException("the voice relay is off on this server (ticket port 0)");
        await using var voice = new BotVoiceClient();
        await voice.ConnectAsync(ticket.Host, ticket.Port, ct);
        log($"voice gate {ticket.Host}:{ticket.Port}");
        using var live = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await voice.AuthAsync(ticket.RoleId, ticket.Token, ticket.Secret, live.Token);
            var setup = await SetupAsync(voice, room, log, live.Token);
            if (setup != null) return setup;
            var echoes = Channel.CreateUnbounded<(long DueMs, byte[] Opus)>(new UnboundedChannelOptions { SingleReader = true });
            var stats = new EchoStats();
            var writer = EchoWriterAsync(voice, echoes.Reader, stats, live.Token);
            var pinger = PingLoopAsync(voice, live.Token);
            var reporter = StatsLoopAsync(stats, log, live.Token);
            try { return await ReadLoopAsync(voice, roleId, (long)delay.TotalMilliseconds, echoes.Writer, stats, log, live.Token); }
            finally
            {
                live.Cancel();
                echoes.Writer.TryComplete();
                await Task.WhenAll(Quiet(writer), Quiet(pinger), Quiet(reporter));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(BotVoiceEchoEndKind.Cancelled); }
        catch (Exception e) when (e is EndOfStreamException or IOException or SocketException or VoiceProtocolException)
        {
            log($"voice connection closed ({e.GetType().Name})");
            return new(BotVoiceEchoEndKind.Closed);
        }
    }

    /// <summary>Waits for AuthOk, joins the room and turns the mic on; returns an end result if that fails.</summary>
    private static async Task<BotVoiceEchoEnd?> SetupAsync(BotVoiceClient voice, string room, Action<string> log, CancellationToken ct)
    {
        var (type, payload) = await voice.ReadFrameAsync(ct);
        if (type == VoiceDown.Kicked) return new(BotVoiceEchoEndKind.Kicked, payload.Length > 0 ? payload[0] : (byte)0);
        if (type != VoiceDown.AuthOk) return new(BotVoiceEchoEndKind.Closed);
        log("voice auth ok");
        await voice.JoinAsync(room, ct);
        while (true)
        {
            (type, payload) = await voice.ReadFrameAsync(ct);
            if (type == VoiceDown.Kicked) return new(BotVoiceEchoEndKind.Kicked, payload.Length > 0 ? payload[0] : (byte)0);
            if (type != VoiceDown.JoinResult) continue;
            byte code = payload.Length > 0 ? payload[0] : byte.MaxValue;
            if (code != VoiceJoinCode.Ok)
            {
                log($"voice join of room {room} refused (code {code})");
                return new(BotVoiceEchoEndKind.JoinRefused, code);
            }
            break;
        }
        await voice.MicAsync(true, ct);
        log($"in voice room {room}, mic on, echoing");
        return null;
    }

    private static async Task<BotVoiceEchoEnd> ReadLoopAsync(BotVoiceClient voice, long roleId, long delayMs,
        ChannelWriter<(long DueMs, byte[] Opus)> echoes, EchoStats stats, Action<string> log, CancellationToken ct)
    {
        while (true)
        {
            var (type, payload) = await voice.ReadFrameAsync(ct);
            switch (type)
            {
                case VoiceDown.Audio when payload.Length >= 10:
                    long speaker = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
                    if (speaker == roleId) break;
                    stats.Received(Environment.TickCount64);
                    echoes.TryWrite((Environment.TickCount64 + delayMs, payload[10..]));
                    break;
                case VoiceDown.MemberChange when payload.Length >= 9:
                    log($"member {BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8))} {(payload[8] != 0 ? "joined" : "left")} the voice room");
                    break;
                case VoiceDown.Kicked:
                    byte code = payload.Length > 0 ? payload[0] : (byte)0;
                    log($"kicked by the voice gate (code {code})");
                    return new(BotVoiceEchoEndKind.Kicked, code);
            }
        }
    }

    /// <summary>Sends the queued echoes in arrival order, each once its due time is reached, under the bot's own
    /// sequence numbers.</summary>
    private static async Task EchoWriterAsync(BotVoiceClient voice, ChannelReader<(long DueMs, byte[] Opus)> echoes, EchoStats stats, CancellationToken ct)
    {
        ushort seq = 0;
        await foreach (var (dueMs, opus) in echoes.ReadAllAsync(ct))
        {
            long wait = dueMs - Environment.TickCount64;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            await voice.SendAudioAsync(seq++, opus, ct);
            stats.Sent(Environment.TickCount64 - dueMs);
        }
    }

    /// <summary>One line every 10 s while audio flows: frames heard and echoed, the longest gap between heard frames
    /// (a gap means the sender's audio stalled before it reached the bot), and how late echoes went out.</summary>
    private static async Task StatsLoopAsync(EchoStats stats, Action<string> log, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(StatsEvery);
        while (await timer.WaitForNextTickAsync(ct))
            if (stats.Take() is { } line) log(line);
    }

    private sealed class EchoStats
    {
        private readonly object _sync = new();
        private int _received, _sent;
        private long _lastAt, _maxGap, _maxLate;

        public void Received(long now)
        {
            lock (_sync)
            {
                if (_lastAt != 0) _maxGap = Math.Max(_maxGap, now - _lastAt);
                _lastAt = now;
                _received++;
            }
        }

        public void Sent(long lateMs)
        {
            lock (_sync) { _sent++; _maxLate = Math.Max(_maxLate, lateMs); }
        }

        public string? Take()
        {
            lock (_sync)
            {
                if (_received == 0 && _sent == 0) return null;
                var line = $"stats 10s: heard {_received} frames (expect ~500), echoed {_sent}, longest gap {_maxGap} ms, echo late up to {_maxLate} ms";
                _received = _sent = 0;
                _maxGap = _maxLate = 0;
                return line;
            }
        }
    }

    private static async Task PingLoopAsync(BotVoiceClient voice, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PingEvery);
        while (await timer.WaitForNextTickAsync(ct)) await voice.PingAsync(ct);
    }

    private static async Task Quiet(Task task)
    {
        try { await task; }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
    }
}
