using System.Collections.Concurrent;
using System.Diagnostics;
using Ro3Emu.Server.Diagnostics;

namespace Ro3Emu.Server.Game;

/// <summary>Something advanced by the game loop's clock (map simulation, regeneration...).</summary>
public interface IGameTicker
{
    void Tick(GameTime time);
}

/// <summary>A tick: monotonic time since the loop started and the step since the previous tick.</summary>
public readonly record struct GameTime(TimeSpan Now, TimeSpan Delta);

public enum GameLoopPhase { Accepting, Quiescing, Draining, Stopped }

public sealed record GameLoopDrainDebt(long Id, string Name, TimeSpan Age, int PendingCallbacks);

public sealed record GameLoopDrainStatus(GameLoopPhase Phase, int Outstanding, int Queued,
    long Rejected, bool Clean, IReadOnlyList<GameLoopDrainDebt> Debt, IReadOnlyList<string> FailedObligations);

/// <summary>
/// The one thread all game logic runs on. Network readers parse messages and hand them over with
/// <see cref="InvokeAsync(Func{Task})"/>; the loop runs them in arrival order and advances every
/// <see cref="IGameTicker"/> every <see cref="TickInterval"/>. A synchronization context keeps <c>await</c>
/// continuations on this thread, so game state needs no locks. Sends to clients are queued per connection
/// (PhonestChannel) and never block the loop.
/// </summary>
public sealed partial class GameLoop : IHostedService, IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State, long EnqueuedAt, WorkPermit Permit)> _queue = [];
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly ServerMetrics? _metrics;
    private readonly List<IGameTicker> _tickers = [];
    private readonly PriorityQueue<Func<Task>, TimeSpan> _timers = new();
    private readonly Stopwatch _clock = new();
    private readonly ILogger<GameLoop> _log;
    private readonly Thread _thread;
    private readonly LoopContext _context;
    private readonly TaskCompletionSource _workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new();
    private readonly Dictionary<long, WorkPermit> _outstanding = [];
    private readonly List<string> _failedObligations = [];
    private readonly AsyncLocal<WorkPermit?> _currentPermit = new();
    private long _nextOperationId, _rejected;
    private const int MaxOrdinaryWork = 4096;
    private const int MaxCleanupWork = 4096;
    private int _ordinaryWork, _cleanupWork;
    private GameLoopPhase _phase = GameLoopPhase.Accepting;
    private bool _drainFailed;
    private long _orphanContinuations;
    private readonly HashSet<string> _orphanSites = [];
    /// <summary>Never closes and is never counted as outstanding work.</summary>
    private WorkPermit _orphan = null!;
    private bool _started;
    private bool _disposeRequested;
    private bool _queueDisposed;
    private TimeSpan _lastTick;

    public GameLoop(ILogger<GameLoop> log, ServerMetrics? metrics = null)
    {
        _log = log;
        _metrics = metrics;
        _orphan = new WorkPermit(this, 0, "orphan continuation", false, 0) { RefCount = int.MaxValue / 2 };
        _context = new LoopContext(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "game-loop" };
    }

    /// <summary>Monotonic game time.</summary>
    public TimeSpan Now => _clock.Elapsed;

    internal Ownership.OwnerRegistry? Owners { get; set; }
    // Actual executing callback capability, not an AsyncLocal inherited by detached work.
    internal WorkPermit? CurrentWork => IsOnLoop && SynchronizationContext.Current is LoopContext context ? context.Permit : null;

    public bool IsOnLoop => Thread.CurrentThread == _thread;
    public Task WorkerExited => _workerExited.Task;
    public bool IsWorkerActive => _started && !_workerExited.Task.IsCompleted;

    public GameLoopPhase Phase { get { lock (_lifecycle) return _phase; } }

    public GameLoopDrainStatus DrainStatus
    {
        get
        {
            lock (_lifecycle)
                return new(_phase, _outstanding.Count, _queueDisposed ? 0 : _queue.Count, _rejected,
                    _phase == GameLoopPhase.Stopped && !_drainFailed,
                    [.. _outstanding.Values.Select(p => new GameLoopDrainDebt(p.Id, p.Name,
                        Stopwatch.GetElapsedTime(p.StartedAt), p.PendingCallbacks))],
                    [.. _failedObligations]);
        }
    }

    /// <summary>Registers a ticker (from the loop or before start).</summary>
    public void Add(IGameTicker ticker)
    {
        if (_thread.IsAlive && !IsOnLoop) Post(() => { _tickers.Add(ticker); return Task.CompletedTask; });
        else _tickers.Add(ticker);
    }

    public void Remove(IGameTicker ticker) => TryRemove(ticker, null);

    /// <summary>Remove a ticker and run its disposal on the loop. Once shutdown has stopped
    /// ticking, the caller can defer disposal until WorkerExited instead of mutating live state.</summary>
    internal bool TryRemove(IGameTicker ticker, Action? onRemoved)
    {
        if (_thread.IsAlive && !IsOnLoop)
        {
            lock (_lifecycle)
            {
                // A stopped loop never ticks this list again. Test phase and ordinary admission
                // together so shutdown cannot begin between them.
                if (_phase != GameLoopPhase.Accepting) return false;
                Post(() => { _tickers.Remove(ticker); onRemoved?.Invoke(); return Task.CompletedTask; });
                return true;
            }
        }
        _tickers.Remove(ticker);
        onRemoved?.Invoke();
        return true;
    }

    /// <summary>Runs work on the loop after a delay (hit moments, corpse removal, respawns). Call from the loop.</summary>
    public void Schedule(TimeSpan delay, Func<Task> work)
    {
        if (Phase != GameLoopPhase.Accepting) return;
        if (IsOnLoop || !_thread.IsAlive) _timers.Enqueue(work, Now + delay);
        else Post(() => { _timers.Enqueue(work, Now + delay); return Task.CompletedTask; });
    }

    /// <summary>Queues work on the loop without waiting for it.</summary>
    public void Post(Func<Task> work)
    {
        if (!TryAdmit("post", cleanup: false, out var permit))
            throw new InvalidOperationException("Game loop ordinary work admission is closed or full.");
        EnqueueAdmitted(_ => _ = RunGuarded(work, permit), null, permit);
    }

    /// <summary>Rejects new ordinary work while retaining already admitted continuations.</summary>
    public void Quiesce()
    {
        lock (_lifecycle)
        {
            if (_phase == GameLoopPhase.Accepting) _phase = GameLoopPhase.Quiescing;
            _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, _drainFailed);
            if (!_queueDisposed) _workAvailable.Set();
        }
    }

    public void ReportCleanupFailure(string obligation)
    {
        lock (_lifecycle)
        {
            _drainFailed = true;
            if (_failedObligations.Count < 64) _failedObligations.Add(obligation);
            _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, true);
        }
    }

    private bool TryAdmit(string name, bool cleanup, out WorkPermit permit)
    {
        lock (_lifecycle)
        {
            var parent = _currentPermit.Value;
            bool continuation = IsOnLoop && parent != null && parent.IsOpen && ReferenceEquals(parent.Owner, this);
            if (_phase == GameLoopPhase.Stopped ||
                (cleanup && _phase != GameLoopPhase.Accepting) ||
                (!cleanup && !continuation && _phase != GameLoopPhase.Accepting) ||
                (cleanup ? _cleanupWork >= MaxCleanupWork : !continuation && _ordinaryWork >= MaxOrdinaryWork))
            {
                _rejected++;
                _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, _drainFailed);
                permit = null!;
                return false;
            }
            permit = new WorkPermit(this, ++_nextOperationId, name, cleanup, Stopwatch.GetTimestamp());
            permit.OrdinarySlot = !cleanup && !continuation;
            if (continuation)
            {
                permit.Parent = parent;
                parent!.RefCount++; // the original admission stays charged until detached follow-ups finish
            }
            _outstanding.Add(permit.Id, permit);
            if (cleanup) _cleanupWork++; else if (permit.OrdinarySlot) _ordinaryWork++;
            _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, _drainFailed);
            return true;
        }
    }

    /// <summary>
    /// An await continuation. Its permit can already be closed when untracked work (a dropped task started from a
    /// ticker or handler) resumes late; throwing here would run on a socket or timer thread and kill the process,
    /// so the continuation still runs on the loop under an unaccounted orphan permit and the call site is reported.
    /// </summary>
    private void EnqueueContinuation(SendOrPostCallback callback, object? state, WorkPermit? permit)
    {
        lock (_lifecycle)
        {
            if (permit != null && permit.IsOpen && _phase != GameLoopPhase.Stopped)
            {
                permit.PendingCallbacks++;
                _queue.Add((callback, state, Stopwatch.GetTimestamp(), permit));
                _workAvailable.Set();
                return;
            }
            _orphanContinuations++;
            string site = Describe(callback, state);
            bool first = _orphanSites.Add(site);
            if (_phase == GameLoopPhase.Stopped)
            {
                _log.LogError("Game loop continuation {Site} arrived after the loop stopped and is dropped", site);
                return;
            }
            if (first) _log.LogWarning("Untracked game loop continuation {Site} (permit {Permit}) runs as an orphan; track its root", site, permit?.Name ?? "none");
            _orphan.PendingCallbacks++;
            _queue.Add((callback, state, Stopwatch.GetTimestamp(), _orphan));
            _workAvailable.Set();
        }
    }

    /// <summary>Continuations that resumed after their permit closed (see <see cref="EnqueueContinuation"/>).</summary>
    public long OrphanContinuations { get { lock (_lifecycle) return _orphanContinuations; } }

    private void EnqueueAdmitted(SendOrPostCallback callback, object? state, WorkPermit permit)
    {
        lock (_lifecycle)
        {
            if (_phase == GameLoopPhase.Stopped || !permit.IsOpen)
                throw new InvalidOperationException($"A game loop continuation {Describe(callback, state)} lost permit {permit.Id}:{permit.Name} in {_phase}.");
            permit.PendingCallbacks++;
            _queue.Add((callback, state, Stopwatch.GetTimestamp(), permit));
            _workAvailable.Set();
        }
    }

    /// <summary>Runs work on the loop and completes when it has finished (inline when already on the loop).</summary>
    public Task InvokeAsync(Func<Task> work) => InvokeAsync(async () => { await work(); return true; });

    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        if (IsOnLoop)
        {
            if (Phase != GameLoopPhase.Accepting && _currentPermit.Value?.IsOpen != true)
                return Task.FromCanceled<T>(new CancellationToken(true));
            return TrackAsync("invoke inline", work);
        }
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryAdmit("invoke", cleanup: false, out var permit)) done.TrySetCanceled();
        else EnqueueAdmitted(_ => _ = Complete(work, done, permit), null, permit);
        return done.Task;
    }

    /// <summary>Reserve a connection's final unlink before its serve task can outlive ordinary admission.</summary>
    public CompletionReservation? ReserveCompletion(string name)
        => TryAdmit(name, cleanup: true, out var permit) ? new CompletionReservation(this, permit) : null;

    public Task InvokeCleanupAsync(CompletionReservation reservation, Func<Task> work)
    {
        var permit = reservation.Permit;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (!ReferenceEquals(permit.Owner, this) || !permit.IsOpen || _phase == GameLoopPhase.Stopped)
                throw new InvalidOperationException("Connection cleanup has no live completion reservation.");
            permit.RefCount++;
        }
        EnqueueAdmitted(_ => _ = CompleteCleanup(work, done, permit), null, permit);
        return done.Task;
    }

    /// <summary>Counts a real asynchronous room handover, spawn or removal through its final continuation.</summary>
    public Task<T> TrackAsync<T>(string name, Func<Task<T>> work)
    {
        if (!IsOnLoop) throw new InvalidOperationException("Tracked game work must start on the loop.");
        if (Phase != GameLoopPhase.Accepting && _currentPermit.Value?.IsOpen != true)
            return Task.FromCanceled<T>(new CancellationToken(true));
        if (!TryAdmit(name, cleanup: false, out var permit)) return Task.FromCanceled<T>(new CancellationToken(true));
        // Explicitly awaited nested work; detached work gets a fresh capability.
        permit.Ownership = CurrentWork?.Ownership;
        return StartTracked(work, permit);
    }

    public Task TrackAsync(string name, Func<Task> work) => TrackAsync(name, async () => { await work(); return true; });

    /// <summary>Starts detached ticker work in the current turn while retaining its async completion.</summary>
    public void TrackDetached(string name, Func<Task> work)
    {
        if (!IsOnLoop) throw new InvalidOperationException("Detached game work must start on the loop.");
        if (!TryAdmit(name, cleanup: false, out var permit))
        {
            ReportCleanupFailure($"detached work admission refused: {name}");
            _log.LogError("Detached game loop work {Name} was refused admission", name);
            return;
        }
        _ = StartTracked(async () =>
        {
            try { await work(); }
            catch (Exception e) { _log.LogError(e, "Detached game loop work {Name} failed", name); }
            return true;
        }, permit);
    }

    private Task<T> StartTracked<T>(Func<Task<T>> work, WorkPermit permit)
    {
        var previous = SynchronizationContext.Current;
        var priorPermit = _currentPermit.Value;
        SynchronizationContext.SetSynchronizationContext(new LoopContext(this, permit));
        _currentPermit.Value = permit;
        try { return FinishTracked(work, permit); }
        finally { _currentPermit.Value = priorPermit; SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private async Task<T> FinishTracked<T>(Func<Task<T>> work, WorkPermit permit)
    {
        try { return await work(); }
        finally { Release(permit); }
    }

    private async Task Complete<T>(Func<Task<T>> work, TaskCompletionSource<T> done, WorkPermit permit)
    {
        try { done.TrySetResult(await work()); }
        catch (Exception e) { done.TrySetException(e); }
        finally { Release(permit); }
    }

    private async Task CompleteCleanup(Func<Task> work, TaskCompletionSource done, WorkPermit permit)
    {
        try { await work(); done.TrySetResult(); }
        catch (Exception e) { done.TrySetException(e); }
        finally { Release(permit); }
    }

    private async Task RunGuarded(Func<Task> work, WorkPermit permit)
    {
        try { await work(); }
        catch (Exception e) { _log.LogError(e, "Game loop work failed"); }
        finally { Release(permit); }
    }

    private void Release(WorkPermit permit)
    {
        lock (_lifecycle)
        {
            if (!permit.IsOpen || --permit.RefCount < 0) throw new InvalidOperationException("Game loop completion released twice.");
            if (permit.RefCount != 0) return;
            permit.IsOpen = false;
            _outstanding.Remove(permit.Id);
            if (permit.Cleanup) _cleanupWork--;
            else if (permit.OrdinarySlot) _ordinaryWork--;
            _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, _drainFailed);
            if (permit.Parent is { } parent)
            {
                Release(parent);
            }
            TryFinishDrain();
        }
    }

    private void TryFinishDrain()
    {
        if (_phase == GameLoopPhase.Draining && _outstanding.Count == 0 && _queue.Count == 0)
        {
            _phase = GameLoopPhase.Stopped;
            _queue.CompleteAdding();
            _workAvailable.Set();
            _metrics?.RecordLoopLifecycle((int)_phase, 0, _rejected, _drainFailed);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_phase != GameLoopPhase.Accepting) throw new InvalidOperationException("The game loop has stopped.");
            if (_started) throw new InvalidOperationException("The game loop has already started.");
            _clock.Start();
            _thread.Start();
            _started = true;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        bool started;
        lock (_lifecycle)
        {
            if (_phase is GameLoopPhase.Accepting or GameLoopPhase.Quiescing)
            {
                _phase = GameLoopPhase.Draining;
                _workAvailable.Set();
                TryFinishDrain();
                _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, _drainFailed);
            }
            started = _started;
            if (!started && _outstanding.Count > 0 && _phase != GameLoopPhase.Stopped)
            {
                _drainFailed = true;
                _failedObligations.Add("unstarted loop stopped with admitted work");
                _phase = GameLoopPhase.Stopped;
                _queue.CompleteAdding();
                _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, true);
            }
        }
        if (!started)
        {
            if (DrainStatus.Outstanding > 0)
                _log.LogError("Unstarted game loop stopped with {Count} admitted operations; drain failed", DrainStatus.Outstanding);
            return;
        }
        if (IsOnLoop) return;
        try { await _workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            lock (_lifecycle)
            {
                _drainFailed = true;
                _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, true);
            }
            var debt = DrainStatus;
            _log.LogError("Game loop drain incomplete: phase={Phase}, outstanding={Outstanding}, queued={Queued}, debt={Debt}",
                debt.Phase, debt.Outstanding, debt.Queued,
                string.Join(", ", debt.Debt.Select(d => $"{d.Id}:{d.Name}:{d.Age.TotalMilliseconds:F0}ms")));
        }
    }

    private void Run()
    {
        try
        {
            SynchronizationContext.SetSynchronizationContext(_context);
            ServerMetrics.LoopOwner = _metrics;
            using var waiter = new HighResolutionWaiter(_workAvailable);
            var nextTick = TickInterval;
            long itemTicks = 0;
            int items = 0;
            while (!_queue.IsCompleted)
            {
                var now = _clock.Elapsed;
                if (now >= nextTick)
                {
                    long tickStart = Stopwatch.GetTimestamp();
                    // Independent deadlines cannot monopolize the loop. A non-preemptible callback can still overrun;
                    // remaining due timers retain their original deadline so the lateness metric exposes the debt.
                    int timers = 0;
                    while (Phase == GameLoopPhase.Accepting && timers < 64 && Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds < 2 &&
                           _timers.TryPeek(out var timer, out var due) && due <= now)
                    {
                        if (!TryAdmit("timer", cleanup: false, out var permit)) break;
                        _timers.Dequeue();
                        long started = Stopwatch.GetTimestamp();
                        double lateness = (Now - due).TotalMilliseconds;
                        var prior = SynchronizationContext.Current;
                        var priorPermit = _currentPermit.Value;
                        SynchronizationContext.SetSynchronizationContext(new LoopContext(this, permit));
                        _currentPermit.Value = permit;
                        try { _ = RunGuarded(timer, permit); }
                        finally { _currentPermit.Value = priorPermit; SynchronizationContext.SetSynchronizationContext(prior); }
                        _metrics?.RecordTimer(lateness, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        timers++;
                    }
                    if (Phase != GameLoopPhase.Accepting) _timers.Clear();
                    var time = new GameTime(now, now - _lastTick);
                    _lastTick = now;
                    long tickerStart = Stopwatch.GetTimestamp();
                    foreach (var ticker in Phase == GameLoopPhase.Accepting ? _tickers.ToArray() : [])
                    {
                        if (!TryAdmit("ticker " + ticker.GetType().Name, cleanup: false, out var tickerPermit)) break;
                        long one = Stopwatch.GetTimestamp();
                        var prior = SynchronizationContext.Current;
                        var priorPermit = _currentPermit.Value;
                        SynchronizationContext.SetSynchronizationContext(new LoopContext(this, tickerPermit));
                        _currentPermit.Value = tickerPermit;
                        try { TickOwned(ticker, time); }
                        catch (Exception e) { _log.LogError(e, "Ticker {Ticker} failed", ticker.GetType().Name); }
                        finally
                        {
                            _currentPermit.Value = priorPermit;
                            SynchronizationContext.SetSynchronizationContext(prior);
                            Release(tickerPermit);
                        }
                        _metrics?.RecordTicker(ticker.GetType().Name, Stopwatch.GetElapsedTime(one).TotalMilliseconds);
                    }
                    long finished = Stopwatch.GetTimestamp();
                    _metrics?.RecordTick((itemTicks + finished - tickStart) * 1000.0 / Stopwatch.Frequency,
                        time.Delta.TotalMilliseconds, Stopwatch.GetElapsedTime(tickerStart, finished).TotalMilliseconds,
                        finished - tickStart, items, _queue.Count);
                    if (_metrics != null && _queue.Count > BurstQueueLength) RecordBurst();
                    items = 0; itemTicks = 0;
                    // Keep the 50 ms phase; skip missed deadlines rather than running a catch-up ticker storm.
                    nextTick += TickInterval;
                    if (nextTick <= _clock.Elapsed)
                        nextTick += TimeSpan.FromTicks(((_clock.Elapsed - nextTick).Ticks / TickInterval.Ticks + 1) * TickInterval.Ticks);
                }
                // Always give queued work one turn after a timer/ticker batch, even if that batch overran.
                if (_queue.TryTake(out var item))
                {
                    long started = Stopwatch.GetTimestamp();
                    Execute(item);
                    long finished = Stopwatch.GetTimestamp();
                    itemTicks += finished - started; items++;
                    _metrics?.RecordQueueItem(item.EnqueuedAt, started, finished);
                    if (_metrics != null && Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds >= SlowItemMs)
                        _metrics.RecordSlowItem(Describe(item.Callback, item.State), Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds);
                }
                else if (!_queue.IsCompleted) waiter.Wait(nextTick - _clock.Elapsed);
            }
        }
        finally
        {
            ServerMetrics.LoopOwner = null;
            // Dispose may be requested from a callback on this thread; the queue is safe to free only after Run exits.
            lock (_lifecycle)
            {
                if (_disposeRequested) DisposeQueue();
                _workerExited.TrySetResult();
            }
        }
    }

    private const double SlowItemMs = 10;
    private const int BurstQueueLength = 200;
    private long _lastBurst;

    /// <summary>
    /// What a queue burst consists of (loop.slowItems "queue burst: X" counts queued items by the work they run), at
    /// most every 5 s: the queue-length histogram shows a burst happened, not who posted it.
    /// </summary>
    private void RecordBurst()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastBurst != 0 && Stopwatch.GetElapsedTime(_lastBurst, now).TotalSeconds < 5) return;
        _lastBurst = now;
        foreach (var group in _queue.ToArray().GroupBy(i => Describe(i.Callback, i.State)).OrderByDescending(g => g.Count()).Take(5))
            _metrics!.RecordSlowItem("queue burst: " + group.Key, group.Count());
    }

    /// <summary>A name for a slow work item (reflection only on the slow path): the async method a continuation
    /// resumes, or the delegate a Post/InvokeAsync closure wraps.</summary>
    private static string Describe(SendOrPostCallback callback, object? state)
    {
        object? target = state ?? callback.Target;
        if (target == null) return callback.Method.DeclaringType?.Name + "." + callback.Method.Name;
        var type = target.GetType();
        if (type.IsGenericType && type.Name.StartsWith("AsyncStateMachineBox"))
            return type.GetGenericArguments().Last().FullName?.Split('.').Last() ?? type.Name;
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            if (field.GetValue(target) is Delegate inner && inner.Method.DeclaringType != typeof(GameLoop))
                return (inner.Method.DeclaringType?.FullName?.Split('.').Last() ?? "?") + "." + inner.Method.Name;
        return type.FullName?.Split('.').Last() ?? type.Name;
    }

    private void Execute((SendOrPostCallback Callback, object? State, long EnqueuedAt, WorkPermit Permit) item)
    {
        var previous = SynchronizationContext.Current;
        var priorPermit = _currentPermit.Value;
        SynchronizationContext.SetSynchronizationContext(new LoopContext(this, item.Permit));
        _currentPermit.Value = item.Permit;
        try { item.Callback(item.State); }
        catch (Exception e)
        {
            _log.LogError(e, "Game loop callback failed");
            if (item.Permit.IsOpen) Release(item.Permit);
        }
        finally
        {
            Interlocked.Decrement(ref item.Permit.PendingCallbacks);
            _currentPermit.Value = priorPermit;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_queueDisposed) return;
            _disposeRequested = true;
            if (_phase is GameLoopPhase.Accepting or GameLoopPhase.Quiescing)
            {
                _phase = GameLoopPhase.Draining;
                _workAvailable.Set();
                TryFinishDrain();
            }
            if (!_started && _outstanding.Count > 0 && _phase != GameLoopPhase.Stopped)
            {
                _drainFailed = true;
                _failedObligations.Add("unstarted loop disposed with admitted work");
                _phase = GameLoopPhase.Stopped;
                _queue.CompleteAdding();
                _metrics?.RecordLoopLifecycle((int)_phase, _outstanding.Count, _rejected, true);
                _log.LogError("Unstarted game loop disposed with {Count} admitted operations; drain failed", _outstanding.Count);
            }
            // A still-running worker owns its queue. Run's finally disposes it after the last use.
            if (!_started || _workerExited.Task.IsCompleted) DisposeQueue();
        }
    }

    private void DisposeQueue()
    {
        if (_queueDisposed) return;
        _queue.Dispose();
        _workAvailable.Dispose();
        _queueDisposed = true;
    }

    /// <summary>Sends every continuation that started on the loop back to the loop.</summary>
    internal sealed class WorkPermit(GameLoop owner, long id, string name, bool cleanup, long startedAt)
    {
        public readonly GameLoop Owner = owner;
        public readonly long Id = id;
        public readonly string Name = name;
        public readonly bool Cleanup = cleanup;
        public readonly long StartedAt = startedAt;
        public bool OrdinarySlot;
        public WorkPermit? Parent;
        internal Ownership.OwnerContext? Ownership;
        // Only explicitly migrated owned commands bind a role presentation lease. The binding
        // survives nested legacy scope disposal for the caller's remaining replies in this permit.
        internal Dictionary<GameSession, Ownership.AttachmentLease>? PresentationLeases;
        public int RefCount = 1;
        public bool IsOpen = true;
        public int PendingCallbacks;
    }

    public sealed class CompletionReservation : IDisposable
    {
        private readonly GameLoop _owner;
        private WorkPermit? _permit;
        internal CompletionReservation(GameLoop owner, WorkPermit permit) { _owner = owner; _permit = permit; }
        internal WorkPermit Permit => _permit ?? throw new ObjectDisposedException(nameof(CompletionReservation));
        public void Dispose()
        {
            var permit = Interlocked.Exchange(ref _permit, null);
            if (permit != null) _owner.Release(permit);
        }
    }

    private sealed class LoopContext(GameLoop loop, WorkPermit? permit = null) : SynchronizationContext
    {
        internal WorkPermit? Permit => permit;
        public override void Post(SendOrPostCallback d, object? state)
        {
            loop.EnqueueContinuation(d, state, permit);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (loop.IsOnLoop) d(state);
            else throw new InvalidOperationException("synchronous Send to the game loop from another thread");
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
