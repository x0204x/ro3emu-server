using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Diagnostics;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One connection and one dedicated thread. Admission accounts for both queued and executing plans.</summary>
public sealed class DbWriter : IDisposable
{
    public const int MaxDurableJobs = 1024, MaxDurableBytes = 4 * 1024 * 1024, MaxLazyBytes = 4 * 1024 * 1024;
    private const int MaxLazyJobs = 1024;
    private readonly object _gate = new();
    private readonly Queue<Job> _durable = new(), _lazy = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _connectionString;
    private readonly ServerMetrics? _serverMetrics;
    private readonly DbAccessMetrics _metrics;
    private readonly ILogger? _log;
    private readonly TimeSpan _stopTimeout;
    private Job? _active;
    private int _durableJobs, _durableBytes, _lazyJobs, _lazyBytes;
    private bool _stopping, _lastWasOverdueLazy;
    private Exception? _terminalError;
    /// <summary>C/D 19 fairness: a lazy job older than this alternates with durable work instead of waiting for an
    /// idle writer, so sustained durable load cannot starve lazy parts (position/settings).</summary>
    internal TimeSpan LazyMaxWait { get; set; } = TimeSpan.FromSeconds(2);

    private sealed record Job(DbPlan Plan, bool Lazy, TaskCompletionSource<object?> Done, long EnqueuedAt);

    public DbWriter(string connectionString, DbAccessMetrics metrics, ServerMetrics? serverMetrics = null, ILogger? log = null,
        TimeSpan? stopTimeout = null)
    {
        _connectionString = connectionString;
        _metrics = metrics;
        _serverMetrics = serverMetrics;
        _log = log;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _thread = new Thread(Run) { IsBackground = true, Name = "ro3-db-writer" };
        using (ExecutionContext.SuppressFlow()) _thread.Start();
    }

    public Task WorkerExited => _exited.Task;
    // Test-only deterministic barrier after SQL/revision work and before FULL commit.
    internal Action<string>? BeforeCommitForTest { get; set; }
    internal Action<string>? AfterCommitForTest { get; set; }
    /// <summary>Runs on this thread right after COMMIT with the role parts the plan declares (snapshot invalidation).</summary>
    internal Action<IReadOnlyList<RoleRevisionKey>>? RoleWritesCommitted { get; set; }
    internal bool StopRequested { get { lock (_gate) return _stopping; } }

    public Task<T> ExecuteAsync<T>(DbPlan plan, bool lazy = false) => Cast<T>(Enqueue(plan, lazy));

    private Task<object?> Enqueue(DbPlan plan, bool lazy)
    {
        if (plan.Bytes < 0) throw new ArgumentOutOfRangeException(nameof(plan));
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping)
            {
                _metrics.WriterRefused();
                return Task.FromException<object?>(_terminalError is { } error
                    ? new InvalidOperationException("Database writer failed and cannot admit more work.", error)
                    : new ObjectDisposedException(nameof(DbWriter)));
            }
            if (lazy ? _lazyJobs >= MaxLazyJobs || (long)_lazyBytes + plan.Bytes > MaxLazyBytes
                : _durableJobs >= MaxDurableJobs || (long)_durableBytes + plan.Bytes > MaxDurableBytes)
            {
                _metrics.WriterRefused();
                return Task.FromException<object?>(new DbQueueFullException("Database writer admission closed or full."));
            }
            if (lazy) { _lazyJobs++; _lazyBytes += plan.Bytes; _lazy.Enqueue(new(plan, true, done, Stopwatch.GetTimestamp())); }
            else { _durableJobs++; _durableBytes += plan.Bytes; _durable.Enqueue(new(plan, false, done, Stopwatch.GetTimestamp())); }
            _metrics.WriterAdmitted(lazy, plan.Bytes);
            Monitor.Pulse(_gate);
        }
        return done.Task;
    }

    private static async Task<T> Cast<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;

    // Caches a plan reports stale while it executes on this writer thread; this writer's database bumps them when the
    // job ends, committed or not (an extra invalidation is harmless), before the job's completion reaches its caller.
    [ThreadStatic] private static List<WrittenCache>? _afterJob;
    /// <summary>For a plan's Execute: the job writes rows behind <paramref name="cache"/>.</summary>
    internal static void AfterJob(WrittenCache cache) => (_afterJob ??= []).Add(cache);

    /// <summary>The cache generations of this writer's database (its <see cref="AccountStore"/>).</summary>
    public CacheGenerations Caches { get; init; } = new();

    public T ExecuteLegacySync<T>(DbPlan plan, bool onLoop)
    {
        long started = Stopwatch.GetTimestamp();
        // The blocked caller observes worker completion directly; async continuations stay asynchronous.
        try { return (T)Enqueue(plan, lazy: false).GetAwaiter().GetResult()!; }
        finally { _metrics.LegacyWait(started, onLoop, writer: true, label: plan.Label); }
    }

    private void Run()
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Pooling = false }.ToString());
            using (var measurement = _serverMetrics?.MeasureDatabaseOpen()) db.Open();
            SqliteMetrics.Attach(db, _serverMetrics);
            DbWriteGuard.AttachWriter(db, _metrics);
            using (var full = db.CreateCommand()) { full.CommandText = "PRAGMA synchronous=FULL"; full.ExecuteNonQuery(); }
            _metrics.WriterThread(Environment.CurrentManagedThreadId);
            while (true)
            {
                Job first;
                lock (_gate)
                {
                    while (_durable.Count == 0 && _lazy.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    if (_durable.Count == 0 && _lazy.Count == 0) break;
                    first = NextJob();
                    _active = first;
                }
                RunGroup(db, first);
            }
        }
        catch (Exception error)
        {
            _metrics.WriterTerminal();
            _log?.LogCritical(error, "Database writer worker failed; queued plans will fail and no write is admitted");
            lock (_gate)
            {
                _stopping = true; _terminalError = error;
                foreach (var job in _durable.Concat(_lazy))
                {
                    job.Done.TrySetException(error);
                    _metrics.WriterFinished(job.Lazy, job.Plan.Bytes, false);
                }
                _durable.Clear(); _lazy.Clear();
                _durableJobs = _durableBytes = _lazyJobs = _lazyBytes = 0;
            }
        }
        finally { _exited.TrySetResult(); }
    }

    // Stage G group commit: jobs already queued behind the first one join its transaction, each inside its own
    // savepoint (a failing job rolls back only itself), and one FULL commit makes the group durable. Every job's
    // completion is still released only after that commit, so durability and per-job outcomes are unchanged; jobs
    // execute in the same order as before, each seeing the earlier ones' writes. On the realm's disk a commit was
    // about 6 ms and a new role's login a dozen of them, one after another: the writer queue, not the loop, was what
    // a login storm waited on.
    internal const int MaxGroupJobs = 64;
    internal TimeSpan MaxGroupExecution { get; set; } = TimeSpan.FromMilliseconds(25);

    private sealed class Pending(Job job)
    {
        public readonly Job Job = job;
        public object? Result;
        public Exception? Error;
        public bool ReplayValidated, Executed;
        public List<WrittenCache>? AfterJob;
    }

    private void RunGroup(SqliteConnection db, Job first)
    {
        var group = new List<Pending>();
        Exception? groupError = null;
        long groupStarted = Stopwatch.GetTimestamp();
        SqliteTransaction? tx = null;
        try
        {
            tx = db.BeginTransaction();
            var job = first;
            while (true)
            {
                var pending = new Pending(job);
                group.Add(pending);
                _metrics.WriterDequeued(job.EnqueuedAt);
                if (!Execute(db, tx, pending, "g" + group.Count, out groupError)) break;
                lock (_gate)
                {
                    if (group.Count >= MaxGroupJobs || Stopwatch.GetElapsedTime(groupStarted) > MaxGroupExecution
                        || _durable.Count == 0 && _lazy.Count == 0) break;
                    job = NextJob();
                    _active = job;
                }
            }
            if (groupError == null)
            {
                int executed = group.Count(p => p.Executed);
                if (executed == 0) tx.Rollback();
                else { tx.Commit(); _metrics.WriterCommitted(executed); }
            }
        }
        catch (Exception e)
        {
            groupError ??= e;
            if (group.Count == 0) group.Add(new Pending(first)); // BEGIN itself failed: the job still completes
        }
        finally { tx?.Dispose(); }
        foreach (var pending in group) Finish(db, pending, groupError);
    }

    /// <summary>One job inside the group's transaction. False when the transaction itself is lost (a failed
    /// savepoint rollback): the group then fails as a whole.</summary>
    private bool Execute(SqliteConnection db, SqliteTransaction tx, Pending pending, string savepoint, out Exception? groupError)
    {
        groupError = null;
        var plan = pending.Job.Plan;
        long executionStarted = Stopwatch.GetTimestamp();
        bool open = false;
        try
        {
            DbWriteGuard.Plan(db, plan);
            tx.Save(savepoint);
            open = true;
            // An economy envelope replays its own receipt; other owned operations replay the generic one.
            bool replay = plan.TryReplay(db, tx, out pending.Result);
            pending.ReplayValidated = true;
            if (!replay
                && (plan.OwnsOperationReceipt || plan.OperationId is not { } id ||
                    !OperationReceipts.TryRead(db, tx, id, out pending.Result)))
            {
                var deltaRevisions = RevisionStore.Check(db, tx, plan);
                pending.Result = plan.Execute(db, tx);
                RevisionStore.Bump(db, tx, plan, pending.Result, deltaRevisions);
                if (plan.OperationId != null && !plan.OwnsOperationReceipt)
                    OperationReceipts.Save(db, tx, plan, pending.Result);
            }
            DbWriteGuard.AssertWriter(db);
            BeforeCommitForTest?.Invoke(plan.Label);
            tx.Release(savepoint);
            open = false;
            pending.Executed = true;
        }
        catch (Exception caught)
        {
            if (caught is OwnerRevisionConflictException) _metrics.RevisionConflict();
            pending.Error = caught;
            if (open)
            {
                try { tx.Rollback(savepoint); tx.Release(savepoint); }
                catch (Exception lost) { groupError = lost; }
            }
        }
        finally
        {
            // Invalidations a plan registered run after the group's commit (or its rollback), before its completion.
            if (_afterJob is { Count: > 0 } afterJob) { pending.AfterJob = [.. afterJob]; afterJob.Clear(); }
            _metrics.WriterExecuted(Stopwatch.GetTimestamp() - executionStarted);
            DbWriteGuard.Plan(db, "writer/idle");
        }
        return groupError == null;
    }

    private void Finish(SqliteConnection db, Pending pending, Exception? groupError)
    {
        var job = pending.Job;
        bool success = false;
        try
        {
            // An executed job of a group that did not commit failed with the group.
            if (pending.Error == null && groupError != null) pending.Error = groupError;
            if (pending.Error == null)
            {
                if (job.Plan.RoleWrites.Count != 0) InvalidateCommitted(job.Plan);
                try
                {
                    if (job.Plan.OperationId != null) AfterCommitForTest?.Invoke(job.Plan.Label);
                    success = true;
                    job.Plan.AfterCommit();
                }
                catch (Exception caught) { success = false; pending.Error = caught; }
            }
            if (pending.Error is { } caughtError && pending.ReplayValidated && !job.Plan.OwnsOperationReceipt &&
                caughtError is not Economy.EconomyOperationConflictException &&
                !(job.Plan is EventEconomyTransactionPlan && caughtError is InvalidDataException) &&
                job.Plan.OperationId is { } operationId)
            {
                try
                {
                    // Resolve from what is committed, on a fresh read-only connection of this worker, before
                    // releasing the accepted job's completion.
                    using var recovery = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString)
                    { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    recovery.Open();
                    DbWriteGuard.AttachRead(recovery, _metrics, "writer/resolve-operation");
                    if (OperationReceipts.TryRead(recovery, null, operationId, out var receipt))
                    { pending.Result = receipt; pending.Error = null; success = true; }
                }
                catch (Exception recoveryError) { pending.Error = new OwnerOutcomeUnknownException(operationId, recoveryError); }
            }
        }
        finally
        {
            if (pending.AfterJob is { } afterJob)
                foreach (var cache in afterJob) Caches.Bump(cache);
            lock (_gate)
            {
                if (ReferenceEquals(_active, job)) _active = null;
                if (job.Lazy) { _lazyJobs--; _lazyBytes -= job.Plan.Bytes; }
                else { _durableJobs--; _durableBytes -= job.Plan.Bytes; }
            }
            _metrics.WriterFinished(job.Lazy, job.Plan.Bytes, success);
        }
        if (pending.Error is null) job.Done.TrySetResult(pending.Result);
        else job.Done.TrySetException(pending.Error);
    }

    /// <summary>
    /// The writer's own post-commit step (snapshot invalidation). The write is durable once COMMIT returned, so a
    /// failure here must not report it as failed - its caller would retry an applied write. Plan-defined steps
    /// (<see cref="DbPlan.AfterCommit"/>) keep their semantics: receipt-backed plans use them as a lost acknowledgement.
    /// </summary>
    private void InvalidateCommitted(DbPlan plan)
    {
        try { RoleWritesCommitted?.Invoke(plan.RoleWrites); }
        catch (Exception e)
        {
            _metrics.PostCommitFailure();
            _log?.LogCritical(e, "Snapshot invalidation after the commit of {Label} failed", plan.Label);
        }
    }

    /// <summary>Durable first; an overdue lazy job alternates with durable ones (never two overdue in a row while
    /// durable work waits). Called under the gate with at least one job queued.</summary>
    private Job NextJob()
    {
        if (_durable.Count == 0) { _lastWasOverdueLazy = false; return _lazy.Dequeue(); }
        if (_lazy.Count != 0 && !_lastWasOverdueLazy && Stopwatch.GetElapsedTime(_lazy.Peek().EnqueuedAt) > LazyMaxWait)
        {
            _lastWasOverdueLazy = true;
            _metrics.LazyPromoted();
            return _lazy.Dequeue();
        }
        _lastWasOverdueLazy = false;
        return _durable.Dequeue();
    }

    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
        // The worker closes its own connection after accepted work. The caller never closes an in-use handle.
        if (Thread.CurrentThread != _thread && !_exited.Task.Wait(_stopTimeout))
        {
            _metrics.WorkerStopTimedOut(writer: true);
            (int durableJobs, int durableBytes, int lazyJobs, int lazyBytes, string labels) debt;
            lock (_gate)
                debt = (_durableJobs, _durableBytes, _lazyJobs, _lazyBytes,
                    string.Join(",", (_active is null ? Enumerable.Empty<DbPlan>() : new[] { _active.Plan })
                        .Concat(_durable.Select(x => x.Plan)).Concat(_lazy.Select(x => x.Plan))
                        .Select(x => x.Label.Length > 64 ? x.Label[..64] : x.Label).Distinct().Take(8)));
            _log?.LogError("Database writer shutdown timed out with durableJobs={DurableJobs}, durableBytes={DurableBytes}, lazyJobs={LazyJobs}, lazyBytes={LazyBytes}, labels={Labels}; worker retains its connection until accepted plans finish",
                debt.durableJobs, debt.durableBytes, debt.lazyJobs, debt.lazyBytes, debt.labels);
        }
    }
}

public sealed class DbQueueFullException(string message) : InvalidOperationException(message);
