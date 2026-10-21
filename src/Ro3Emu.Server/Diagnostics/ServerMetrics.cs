using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>Per-host metrics. Reset exchanges the measurement epoch; live connection gauges are never reset.
/// Hot-path records allocate no strings and take no locks. First use of a bounded label creates its counters.</summary>
public sealed class ServerMetrics : IDisposable
{
    internal Accounts.Persistence.DbAccessMetrics? DbAccess;
    internal Game.Ownership.OwnerRegistry? Owners;
    [ThreadStatic] internal static ServerMetrics? LoopOwner;
    internal static readonly AsyncLocal<RequestMeasurement?> Request = new();
    internal Epoch Current => Volatile.Read(ref _epoch);
    private Epoch _epoch = new();
    private readonly ConcurrentDictionary<long, ConnectionMetrics> _connections = new();
    private long _connectionId, _egressBytes, _peakEgressBytes, _peakConnectionEgressBytes;
    private readonly long _runtimeRegistration;
    private int _loopPhase, _loopOutstanding;
    private long _loopRejected;
    private int _loopDrainFailed;

    public ServerMetrics(bool observeRuntime = true)
    {
        if (observeRuntime) _runtimeRegistration = RuntimeEvents.Shared.Value.Register(this);
    }
    public void Dispose()
    {
        if (_runtimeRegistration != 0) RuntimeEvents.Shared.Value.Unregister(_runtimeRegistration);
    }
    public string Reset()
    {
        var next = new Epoch();
        Interlocked.Exchange(ref _epoch, next);
        return next.Id;
    }

    public ConnectionMetrics Connect()
    {
        long id = Interlocked.Increment(ref _connectionId);
        var connection = new ConnectionMetrics(this, id);
        _connections[id] = connection;
        return connection;
    }
    internal void Disconnect(long id) => _connections.TryRemove(id, out _);
    internal void EgressDelta(long delta)
    {
        long current = Interlocked.Add(ref _egressBytes, delta);
        FixedHistogram.UpdateMax(ref _peakEgressBytes, current);
    }
    internal void ConnectionPeak(long bytes) => FixedHistogram.UpdateMax(ref _peakConnectionEgressBytes, bytes);
    public RequestMeasurement BeginRequest(ConnectionMetrics? connection, uint id, long receivedAt)
        => new(this, connection, id, receivedAt);

    public void RecordQueueItem(long queuedAt, long startedAt, long finishedAt)
    {
        var e = Current;
        e.QueueAgeMs.Record(Stopwatch.GetElapsedTime(queuedAt, startedAt).TotalMilliseconds);
        e.ItemDurationMs.Record(Stopwatch.GetElapsedTime(startedAt, finishedAt).TotalMilliseconds);
        Interlocked.Add(ref e.BusyTicks, finishedAt - startedAt);
    }
    public void RecordLoopLifecycle(int phase, int outstanding, long rejected, bool drainFailed)
    {
        Volatile.Write(ref _loopPhase, phase);
        Volatile.Write(ref _loopOutstanding, outstanding);
        Volatile.Write(ref _loopRejected, rejected);
        Volatile.Write(ref _loopDrainFailed, drainFailed ? 1 : 0);
    }
    internal void RecordDbCallSite(string operation, string site)
    {
        var key = operation + " @ " + site;
        var counts = Current.DbCallSites;
        counts.AddOrUpdate(counts.Count < 512 ? key : "(other)", 1, static (_, n) => n + 1);
    }

    /// <summary>Loop items over 10 ms, by the work they ran (bounded to 256 names).</summary>
    public void RecordSlowItem(string name, double durationMs) =>
        Current.SlowItems.GetOrAdd(Current.SlowItems.Count < 256 ? name : "(other)", static _ => new()).Record(durationMs);

    /// <summary>One ticker's share of a tick, by ticker type (bounded: one entry per registered type).</summary>
    public void RecordTicker(string type, double durationMs) =>
        Current.Tickers.GetOrAdd(type, static _ => new()).Record(durationMs);

    public void RecordTimer(double latenessMs, double durationMs)
    {
        var e = Current;
        e.TimerLatenessMs.Record(Math.Max(0, latenessMs));
        e.TimerDurationMs.Record(durationMs);
    }
    public void RecordTick(double durationMs, double cadenceMs, double tickerMs, long timerAndTickerTicks, int items, int queueLength)
    {
        var e = Current;
        e.TickDurationMs.Record(durationMs);
        e.CadenceMs.Record(cadenceMs);
        e.TickerDurationMs.Record(tickerMs);
        e.ItemsPerTick.Record(items);
        e.QueueLength.Record(queueLength);
        Volatile.Write(ref e.QueueLengthCurrent, queueLength);
        Interlocked.Add(ref e.BusyTicks, timerAndTickerTicks);
        if (durationMs > 50) Interlocked.Increment(ref e.Overruns);
    }

    internal void RecordDatabase(string operation, long start, bool onLoop, int threadId, bool commit)
    {
        var e = Current;
        if (!e.Database.TryGetValue(operation, out var op))
            op = e.Database.GetOrAdd(e.Database.Count < 2048 ? operation : "other/overflow", static _ => new());
        double durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        op.DurationMs.Record(durationMs);
        Interlocked.Increment(ref op.Count);
        if (onLoop) { Interlocked.Increment(ref op.LoopCount); op.LoopDurationMs.Record(durationMs); }
        else { Interlocked.Increment(ref op.OtherCount); op.OtherDurationMs.Record(durationMs); }
        // Thread labels have a fixed cap even during long-running connection churn.
        if (!op.Threads.TryGetValue(threadId, out var thread))
            thread = op.Threads.GetOrAdd(op.Threads.Count < 128 ? threadId : -1, static _ => new());
        Interlocked.Increment(ref thread.Count);
        if (commit)
        {
            Interlocked.Increment(ref e.Commits);
            Interlocked.Increment(ref op.Commits);
            if (onLoop) Interlocked.Increment(ref op.LoopCommits);
            e.CommitStatementMs.Record(durationMs);
        }
    }
    public DatabaseOpenMeasurement MeasureDatabaseOpen() => new(this);

    public object Snapshot(SessionCensus.Snapshot? census = null, PublicLineManager? publicLines = null)
    {
        var e = Current;
        double elapsed = Stopwatch.GetElapsedTime(e.StartedAt).TotalSeconds;
        var tick = e.TickDurationMs.Snapshot();
        using var process = Process.GetCurrentProcess();
        census ??= SessionCensus.Snapshot.Empty;
        var connections = _connections.Values.ToArray();
        var operations = e.Database.ToDictionary(x => x.Key, x => new
        {
            count = Volatile.Read(ref x.Value.Count), loopCount = Volatile.Read(ref x.Value.LoopCount),
            otherCount = Volatile.Read(ref x.Value.OtherCount), durationMs = x.Value.DurationMs.Snapshot(),
            loopDurationMs = x.Value.LoopDurationMs.Snapshot(), otherDurationMs = x.Value.OtherDurationMs.Snapshot(),
            commits = Volatile.Read(ref x.Value.Commits), loopCommits = Volatile.Read(ref x.Value.LoopCommits),
            threads = x.Value.Threads.ToDictionary(t => t.Key, t => Volatile.Read(ref t.Value.Count)),
        });
        double busyMs = Volatile.Read(ref e.BusyTicks) * 1000.0 / Stopwatch.Frequency;
        return new
        {
            schemaVersion = 1, epoch = e.Id, capturedUtc = DateTimeOffset.UtcNow, elapsedSeconds = elapsed,
            semantics = "Cumulative epoch counters; database.routedAccess is cumulative for the AccountStore lifetime and requires start/end subtraction. Histogram buckets are disjoint upper bounds; snapshots are weakly consistent. Wire replies cover the first same-id response on the originating connection, after a full socket send. Egress gauges include the frame currently being sent and are lifetime gauges. DB commitStatementMs includes the statement causing an implicit commit, not only fsync. Sessions are the game loop's census, taken once a second (sessions.capturedUtc).",
            loop = new
            {
                lifecycle = new
                {
                    phase = ((GameLoopPhase)Volatile.Read(ref _loopPhase)).ToString(),
                    outstanding = Volatile.Read(ref _loopOutstanding), rejected = Volatile.Read(ref _loopRejected),
                    drainFailed = Volatile.Read(ref _loopDrainFailed) != 0
                },
                ticks = tick.Count, ticksPerSecond = tick.Count / Math.Max(elapsed, .001), busyMs,
                busyRatio = busyMs / Math.Max(elapsed * 1000, 1), overruns = Volatile.Read(ref e.Overruns),
                tickDurationMs = tick, cadenceMs = e.CadenceMs.Snapshot(), queueLength = e.QueueLength.Snapshot(),
                queueLengthCurrent = Volatile.Read(ref e.QueueLengthCurrent), queueAgeMs = e.QueueAgeMs.Snapshot(),
                itemDurationMs = e.ItemDurationMs.Snapshot(), itemsPerTick = e.ItemsPerTick.Snapshot(),
                timerLatenessMs = e.TimerLatenessMs.Snapshot(), timerDurationMs = e.TimerDurationMs.Snapshot(),
                tickerDurationMs = e.TickerDurationMs.Snapshot(),
                tickers = e.Tickers.ToDictionary(x => x.Key, x => x.Value.Snapshot()),
                slowItems = e.SlowItems.ToDictionary(x => x.Key, x => x.Value.Snapshot()),
                dbCallSites = e.DbCallSites.ToDictionary(x => x.Key, x => x.Value),
            },
            inbound = e.Inbound.ToDictionary(x => x.Key, x => new
            {
                count = Volatile.Read(ref x.Value.Count), completed = Volatile.Read(ref x.Value.Completed),
                failed = Volatile.Read(ref x.Value.Failed), handlingMs = x.Value.HandlingMs.Snapshot(),
                wireReplies = x.Value.WireReplyMs.Snapshot().Count, wireReplyMs = x.Value.WireReplyMs.Snapshot(),
            }),
            outbound = e.Outbound.ToDictionary(x => x.Key, x => new
            {
                count = Volatile.Read(ref x.Value.Count), bytes = Volatile.Read(ref x.Value.Bytes), recipients = Volatile.Read(ref x.Value.Count),
            }),
            wire = new
            {
                framesIn = Volatile.Read(ref e.FramesIn), framesOut = Volatile.Read(ref e.FramesOut),
                socketSends = Volatile.Read(ref e.SocketSends), bytesIn = Volatile.Read(ref e.BytesIn), bytesOut = Volatile.Read(ref e.BytesOut),
                partialSends = Volatile.Read(ref e.PartialSends), writerErrors = Volatile.Read(ref e.WriterErrors),
                connections = connections.Length, currentEgressQueueBytes = Volatile.Read(ref _egressBytes),
                peakEgressQueueBytes = Volatile.Read(ref _peakEgressBytes),
                maxConnectionEgressQueueBytes = Volatile.Read(ref _peakConnectionEgressBytes),
                connectionQueueBytes = connections.Select(c => new { connection = c.Id, current = c.CurrentBytes, peak = c.PeakBytes }).ToArray(),
            },
            database = new
            {
                statements = operations.Where(x => x.Key != "connection/OPEN").Sum(x => x.Value.count), commits = Volatile.Read(ref e.Commits),
                loopStatements = operations.Where(x => x.Key != "connection/OPEN").Sum(x => x.Value.loopCount),
                otherStatements = operations.Where(x => x.Key != "connection/OPEN").Sum(x => x.Value.otherCount),
                opens = operations.Where(x => x.Key == "connection/OPEN").Sum(x => x.Value.count),
                loopOpens = operations.Where(x => x.Key == "connection/OPEN").Sum(x => x.Value.loopCount),
                commitStatementMs = e.CommitStatementMs.Snapshot(), operations, profileErrors = Volatile.Read(ref e.DatabaseProfileErrors),
                writeBehindFlushLatency = (double?)null,
                routedAccess = DbAccess?.Snapshot(),
            },
            ownership = Owners?.Snapshot(),
            sessions = new
            {
                capturedUtc = census.CapturedUtc,
                login = census.Login, loading = census.Roles.Count(s => s.Loading),
                inWorld = census.Roles.Count(s => !s.Loading),
                byMap = census.Roles.GroupBy(s => s.Map).Select(g => new
                {
                    map = g.Key.SceneEntry, line = g.Key.LineNumber,
                    kind = g.Key.Kind.ToString(), runtimeId = g.Key.Id, sceneId = g.Key.WorldScene,
                    count = g.Count(), loading = g.Count(s => s.Loading)
                }).ToArray(),
                publicLines = publicLines == null ? null : census.Roles.Select(s => s.Map.SceneEntry)
                    .Distinct().SelectMany(publicLines.Snapshot).Select(l => new
                    {
                        map = l.Map.SceneEntry,
                        line = l.Map.LineNumber, sceneId = l.Map.WorldScene, players = l.Players,
                        reservations = l.Reservations, softLimit = l.SoftLimit, hardLimit = l.HardLimit
                    }).ToArray(),
                lineSemantics = "byMap is approximate registered-session locality, including provisional/loading roles; line is public line number (private/event line is 1), kind + runtimeId distinguish rooms; sceneId is RuntimeMap.WorldScene. publicLines is the admission manager's occupied players + reservations, used for capacity checks",
            },
            runtime = new
            {
                gen0 = GC.CollectionCount(0) - e.Gen0, gen1 = GC.CollectionCount(1) - e.Gen1, gen2 = GC.CollectionCount(2) - e.Gen2,
                pauseMs = (GC.GetTotalPauseDuration() - e.Pause).TotalMilliseconds, pauseDurationMs = e.PauseDurationMs.Snapshot(),
                allocatedBytes = GC.GetTotalAllocatedBytes() - e.Allocated,
                allocationBytesPerSecond = (GC.GetTotalAllocatedBytes() - e.Allocated) / Math.Max(elapsed, .001),
                workingSetBytes = process.WorkingSet64, threadPoolQueueLength = ThreadPool.PendingWorkItemCount,
                processCpuSeconds = (process.TotalProcessorTime - e.Cpu).TotalSeconds,
            },
            errors = new { exceptions = Volatile.Read(ref e.Exceptions), errorLogs = Volatile.Read(ref e.ErrorLogs) },
        };
    }

    internal sealed class Epoch
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly long StartedAt = Stopwatch.GetTimestamp(), Allocated = GC.GetTotalAllocatedBytes();
        public readonly int Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2);
        public readonly TimeSpan Pause = GC.GetTotalPauseDuration(), Cpu = ReadCpu();
        private static TimeSpan ReadCpu() { using var p = Process.GetCurrentProcess(); return p.TotalProcessorTime; }
        public readonly FixedHistogram TickDurationMs = new(), CadenceMs = new(), QueueLength = new(), QueueAgeMs = new(),
            ItemDurationMs = new(), ItemsPerTick = new(), TimerLatenessMs = new(), TimerDurationMs = new(), TickerDurationMs = new(),
            PauseDurationMs = new(), CommitStatementMs = new();
        public readonly ConcurrentDictionary<uint, InboundMetrics> Inbound = new();
        public readonly ConcurrentDictionary<uint, OutboundMetrics> Outbound = new();
        public readonly ConcurrentDictionary<string, DatabaseMetrics> Database = new();
        public readonly ConcurrentDictionary<string, FixedHistogram> Tickers = new();
        public readonly ConcurrentDictionary<string, FixedHistogram> SlowItems = new();
        public readonly ConcurrentDictionary<string, long> DbCallSites = new();
        public long BusyTicks, Overruns, FramesIn, FramesOut, SocketSends, BytesIn, BytesOut, PartialSends, WriterErrors, Commits, Exceptions, ErrorLogs, DatabaseProfileErrors;
        public int QueueLengthCurrent;
        public InboundMetrics Message(uint id) => Inbound.TryGetValue(id, out var found) ? found : Inbound.GetOrAdd(Inbound.Count < 4096 ? id : uint.MaxValue, static _ => new());
    }
    internal sealed class InboundMetrics
    {
        public long Count, Completed, Failed;
        public readonly FixedHistogram HandlingMs = new(), WireReplyMs = new();
    }
    internal sealed class OutboundMetrics { public long Count, Bytes; }
    internal sealed class DatabaseMetrics
    {
        public long Count, LoopCount, OtherCount, Commits, LoopCommits;
        public readonly FixedHistogram DurationMs = new(), LoopDurationMs = new(), OtherDurationMs = new();
        public readonly ConcurrentDictionary<int, ThreadCounter> Threads = new();
    }
    internal sealed class ThreadCounter { public long Count; }

    private sealed class RuntimeEvents : EventListener
    {
        // Runtime events are process-wide. Repeated Enable/Dispose per host can deadlock .NET's EventPipe
        // dispatcher during concurrent host construction/disposal. One listener lives for the process;
        // hosts only register/remove cheap recipients, without reconfiguring the runtime provider.
        internal static readonly Lazy<RuntimeEvents> Shared = new(static () => new RuntimeEvents());
        private readonly ConcurrentDictionary<long, ServerMetrics> _owners = new();
        private long _nextOwner;
        private long _suspended;
        private int _enabled;
        public long Register(ServerMetrics owner)
        {
            long id = Interlocked.Increment(ref _nextOwner);
            _owners[id] = owner;
            return id;
        }
        public void Unregister(long id) => _owners.TryRemove(id, out _);
        protected override void OnEventSourceCreated(EventSource source)
        {
            // EventListener calls this virtual method while holding its source lock (including in its base
            // constructor). Enable outside that lock to avoid the EventSource/EventPipe lock-order inversion.
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                ThreadPool.UnsafeQueueUserWorkItem(static (EventSource runtime) => Shared.Value.EnableRuntime(runtime), source, false);
        }
        private void EnableRuntime(EventSource source)
        {
            if (Interlocked.Exchange(ref _enabled, 1) == 0)
                EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventName is "GCSuspendEEBegin_V1" or "GCSuspendEEBegin") Interlocked.Exchange(ref _suspended, data.TimeStamp.Ticks);
            else if (data.EventName is "GCRestartEEEnd_V1" or "GCRestartEEEnd")
            {
                long start = Interlocked.Exchange(ref _suspended, 0);
                if (start > 0 && data.TimeStamp.Ticks >= start)
                    foreach (var owner in _owners)
                        owner.Value.Current.PauseDurationMs.Record((data.TimeStamp.Ticks - start) / 10000.0);
            }
        }
    }
}

public readonly struct DatabaseOpenMeasurement(ServerMetrics metrics) : IDisposable
{
    private readonly long _start = Stopwatch.GetTimestamp();
    public void Dispose() => metrics.RecordDatabase("connection/OPEN", _start, ReferenceEquals(ServerMetrics.LoopOwner, metrics), Environment.CurrentManagedThreadId, false);
}

public sealed class RequestMeasurement : IDisposable
{
    private readonly RequestMeasurement? _previous;
    private readonly ServerMetrics.InboundMetrics _counter;
    private readonly long _handlingAt = Stopwatch.GetTimestamp();
    internal readonly ConnectionMetrics? Connection;
    internal readonly uint Id;
    internal readonly long ReceivedAt;
    internal int ReplyStamped;
    internal bool Active = true;
    private bool _completed;
    internal RequestMeasurement(ServerMetrics owner, ConnectionMetrics? connection, uint id, long receivedAt)
    {
        Connection = connection; Id = id; ReceivedAt = receivedAt;
        _counter = owner.Current.Message(id);
        Interlocked.Increment(ref _counter.Count);
        _previous = ServerMetrics.Request.Value;
        ServerMetrics.Request.Value = this;
    }
    public void Complete() => _completed = true;
    public void Dispose()
    {
        Active = false;
        _counter.HandlingMs.RecordSince(_handlingAt);
        if (_completed) Interlocked.Increment(ref _counter.Completed); else Interlocked.Increment(ref _counter.Failed);
        ServerMetrics.Request.Value = _previous;
    }
}

/// <summary>Counts existing error logs without formatting them or changing their destinations.</summary>
public sealed class MetricsLoggerProvider(ServerMetrics metrics) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CounterLogger(metrics);
    public void Dispose() { }
    private sealed class CounterLogger(ServerMetrics owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            Interlocked.Increment(ref owner.Current.ErrorLogs);
            if (exception != null) Interlocked.Increment(ref owner.Current.Exceptions);
        }
    }
}
