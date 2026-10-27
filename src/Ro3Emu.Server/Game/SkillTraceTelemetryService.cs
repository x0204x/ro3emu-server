using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.ClientHooks;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Opt-in player-client evidence for skill presentation. The server already knows which skill messages it sent; this
/// channel records whether a permitted patched client received and completed each route/timeline stage. It stores no
/// account, role name, address, nonce, chat, raw request or stack trace.
/// </summary>
public sealed class SkillTraceTelemetryService : Messaging.IMessageModule, IDisposable
{
    [Flags]
    private enum StageMask : byte
    {
        None = 0,
        Route = 1,
        Handler = 2,
        Timeline = 4,
    }

    private sealed class ExpectedEvent(long id, DateTimeOffset at, uint msgId, ServerObservation observation,
        StageMask required)
    {
        public long Id { get; } = id;
        public DateTimeOffset At { get; } = at;
        public uint MsgId { get; } = msgId;
        public ServerObservation Observation { get; } = observation;
        public StageMask Required { get; } = required;
        public StageMask Seen { get; set; }
    }

    private sealed class SessionState(byte[] token, string traceId, DateTimeOffset expires, SkillTraceRateLimiter limiter)
    {
        public byte[] Token { get; } = token;
        public string TraceId { get; } = traceId;
        public DateTimeOffset Expires { get; } = expires;
        public SkillTraceRateLimiter Limiter { get; } = limiter;
        public object Gate { get; } = new();
        public LinkedList<ExpectedEvent> Expected { get; } = [];
        public uint LastSequence { get; set; }
        public long NextEventId { get; set; }
        public int ClientSeen;
    }

    private readonly record struct Entry(DateTimeOffset At, string TraceId, long RoleId, string Side, uint MsgId,
        ServerObservation? Observation, SkillTraceProtocol.Report? Report, long EventId, string Status,
        bool OutOfOrder, string[] MissingStages, int PayloadBytes);

    private readonly record struct ServerObservation(int SkillId, long SourceId, long TargetId, long SessionId,
        int ShowPoint, int EffectId, long StartTimestampMs, int TargetCount, long[] TargetIds);

    private static readonly HashSet<uint> SkillMessages =
    [
        CombatService.UseInSkill, CombatService.UseInSkillHigh, CombatService.ExtraUseInSkill,
        CombatService.ExtraUseInSkillHigh, CombatService.SkillHit, CombatService.SkillEffect,
        CombatService.SkillEffectHigh, CombatService.SkillBullet, CombatService.SkillBulletHigh,
    ];

    private const int QueueCapacity = 8192;
    private const int MaxExpectedPerSession = 256;
    private static readonly TimeSpan ExpectedLifetime = TimeSpan.FromSeconds(15);
    private readonly SkillTraceTelemetryOptions _options;
    private readonly TimeProvider _clock;
    private readonly SceneService _scene;
    private readonly string _logDir;
    private readonly ConcurrentDictionary<GameSession, SessionState> _states = new();
    private readonly Channel<Entry>? _queue;
    private readonly Task? _writerTask;
    private readonly CancellationTokenSource? _sweepStop;
    private readonly Task? _sweepTask;
    private long _accepted, _serverEvents, _unmatched, _completed, _gaps, _dropped, _reportedDropped, _grantRetries;
    private long _grantRetryFailures;
    private long _writerFailures;
    private int _disposed;
    private string? _lastWriterError;

    public SkillTraceTelemetryService(IOptions<ServerOptions> options, TimeProvider clock, SceneService scene)
    {
        _options = options.Value.SkillTraceTelemetry;
        _clock = clock;
        _scene = scene;
        _scene.Loaded.Add(GrantAsync);
        _logDir = Path.Combine(options.Value.DataDir, "logs");
        if (!_options.Enabled) return;
        Directory.CreateDirectory(_logDir);
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        }, _ => Interlocked.Increment(ref _dropped));
        _writerTask = Task.Factory.StartNew(() => WriteLoopAsync(_queue.Reader), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _sweepStop = new CancellationTokenSource();
        _sweepTask = Task.Run(() => SweepLoopAsync(_sweepStop.Token));
    }

    internal long Accepted => Interlocked.Read(ref _accepted);
    internal long ServerEvents => Interlocked.Read(ref _serverEvents);
    internal long Unmatched => Interlocked.Read(ref _unmatched);
    internal long Completed => Interlocked.Read(ref _completed);
    internal long Gaps => Interlocked.Read(ref _gaps);
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal long GrantRetries => Interlocked.Read(ref _grantRetries);
    internal long GrantRetryFailures => Interlocked.Read(ref _grantRetryFailures);
    internal long WriterFailures => Interlocked.Read(ref _writerFailures);
    internal string? LastWriterError => Volatile.Read(ref _lastWriterError);
    internal bool HasGrant(GameSession session) => _states.ContainsKey(session);

    public void Map(Messaging.MessageRoutes routes)
    {
        routes.AdmitRole(id => id == SkillTraceProtocol.ReportMsgId);
        routes.Map(SkillTraceProtocol.ReportMsgId, "skill-trace", HandleAsync);
    }

    /// <summary>Issues a connection-scoped grant to every player when explicitly enabled, or to a listed account.</summary>
    public async Task GrantAsync(GameSession session)
    {
        Reset(session);
        if (!Allowed(session) || session.Role == null) return;
        byte[] token = RandomNumberGenerator.GetBytes(SkillTraceProtocol.TokenSize);
        var state = new SessionState(token, Guid.NewGuid().ToString("N"),
            _clock.GetUtcNow().AddSeconds(_options.GrantSeconds),
            new SkillTraceRateLimiter(_options.EventsPerSecond, _options.Burst));
        _states[session] = state;
        await SendGrantAsync(session, state, _clock.GetUtcNow());
    }

    /// <summary>A role switch or reconnect invalidates the previous connection's token.</summary>
    public void Reset(GameSession session) => RemoveState(session, "grant-reset");

    /// <summary>Records only the nine skill-presentation messages and only while this client has a live grant.</summary>
    public void ServerToClient(GameSession session, uint msgId, byte[] body)
    {
        if (!SkillMessages.Contains(msgId) || !Current(session, out var state)) return;
        DateTimeOffset now = _clock.GetUtcNow();
        long roleId = session.Role?.Id ?? 0;
        Expire(state, roleId, now, "timeout");
        Interlocked.Increment(ref _serverEvents);
        if (!TryObserve(msgId, body, out var observation))
        {
            Enqueue(new Entry(now, state.TraceId, roleId, "server", msgId, null, null, 0,
                "parse-failed", false, [], body.Length));
            return;
        }

        ExpectedEvent expected;
        lock (state.Gate)
        {
            if (state.Expected.Count >= MaxExpectedPerSession)
            {
                var evicted = state.Expected.First!.Value;
                state.Expected.RemoveFirst();
                Gap(state, roleId, evicted, now, "capacity");
            }
            StageMask required = StageMask.Route | StageMask.Handler;
            if (msgId is CombatService.UseInSkill or CombatService.UseInSkillHigh
                or CombatService.ExtraUseInSkill or CombatService.ExtraUseInSkillHigh)
                required |= StageMask.Timeline;
            expected = new ExpectedEvent(++state.NextEventId, now, msgId, observation, required);
            state.Expected.AddLast(expected);
        }
        Enqueue(new Entry(now, state.TraceId, roleId, "server", msgId, observation, null,
            expected.Id, "expected", false, Missing(expected.Required, expected.Seen), body.Length));
    }

    private Task HandleAsync(GameSession session, uint _, ReadOnlyMemory<byte> body)
    {
        if (!Current(session, out var state) || body.Length > _options.MaxPayloadBytes) return Task.CompletedTask;
        byte[] bytes = body.ToArray();
        if (!SkillTraceProtocol.TryDecodeReport(bytes, out var report)
            || !SkillMessages.Contains(report.MsgId)
            || !CryptographicOperations.FixedTimeEquals(report.Token, state.Token)
            || report.Sequence <= state.LastSequence || !state.Limiter.TryTake()) return Task.CompletedTask;
        state.LastSequence = report.Sequence;
        Volatile.Write(ref state.ClientSeen, 1);
        DateTimeOffset now = _clock.GetUtcNow();
        long roleId = session.Role?.Id ?? 0;
        Expire(state, roleId, now, "timeout");
        MatchReport(state, roleId, now, report);
        return Task.CompletedTask;
    }

    private void MatchReport(SessionState state, long roleId, DateTimeOffset now, SkillTraceProtocol.Report report)
    {
        StageMask stage = Mask(report.EventStage);
        ExpectedEvent? expected = null;
        bool outOfOrder = false;
        bool complete = false;
        bool duplicate = false;
        lock (state.Gate)
        {
            ExpectedEvent? duplicateCandidate = null;
            var node = state.Expected.First;
            while (node != null)
            {
                if (Matches(node.Value, report))
                {
                    if (stage == StageMask.None || (node.Value.Required & stage) == 0
                        || (node.Value.Seen & stage) != 0)
                    {
                        duplicateCandidate ??= node.Value;
                        node = node.Next;
                        continue;
                    }
                    expected = node.Value;
                    outOfOrder = stage switch
                    {
                        StageMask.Handler => (expected.Seen & StageMask.Route) == 0,
                        StageMask.Timeline => (expected.Seen & (StageMask.Route | StageMask.Handler))
                            != (StageMask.Route | StageMask.Handler),
                        _ => false,
                    };
                    expected.Seen |= stage;
                    complete = (expected.Seen & expected.Required) == expected.Required;
                    if (complete) state.Expected.Remove(node);
                    break;
                }
                node = node.Next;
            }
            if (expected == null && duplicateCandidate != null)
            {
                expected = duplicateCandidate;
                duplicate = true;
            }
        }

        if (expected == null)
        {
            Interlocked.Increment(ref _unmatched);
            Enqueue(new Entry(now, state.TraceId, roleId, "client", report.MsgId, null, report, 0,
                "unmatched", false, [], 0));
            return;
        }
        if (duplicate)
        {
            Enqueue(new Entry(now, state.TraceId, roleId, "client", report.MsgId, null, report, expected.Id,
                "duplicate-stage", false, Missing(expected.Required, expected.Seen), 0));
            return;
        }

        Interlocked.Increment(ref _accepted);
        if (complete) Interlocked.Increment(ref _completed);
        Enqueue(new Entry(now, state.TraceId, roleId, "client", report.MsgId, null, report, expected.Id,
            complete ? "complete" : "matched", outOfOrder, Missing(expected.Required, expected.Seen), 0));
    }

    private static bool Matches(ExpectedEvent expected, SkillTraceProtocol.Report report)
    {
        ServerObservation server = expected.Observation;
        return expected.MsgId == report.MsgId
            && server.SkillId == report.SkillId
            && server.SourceId == report.SourceId
            && Compatible(server.TargetId, report.TargetId)
            && Compatible(server.SessionId, report.SessionId)
            && Compatible(server.ShowPoint, report.ShowPoint)
            && Compatible(server.EffectId, report.EffectId)
            && Compatible(server.StartTimestampMs, report.StartTimestampMs);
    }

    private static bool Compatible(long server, long client) => server == 0 || client == 0 || server == client;

    private static StageMask Mask(SkillTraceProtocol.Stage stage) => stage switch
    {
        SkillTraceProtocol.Stage.RouteReceived => StageMask.Route,
        SkillTraceProtocol.Stage.HandlerCompleted or SkillTraceProtocol.Stage.HandlerFailed => StageMask.Handler,
        SkillTraceProtocol.Stage.TimelineBuilt or SkillTraceProtocol.Stage.TimelineFailed => StageMask.Timeline,
        _ => StageMask.None,
    };

    private static string[] Missing(StageMask required, StageMask seen)
    {
        List<string> missing = [];
        if ((required & StageMask.Route) != 0 && (seen & StageMask.Route) == 0) missing.Add("RouteReceived");
        if ((required & StageMask.Handler) != 0 && (seen & StageMask.Handler) == 0) missing.Add("HandlerTerminal");
        if ((required & StageMask.Timeline) != 0 && (seen & StageMask.Timeline) == 0)
            missing.Add("TimelineTerminal");
        return [.. missing];
    }

    private bool Current(GameSession session, out SessionState state)
    {
        state = null!;
        if (!_options.Enabled || !Allowed(session) || !_states.TryGetValue(session, out var current)) return false;
        state = current;
        if (_clock.GetUtcNow() < state.Expires) return true;
        RemoveState(session, "grant-expired");
        state = null!;
        return false;
    }

    private bool Allowed(GameSession session) => session.Account is { Length: > 0 } account
        && (_options.AllowAllAccounts || _options.Accounts?.Contains(account, StringComparer.OrdinalIgnoreCase) == true);

    private void RemoveState(GameSession session, string reason)
    {
        if (!_states.TryRemove(session, out var state)) return;
        DateTimeOffset now = _clock.GetUtcNow();
        long roleId = session.Role?.Id ?? 0;
        lock (state.Gate)
        {
            foreach (ExpectedEvent expected in state.Expected) Gap(state, roleId, expected, now, reason);
            state.Expected.Clear();
        }
    }

    private void Expire(SessionState state, long roleId, DateTimeOffset now, string reason)
    {
        lock (state.Gate)
        {
            while (state.Expected.First is { } node && now - node.Value.At >= ExpectedLifetime)
            {
                state.Expected.RemoveFirst();
                Gap(state, roleId, node.Value, now, reason);
            }
        }
    }

    private void Gap(SessionState state, long roleId, ExpectedEvent expected, DateTimeOffset now, string reason)
    {
        Interlocked.Increment(ref _gaps);
        Enqueue(new Entry(now, state.TraceId, roleId, "summary", expected.MsgId, expected.Observation, null,
            expected.Id, $"gap:{reason}", false, Missing(expected.Required, expected.Seen), 0));
    }

    private void Enqueue(in Entry entry) => _queue?.Writer.TryWrite(entry);

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                DateTimeOffset now = _clock.GetUtcNow();
                foreach (var item in _states) Expire(item.Value, item.Key.Role?.Id ?? 0, now, "timeout");
                await RetryUnacknowledgedGrantsAsync(now).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// The client's game-message hook is installed by its chat adapter and may become ready after the scene-loaded
    /// grant was sent. Retry the same connection token until the first valid client checkpoint proves the hook is
    /// listening; do not rotate the token or extend the server-side expiry.
    /// </summary>
    internal Task RetryUnacknowledgedGrantsAsync() => RetryUnacknowledgedGrantsAsync(_clock.GetUtcNow());

    private async Task RetryUnacknowledgedGrantsAsync(DateTimeOffset now)
    {
        foreach (var item in _states)
        {
            if (Volatile.Read(ref item.Value.ClientSeen) != 0 || now >= item.Value.Expires) continue;
            try
            {
                await SendGrantAsync(item.Key, item.Value, now).ConfigureAwait(false);
                Interlocked.Increment(ref _grantRetries);
            }
            catch (Exception)
            {
                // A disconnect racing the sweep is cleaned up by Reset; it must not stop retries for other players.
                Interlocked.Increment(ref _grantRetryFailures);
            }
        }
    }

    private Task SendGrantAsync(GameSession session, SessionState state, DateTimeOffset now)
    {
        uint ttl = (uint)Math.Max(1, Math.Ceiling((state.Expires - now).TotalSeconds));
        return session.SendGameRawAsync(SkillTraceProtocol.GrantMsgId, SkillTraceProtocol.EncodeGrant(state.Token,
            ttl, (ushort)_options.EventsPerSecond, (ushort)_options.Burst));
    }

    private async Task WriteLoopAsync(ChannelReader<Entry> reader)
    {
        StreamWriter? writer = null;
        string? day = null;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    try
                    {
                        string nextDay = entry.At.ToString("yyyyMMdd");
                        if (nextDay != day)
                        {
                            writer?.Dispose();
                            writer = new StreamWriter(new FileStream(Path.Combine(_logDir, $"skilltrace-{nextDay}.jsonl"),
                                FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 1 << 15);
                            day = nextDay;
                        }
                        long dropped = Interlocked.Read(ref _dropped);
                        if (dropped != _reportedDropped)
                        {
                            writer!.WriteLine(JsonSerializer.Serialize(new
                            {
                                t = entry.At,
                                side = "log",
                                dropped = dropped - _reportedDropped,
                            }));
                            _reportedDropped = dropped;
                        }
                        writer!.WriteLine(Format(entry));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _writerFailures);
                        Volatile.Write(ref _lastWriterError, ex.GetType().Name + ": " + ex.Message);
                        writer?.Dispose();
                        writer = null;
                        day = null;
                    }
                }
                try { writer?.Flush(); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _writerFailures);
                    Volatile.Write(ref _lastWriterError, ex.GetType().Name + ": " + ex.Message);
                    writer?.Dispose();
                    writer = null;
                    day = null;
                }
            }
        }
        finally { writer?.Dispose(); }
    }

    private static string Format(in Entry entry)
    {
        if (entry.Report is { } report)
            return JsonSerializer.Serialize(new
            {
                t = entry.At,
                trace = entry.TraceId,
                side = entry.Side,
                roleId = entry.RoleId,
                eventId = entry.EventId,
                status = entry.Status,
                outOfOrder = entry.OutOfOrder,
                missingStages = entry.MissingStages,
                sequence = report.Sequence,
                clientMs = report.ClientMonotonicMs,
                msgId = report.MsgId,
                skillId = report.SkillId,
                sourceId = report.SourceId,
                targetId = report.TargetId,
                sessionId = report.SessionId,
                correlationId = report.CorrelationId,
                showPoint = report.ShowPoint,
                effectId = report.EffectId,
                startTimestampMs = report.StartTimestampMs,
                stage = report.EventStage.ToString(),
                success = report.Success,
                reason = report.Reason.Length == 0 ? null : report.Reason,
            });

        if (entry.Observation is { } observation)
            return JsonSerializer.Serialize(new
            {
                t = entry.At,
                trace = entry.TraceId,
                side = entry.Side,
                roleId = entry.RoleId,
                eventId = entry.EventId,
                status = entry.Status,
                missingStages = entry.MissingStages,
                msgId = entry.MsgId,
                skillId = observation.SkillId,
                sourceId = observation.SourceId,
                targetId = observation.TargetId,
                sessionId = observation.SessionId,
                showPoint = observation.ShowPoint,
                effectId = observation.EffectId,
                startTimestampMs = observation.StartTimestampMs,
                targetCount = observation.TargetCount,
                targetIds = observation.TargetIds,
            });

        return JsonSerializer.Serialize(new
        {
            t = entry.At,
            trace = entry.TraceId,
            side = entry.Side,
            roleId = entry.RoleId,
            status = entry.Status,
            msgId = entry.MsgId,
            parsed = false,
            payloadBytes = entry.PayloadBytes,
        });
    }

    private static bool TryObserve(uint msgId, ReadOnlyMemory<byte> body, out ServerObservation observation)
    {
        try
        {
            observation = Observe(msgId, body);
            return true;
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            observation = default;
            return false;
        }
    }

    private static ServerObservation Observe(uint msgId, ReadOnlyMemory<byte> body)
    {
        if (msgId is CombatService.UseInSkill or CombatService.UseInSkillHigh
            or CombatService.ExtraUseInSkill or CombatService.ExtraUseInSkillHigh)
        {
            var cast = UseInSkillInfo.Parser.ParseFrom(body.Span);
            return new(cast.SkillID, cast.SrcID, cast.TargetId, cast.SessionId, 0, 0, cast.StartTime, 0, []);
        }
        if (msgId == CombatService.SkillHit)
        {
            var hit = SkillHitInfo.Parser.ParseFrom(body.Span);
            long[] targets = hit.Dsts.Take(32).Select(target => target.DstID).ToArray();
            return new(hit.SkillId, hit.SrcID, targets.FirstOrDefault(), hit.SessionId, hit.ShowPoint, 0, 0,
                hit.Dsts.Count, targets);
        }
        if (msgId is CombatService.SkillEffect or CombatService.SkillEffectHigh)
        {
            var effect = SkillEffectDisplayNotify.Parser.ParseFrom(body.Span);
            return new((int)effect.SkillId, effect.SourceId, effect.TargetId, 0, 0, effect.EffectId, 0, 0, []);
        }
        var bullet = SkillFixTimeBulletNotify.Parser.ParseFrom(body.Span);
        return new(bullet.SkillConfigId, bullet.SourceId, bullet.TargetId, 0, 0, bullet.EffectId,
            bullet.StartTimestampMs, 0, []);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _scene.Loaded.Remove(GrantAsync);
        _sweepStop?.Cancel();
        try { _sweepTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _sweepStop?.Dispose();
        foreach (GameSession session in _states.Keys) RemoveState(session, "dispose");
        if (_queue == null) return;
        _queue.Writer.TryComplete();
        try { _writerTask!.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }
}

/// <summary>Small real-time token bucket; a flood is dropped before it reaches the diagnostic writer.</summary>
internal sealed class SkillTraceRateLimiter(int eventsPerSecond, int burst)
{
    private readonly double _capacity = eventsPerSecond + burst;
    private double _tokens = eventsPerSecond + burst;
    private long _lastRefillMs = Environment.TickCount64;

    public bool TryTake()
    {
        long now = Environment.TickCount64;
        double elapsed = (now - _lastRefillMs) / 1000d;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsed * eventsPerSecond);
            _lastRefillMs = now;
        }
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
