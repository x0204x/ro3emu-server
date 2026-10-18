using System.Diagnostics;
using System.Security.Cryptography;
using MessagePack;
using Microsoft.Extensions.Options;
using Phonest.Proto;
using Romsg;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Game;

/// <summary>One avatar's reconnect credential. Loop-owned.</summary>
public sealed class ReconnectTicket
{
    internal ReconnectTicket(string entityId, GameSession session)
    {
        EntityId = entityId;
        Session = session;
        AccountGeneration = session.AccountGeneration;
        Attachment = session.Attachment;
    }

    public string EntityId { get; }
    public GameSession Session { get; }
    /// <summary>128-bit secret, sent as msgpack(str hex) inside ReconnectInfo.Auth; rotated on every reconnect.</summary>
    internal byte[] Auth { get; set; } = RandomNumberGenerator.GetBytes(16);
    internal long AccountGeneration { get; }
    internal Ownership.AttachmentLease? Attachment { get; }
    /// <summary>Set while the socket is gone and the role waits for the client.</summary>
    public DateTimeOffset? GraceUntil { get; internal set; }
    /// <summary>The client answered OnGetAuth with OnClientReadyInternal(true): it is a Phonest client that holds
    /// this ReconnectInfo and can reconnect.</summary>
    public bool ClientReady { get; internal set; }
    /// <summary>Reconnected; the resync runs on OnClientReadyInternal(reconnect: true) or after a short fallback.</summary>
    internal bool ResyncPending { get; set; }
    internal long LostAt { get; set; }
}

/// <summary>
/// The Phonest silent reconnect of the game gate (phase 1): an in-game client whose socket
/// drops comes back with ConnectServerReq{ReConnection, entityInfo = Mailbox, auth} on a new connection. The role stays
/// in the world for <see cref="ServerOptions.ReconnectGraceSeconds"/>; a valid reconnect swaps the transport of the
/// existing <see cref="GameSession"/> and replays a presentation-only resync (1011, ENTITYSELF, AOI). Nothing of the
/// role state is reloaded or saved. All state is loop-owned.
/// </summary>
public sealed class ReconnectService
{
    /// <summary>Phonest DisconnectClientReason values the gate sends.</summary>
    public const int ReasonBeReconnected = 10, ReasonLogicError = 16;
    /// <summary>The avatar entity's channel (GameMessageHandler's ClientAvatar).</summary>
    public const long AvatarChannel = 2;
    private const string GraceKeyPrefix = "reconnect-grace:", ReadyKeyPrefix = "reconnect-ready:";
    /// <summary>(inferred) Refusals allowed per remote address and per entity id inside the window (spec §4.4, §4.7).</summary>
    internal const int RefusalLimit = 5;
    internal static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(10);
    /// <summary>(inferred) The resync starts on OnClientReadyInternal; without it (a failed micro bind) after this.</summary>
    internal static readonly TimeSpan ReadyFallback = TimeSpan.FromSeconds(3);
    /// <summary>(inferred) A refused reconnect connection is closed after this, the client opens its own retry.</summary>
    internal static readonly TimeSpan RefusedCloseDelay = TimeSpan.FromSeconds(1);
    private static readonly MessagePackSerializerOptions Untrusted = Micro.MicroServiceRouter.Untrusted;

    private readonly ServerOptions _options;
    private readonly GameLoop _loop;
    private readonly Deadlines _deadlines;
    private readonly SessionRegistry _sessions;
    private readonly Ownership.RoleHandover _handover;
    private readonly SceneService _scene;
    private readonly GateAuthRegistry _gateAuths;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReconnectService> _log;
    private readonly Dictionary<string, ReconnectTicket> _byEntity = new(StringComparer.Ordinal);
    private readonly Dictionary<GameSession, ReconnectTicket> _bySession = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _refusals = new(StringComparer.Ordinal);
    private bool _closed;

    public ReconnectService(IOptions<ServerOptions> options, GameLoop loop, Deadlines deadlines, SessionRegistry sessions,
        Ownership.RoleHandover handover, SceneService scene, GateAuthRegistry gateAuths, GmCommands gm, TimeProvider clock,
        ILogger<ReconnectService> log)
    {
        _options = options.Value;
        _loop = loop;
        _deadlines = deadlines;
        _sessions = sessions;
        _handover = handover;
        _scene = scene;
        _gateAuths = gateAuths;
        _clock = clock;
        _log = log;
        handover.Beginning += Revoke;
        gm.Register("netdrop", "[fin|rst] (test aid: the server drops this game connection; the client reconnects)", NetDropAsync);
    }

    public long Reconnects { get; private set; }
    public long Refusals { get; private set; }
    public long GracesEntered { get; private set; }
    public long GracesExpired { get; private set; }

    internal ReconnectTicket? TicketOf(GameSession session) => _bySession.GetValueOrDefault(session);
    internal void ResetRefusalsForTest() => _refusals.Clear();

    // ---------------------------------------------------------------- issue

    /// <summary>
    /// Role select: the ClientAvatar entity with a fresh ReconnectInfo, then OnGetAuth(relogin: false, reconnect: false)
    /// so the client binds its micro connection with PhonestGate.bind and answers OnClientReadyInternal.
    /// </summary>
    internal async Task CreateAvatarAsync(GameSession session, string entityId)
    {
        var ticket = Issue(session, entityId);
        await session.CreateChannelEntityAsync("ClientAvatar", entityId, AvatarChannel, Encode(ticket));
        await SendGetAuthAsync(session, ticket, relogin: false, reconnect: false);
    }

    private ReconnectTicket Issue(GameSession session, string entityId)
    {
        Revoke(session);
        if (_byEntity.TryGetValue(entityId, out var stale)) Remove(stale);
        var ticket = new ReconnectTicket(entityId, session);
        _byEntity[entityId] = ticket;
        _bySession[session] = ticket;
        return ticket;
    }

    private Task SendGetAuthAsync(GameSession session, ReconnectTicket ticket, bool relogin, bool reconnect)
    {
        var gateAuth = _gateAuths.Issue(ticket.EntityId, session.Account!);
        return session.SendRpcAsync("OnGetAuth", ticket.EntityId, relogin, reconnect, gateAuth);
    }

    /// <summary>ReconnectInfo, [MessagePackObject(true)] {Auth, Mailbox}: Auth = msgpack(str), Mailbox =
    /// msgpack([Id, ServerName, ClusterId, IsProxy]) as the reference AvatarEntity.UpdateReconnectInfo builds them.</summary>
    internal byte[] Encode(ReconnectTicket ticket) => MessagePackSerializer.Serialize(new Dictionary<string, byte[]>
    {
        ["Auth"] = MessagePackSerializer.Serialize(Convert.ToHexString(ticket.Auth).ToLowerInvariant()),
        ["Mailbox"] = MessagePackSerializer.Serialize(new object[] { ticket.EntityId, "game", _options.ClusterId, false }),
    });

    /// <summary>Ends the session's ticket (handover, replacement, kicks, expiry): a later ReConnection is refused.</summary>
    internal void Revoke(GameSession session)
    {
        if (_bySession.TryGetValue(session, out var ticket)) Remove(ticket);
    }

    private void Remove(ReconnectTicket ticket)
    {
        if (_byEntity.TryGetValue(ticket.EntityId, out var current) && ReferenceEquals(current, ticket)) _byEntity.Remove(ticket.EntityId);
        if (_bySession.TryGetValue(ticket.Session, out var mine) && ReferenceEquals(mine, ticket))
        {
            _bySession.Remove(ticket.Session);
            _gateAuths.Revoke(ticket.EntityId);
        }
        _deadlines.CancelGlobal(GraceKeyPrefix + ticket.EntityId);
        _deadlines.CancelGlobal(ReadyKeyPrefix + ticket.EntityId);
        ticket.ResyncPending = false;
        ticket.Session.TransportLost = false;
    }

    private bool IsCurrent(ReconnectTicket ticket) =>
        _byEntity.TryGetValue(ticket.EntityId, out var current) && ReferenceEquals(current, ticket);

    // ---------------------------------------------------------------- client ready

    /// <summary>
    /// Avatar RPC OnClientReadyInternal(status, relogin, reconnect), sent after the client's PhonestGate.bind. A failed
    /// bind on a reconnect is kicked with LOGIC_ERROR like AvatarEntity.OnClientReadyInternal; on a first login it is
    /// only logged (inferred: the role stays playable, it just gets no reconnect grace).
    /// </summary>
    internal async Task ClientReadyAsync(GameSession session, bool status, bool relogin, bool reconnect)
    {
        if (!_bySession.TryGetValue(session, out var ticket)) return;
        if (!status)
        {
            _log.LogWarning("{Account}: OnClientReadyInternal(false, {Relogin}, {Reconnect}); micro bind failed", session.Account, relogin, reconnect);
            if (reconnect) Kick(session, ReasonLogicError);
            return;
        }
        ticket.ClientReady = true;
        if (reconnect && ticket.ResyncPending)
        {
            ticket.ResyncPending = false;
            _deadlines.CancelGlobal(ReadyKeyPrefix + ticket.EntityId);
            await ResyncAsync(session, ticket);
        }
    }

    private void Kick(GameSession session, int reason)
    {
        Revoke(session);
        session.ReconnectBlocked = true;
        var channel = session.Channel;
        _ = Task.Run(() => CloseAsync(channel, reason));
    }

    private async Task CloseAsync(PhonestChannel channel, int reason)
    {
        try
        {
            await channel.SendAsync(GameGateServer.DisconnectClient, new DisconnectClientInfo { Reason = reason });
            await channel.DisposeAsync();
        }
        catch (Exception e) { _log.LogDebug("Closing {Remote} failed: {Error}", channel.Remote, e.Message); }
    }

    // ---------------------------------------------------------------- grace

    /// <summary>
    /// The game socket of <paramref name="session"/> closed (its current transport). An in-game role whose client can
    /// reconnect keeps its world membership, dungeon, team and guild state for the grace period; returns false when
    /// the caller must run today's immediate handover.
    /// </summary>
    internal bool TryEnterGrace(GameSession session)
    {
        if (!_bySession.TryGetValue(session, out var ticket)) return false;
        bool eligible = !_closed && _loop.Phase == GameLoopPhase.Accepting && _options.ReconnectGraceSeconds > 0
            && ticket.ClientReady && !session.ReconnectBlocked && !session.SceneRecoveryRequired
            && session.Role != null && session.Owner is { State: Ownership.RoleOwnerState.Attached, Detaching: null }
            && ticket.Attachment != null && ReferenceEquals(session.Attachment, ticket.Attachment) && !ticket.Attachment.Cancelled
            && _sessions.IsWinner(session) && session.AccountGeneration == ticket.AccountGeneration;
        if (!eligible)
        {
            Remove(ticket);
            return false;
        }
        // A drop before the previous reconnect's resync: the next reconnect starts it again.
        ticket.ResyncPending = false;
        _deadlines.CancelGlobal(ReadyKeyPrefix + ticket.EntityId);
        var until = _clock.GetUtcNow().AddSeconds(_options.ReconnectGraceSeconds);
        ticket.GraceUntil = until;
        ticket.LostAt = Stopwatch.GetTimestamp();
        session.TransportLost = true;
        // (inferred) Stop what would keep acting for an absent player; everything else waits as it is.
        session.Attack = null;
        session.LockTarget = 0;
        _deadlines.SetGlobal(GraceKeyPrefix + ticket.EntityId, until, (_, _, _) =>
        {
            if (IsCurrent(ticket) && ticket.GraceUntil != null) Expire(ticket, "grace expired");
            return Task.CompletedTask;
        });
        GracesEntered++;
        _log.LogInformation("{Account} ({Entity}) lost its game connection; role kept for {Seconds} s",
            session.Account, ticket.EntityId, _options.ReconnectGraceSeconds);
        return true;
    }

    /// <summary>Today's logout for a session whose grace ended: handover cleanup, then unregister.</summary>
    private void Expire(ReconnectTicket ticket, string why)
    {
        var session = ticket.Session;
        Remove(ticket);
        GracesExpired++;
        _log.LogInformation("{Account} ({Entity}): {Why}; logging the role out", session.Account, ticket.EntityId, why);
        _loop.TrackDetached("reconnect grace expiry", async () =>
        {
            try { await _handover.Begin(session); }
            finally { _sessions.Unregister(session); }
        });
    }

    /// <summary>Host stop (before the loop quiesces): every waiting role is logged out now, so its cleanup and the
    /// durable fence run before the loop drains; no new grace starts.</summary>
    internal Task ShutdownAsync()
    {
        _closed = true;
        foreach (var ticket in _byEntity.Values.Where(t => t.GraceUntil != null).ToArray()) Expire(ticket, "server stopping");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- reconnect

    /// <summary>
    /// ConnectServerReq{ReConnection} on a new, encrypted connection (<paramref name="incoming"/> is its provisional
    /// session). The check and the transport swap run in one loop turn, without an await in between.
    /// </summary>
    internal async Task ReconnectAsync(GameSession incoming, ConnectServerReq request)
    {
        var channel = incoming.Channel;
        var now = _clock.GetUtcNow();
        string address = Address(channel.Remote);
        string? entityId = MailboxId(request.EntityInfo.Memory);
        string? refusal = incoming.Account != null ? "connection already in use"
            : Limited("ip:" + address, now) ? "rate limited (address)"
            : entityId == null ? "malformed mailbox"
            : Limited("entity:" + entityId, now) ? "rate limited (entity)"
            : null;
        ReconnectTicket? ticket = null;
        if (refusal == null)
        {
            var auth = AuthBytes(request.Auth.Memory);
            refusal = !_byEntity.TryGetValue(entityId!, out ticket) ? "unknown entity"
                : auth == null || !CryptographicOperations.FixedTimeEquals(auth, ticket.Auth) ? "auth mismatch"
                : Refuse(ticket, now);
        }
        if (refusal != null || ticket == null)
        {
            Refusals++;
            if (!refusal!.StartsWith("rate limited", StringComparison.Ordinal))
            {
                Count("ip:" + address, now);
                if (entityId != null) Count("entity:" + entityId, now);
            }
            _log.LogWarning("GameGate: reconnect of {Entity} from {Remote} refused: {Why}", entityId ?? "?", channel.Remote, refusal);
            await channel.SendAsync(GameGateServer.ConnectResponse, new ConnectServerResp { Type = ConnectServerResp.Types.RespType.ReconnectRefused });
            _loop.Schedule(RefusedCloseDelay, () =>
            {
                if (channel.Session is not GameSession owner || !ReferenceEquals(owner.Channel, channel) || owner.Account == null)
                    channel.Abort();
                return Task.CompletedTask;
            });
            return;
        }

        var session = ticket.Session;
        var old = session.Channel;
        bool oldOpen = !session.TransportLost && !ReferenceEquals(old, channel);
        session.Channel = channel;
        channel.Session = session;
        _deadlines.CancelGlobal(GraceKeyPrefix + ticket.EntityId);
        long lostAt = ticket.LostAt;
        ticket.LostAt = 0;
        ticket.GraceUntil = null;
        session.TransportLost = false;
        // The client destroyed every unit: what it was sent no longer counts (spec §4.4).
        session.SentPropertyIds.Clear();
        ticket.Auth = RandomNumberGenerator.GetBytes(16);
        if (oldOpen) _ = Task.Run(() => CloseAsync(old, ReasonBeReconnected)); // half-open: the old socket never closed
        ticket.ResyncPending = true;
        _deadlines.SetGlobal(ReadyKeyPrefix + ticket.EntityId, now + ReadyFallback, (_, _, _) =>
        {
            if (IsCurrent(ticket) && ticket.ResyncPending)
            {
                ticket.ResyncPending = false;
                _loop.TrackDetached("reconnect resync fallback", () => ResyncAsync(session, ticket));
            }
            return Task.CompletedTask;
        });
        _log.LogInformation("{Account} ({Entity}) reconnected from {Remote} after {Ms:F0} ms{HalfOpen}", session.Account,
            ticket.EntityId, channel.Remote, lostAt == 0 ? 0 : Stopwatch.GetElapsedTime(lostAt).TotalMilliseconds,
            oldOpen ? " (old connection still open)" : "");
        await channel.SendAsync(GameGateServer.ConnectResponse, new ConnectServerResp { Type = ConnectServerResp.Types.RespType.Reconnected });
        await session.CreateChannelEntityAsync("ClientAvatar", ticket.EntityId, AvatarChannel, Encode(ticket));
        await SendGetAuthAsync(session, ticket, relogin: true, reconnect: true);
    }

    private string? Refuse(ReconnectTicket ticket, DateTimeOffset now)
    {
        var session = ticket.Session;
        if (ticket.GraceUntil is { } until && now > until) return "grace expired";
        if (session.ReconnectBlocked || session.SceneRecoveryRequired) return "recovery required";
        if (session.Role == null || session.Owner is not { State: Ownership.RoleOwnerState.Attached, Detaching: null }
            || ticket.Attachment == null || !ReferenceEquals(session.Attachment, ticket.Attachment) || ticket.Attachment.Cancelled)
            return "role no longer attached";
        if (!_sessions.IsWinner(session) || session.AccountGeneration != ticket.AccountGeneration) return "replaced by a newer login";
        return null;
    }

    /// <summary>1011, then the scene resync (§4.5); a pending 70010 handoff is completed by the client's 30005.</summary>
    private async Task ResyncAsync(GameSession session, ReconnectTicket ticket)
    {
        var role = session.Role;
        var lease = session.Attachment;
        var channel = session.Channel;
        if (role == null || !IsCurrent(ticket)) return;
        bool Current() => IsCurrent(ticket) && ReferenceEquals(session.Role, role) && ReferenceEquals(session.Attachment, lease)
            && lease?.Cancelled != true && ReferenceEquals(session.Channel, channel) && !session.TransportLost;
        using var scope = session.Owners is { } owners
            ? await owners.EnterAsync("reconnect resync", Ownership.ReservationKey.Role(role.Id), Ownership.ReservationKey.Lifecycle(role.Id))
            : null;
        if (!Current()) return;
        bool handoffPending = session.SceneLoading && session.Handoff is { Completing: false };
        await session.SendGameAsync(MsgId.LoginReconnect, new UserLoginReconnectRsp
        {
            Result = 0,
            RoleID = role.Id,
            Account = session.Account ?? "",
            NewToken = "",
            AreaID = role.Area,
            SceneId = session.SceneEntry,
        });
        if (!Current()) return;
        if (handoffPending) session.ReconnectAoiPending = true;
        else await _scene.ResyncAsync(session, Current);
        Reconnects++;
        _log.LogInformation("{Account} resynced after reconnect (scene {Scene}{Pending})", session.Account, session.SceneEntry,
            handoffPending ? ", map load pending" : "");
    }

    // ---------------------------------------------------------------- GM

    private async Task NetDropAsync(GameSession session, string[] args)
    {
        bool reset = args.Length > 0 && args[0].Equals("rst", StringComparison.OrdinalIgnoreCase);
        await GmCommands.ReplyAsync(session, $"Dropping this game connection ({(reset ? "RST" : "FIN")}).");
        var channel = session.Channel;
        // Late enough for the reply to leave; a FIN/RST from the server, the client sees an ordinary drop.
        _loop.Schedule(TimeSpan.FromMilliseconds(300), () =>
        {
            if (ReferenceEquals(session.Channel, channel))
            {
                if (reset) channel.AbortReset();
                else channel.Abort();
            }
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- helpers

    private bool Limited(string key, DateTimeOffset now) =>
        _refusals.TryGetValue(key, out var times) && Prune(times, now) >= RefusalLimit;

    private void Count(string key, DateTimeOffset now)
    {
        if (!_refusals.TryGetValue(key, out var times))
        {
            if (_refusals.Count > 4096)
                foreach (var (k, q) in _refusals.ToArray())
                    if (Prune(q, now) == 0) _refusals.Remove(k);
            _refusals[key] = times = new Queue<DateTimeOffset>();
        }
        Prune(times, now);
        times.Enqueue(now);
    }

    private static int Prune(Queue<DateTimeOffset> times, DateTimeOffset now)
    {
        while (times.Count > 0 && now - times.Peek() > RefusalWindow) times.Dequeue();
        return times.Count;
    }

    private static string Address(string remote)
    {
        int colon = remote.LastIndexOf(':');
        return colon > 0 ? remote[..colon] : remote;
    }

    /// <summary>Mailbox.Id: [MessagePackObject(false)] array [Id, ServerName, ClusterId, IsProxy]; a map form is accepted.</summary>
    internal static string? MailboxId(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            return MessagePackSerializer.Deserialize<object>(bytes, Untrusted) switch
            {
                object[] { Length: > 0 } array => array[0] as string,
                IDictionary<object, object?> map => (map.TryGetValue("Id", out var id) ? id : map.TryGetValue((byte)0, out id) ? id : null) as string,
                _ => null,
            };
        }
        catch (MessagePackSerializationException) { return null; }
    }

    /// <summary>ReconnectInfo.Auth: msgpack(str hex) as issued; raw bin is accepted too.</summary>
    internal static byte[]? AuthBytes(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            return MessagePackSerializer.Deserialize<object>(bytes, Untrusted) switch
            {
                string hex when hex.Length == 32 => Convert.FromHexString(hex),
                byte[] { Length: 16 } raw => raw,
                _ => null,
            };
        }
        catch (Exception e) when (e is MessagePackSerializationException or FormatException) { return null; }
    }
}

/// <summary>Stops before <see cref="GameLoopQuiesceService"/> (hosted services stop in reverse order): roles waiting
/// for a reconnect are logged out while the loop still admits their cleanup.</summary>
internal sealed class ReconnectShutdownService(GameLoop loop, ReconnectService reconnects) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await loop.InvokeAsync(reconnects.ShutdownAsync).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }
}
