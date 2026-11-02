using System.Runtime.CompilerServices;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Cancellable, replaceable wall-clock deadlines for game-loop work. The loop owns all methods and
/// callbacks; callers outside the loop marshal through GameLoop.InvokeAsync. Unlike a timer per
/// player through GameLoop.Schedule, removing an entry here also removes its callback immediately.
/// Only a bounded number of due entries is posted on each game-loop tick. Publication can be
/// several ticks late under load; callbacks receive their original due instant, while services
/// continue to check eligibility against the configured boundary.
/// </summary>
public sealed class Deadlines : IGameTicker, IDisposable
{
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly int _perTickLimit;
    private readonly Dictionary<GameSession, Owner> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<GameSession, Detached> _detached = [];
    private readonly Dictionary<string, Entry> _global = new(StringComparer.Ordinal);
    private readonly SortedSet<Entry> _due = new(EntryComparer.Instance);
    private long _nextGeneration;
    private int _disposed;
    private readonly TaskCompletionSource _disposalComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task DisposalComplete => _disposalComplete.Task;

    public Deadlines(GameLoop loop, TimeProvider? clock = null, int perTickLimit = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perTickLimit);
        _loop = loop;
        _clock = clock ?? TimeProvider.System;
        _perTickLimit = perTickLimit;
    }

    /// <summary>Explicitly begin a new attachment, invalidating any work from the previous one.</summary>
    public void Attach(GameSession session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        DetachEntries(session);
        _detached.Remove(session);
        _owners.Add(session, new Owner(session, ++_nextGeneration));
    }

    /// <summary>Invalidate all work and release this connection, including queued callbacks.</summary>
    public void Detach(GameSession session)
    {
        DetachEntries(session);
        _detached.Remove(session);
        _detached.Add(session, new Detached());
    }

    /// <summary>
    /// Replace one service deadline for a session. A never-attached session attaches on first use,
    /// so isolated service tests work; an explicitly detached session must be reattached explicitly.
    /// Use the session argument in the callback instead of closing over the session.
    /// After each await, check IsCurrent(session, lease) before changing role state.
    /// A default lease means the session was detached and no deadline was installed.
    /// </summary>
    public DeadlineLease Set(GameSession session, string key, DateTimeOffset dueUtc,
        Func<GameSession, DateTimeOffset, DeadlineLease, CancellationToken, Task> callback, bool realmScope = false,
        bool callbackOwnsScope = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(callback);
        if (realmScope && callbackOwnsScope)
            throw new ArgumentException("A deadline cannot request both a realm scope and a callback-owned scope.");
        if (_detached.TryGetValue(session, out _)) return default;
        var owner = GetOrAttach(session);
        if (owner.Entries.Remove(key, out var old)) CancelEntry(old);
        var lease = new DeadlineLease(owner.Generation, ++_nextGeneration, session.Role?.Id,
            session.AccountEntitySerial, key, false);
        var entry = new Entry(dueUtc, lease.ScheduleGeneration, owner, key, lease, callback, null)
        { RealmScope = realmScope, CallbackOwnsScope = callbackOwnsScope };
        owner.Entries.Add(key, entry);
        _due.Add(entry);
        return lease;
    }

    public void Cancel(GameSession session, string key)
    {
        if (_owners.TryGetValue(session, out var owner) && owner.Entries.Remove(key, out var entry))
            CancelEntry(entry);
    }

    /// <summary>Replace one process-wide deadline, sharing the same due-work quota.</summary>
    public DeadlineLease SetGlobal(string key, DateTimeOffset dueUtc,
        Func<DateTimeOffset, DeadlineLease, CancellationToken, Task> callback)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(callback);
        if (_global.Remove(key, out var old)) CancelEntry(old);
        var lease = new DeadlineLease(0, ++_nextGeneration, null, 0, key, true);
        var entry = new Entry(dueUtc, lease.ScheduleGeneration, null, key, lease, null, callback);
        _global.Add(key, entry);
        _due.Add(entry);
        return lease;
    }

    public void CancelGlobal(string key)
    {
        if (_global.Remove(key, out var entry)) CancelEntry(entry);
    }

    /// <summary>Capture the current attachment and role before awaiting a service gate.</summary>
    public DeadlineLease Capture(GameSession session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_detached.TryGetValue(session, out _)) return default;
        var owner = GetOrAttach(session);
        return new DeadlineLease(owner.Generation, 0, session.Role?.Id, session.AccountEntitySerial, null, false);
    }

    /// <summary>True only for the same attachment, role and (when present) latest service schedule.</summary>
    public bool IsCurrent(GameSession session, DeadlineLease lease)
    {
        if (Volatile.Read(ref _disposed) != 0 || lease.IsGlobal || lease.AttachmentGeneration == 0 ||
            !_owners.TryGetValue(session, out var owner) || owner.Generation != lease.AttachmentGeneration ||
            session.Role?.Id != lease.RoleId || session.AccountEntitySerial != lease.AccountEntitySerial)
            return false;
        return lease.Key == null ||
            (owner.Entries.TryGetValue(lease.Key, out var entry) &&
             entry.Generation == lease.ScheduleGeneration && !entry.Cancellation.IsCancellationRequested);
    }

    public bool IsGlobalCurrent(DeadlineLease lease) =>
        Volatile.Read(ref _disposed) == 0 && lease.IsGlobal && lease.Key != null &&
        _global.TryGetValue(lease.Key, out var entry) &&
        entry.Generation == lease.ScheduleGeneration && !entry.Cancellation.IsCancellationRequested;

    public void Tick(GameTime time)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var now = _clock.GetUtcNow();
        for (var i = 0; i < _perTickLimit && _due.Count > 0; i++)
        {
            var entry = _due.Min!;
            if (entry.DueUtc > now) break;
            _due.Remove(entry);
            _loop.Post(() => RunAsync(entry));
        }
    }

    /// <summary>Number of live scheduled entries, including work posted but not yet completed.</summary>
    public int Count => _owners.Values.Sum(owner => owner.Entries.Count) + _global.Count;

    internal int WaitingCount => _due.Count;

    internal DateTimeOffset? ScheduledDue(GameSession session, string key) =>
        _owners.TryGetValue(session, out var owner) && owner.Entries.TryGetValue(key, out var entry)
            ? entry.DueUtc : null;

    internal DeadlineLease? ScheduledLease(GameSession session, string key) =>
        _owners.TryGetValue(session, out var owner) && owner.Entries.TryGetValue(key, out var entry)
            ? entry.Lease : null;

    // Test-only completion observation; the exact lease excludes older callbacks with the same key.
    internal Action<DeadlineLease>? CallbackCompletedForTest { get; set; }

    private Owner GetOrAttach(GameSession session)
    {
        if (_owners.TryGetValue(session, out var owner)) return owner;
        owner = new Owner(session, ++_nextGeneration);
        _owners.Add(session, owner);
        return owner;
    }

    private async Task RunAsync(Entry entry)
    {
        var owner = entry.Owner;
        try
        {
            if (entry.Cancellation.IsCancellationRequested) return;
            var session = owner?.Session;
            if (session != null && entry.SessionCallback != null && IsCurrent(session, entry.Lease))
            {
                try
                {
                    if (entry.CallbackOwnsScope && _loop.Owners is { } owners)
                        await owners.RunRuntimeAsync("deadline/" + entry.Key, async () =>
                        {
                            if (IsCurrent(session, entry.Lease))
                                await entry.SessionCallback(session, entry.DueUtc, entry.Lease, entry.Cancellation.Token);
                        });
                    else
                    {
                        using var scope = _loop.Owners == null ? null : await _loop.Owners.EnterAsync("deadline/" + entry.Key,
                            entry.RealmScope ? Ownership.ReservationKey.Realm : Ownership.ReservationKey.Role(session.Role!.Id));
                        if (IsCurrent(session, entry.Lease))
                            await entry.SessionCallback(session, entry.DueUtc, entry.Lease, entry.Cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested) { }
            }
            else if (entry.GlobalCallback != null && IsGlobalCurrent(entry.Lease))
            {
                try
                {
                    using var scope = _loop.Owners == null ? null : await _loop.Owners.EnterAsync("deadline/" + entry.Key,
                        Ownership.ReservationKey.Realm);
                    if (IsGlobalCurrent(entry.Lease)) await entry.GlobalCallback(entry.DueUtc, entry.Lease, entry.Cancellation.Token);
                }
                catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested) { }
            }
        }
        finally
        {
            if (owner != null && owner.Entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                owner.Entries.Remove(entry.Key);
            if (owner == null && _global.TryGetValue(entry.Key, out var global) && ReferenceEquals(global, entry))
                _global.Remove(entry.Key);
            ClearEntry(entry);
            CallbackCompletedForTest?.Invoke(entry.Lease);
        }
    }

    private void DetachEntries(GameSession session)
    {
        if (!_owners.Remove(session, out var owner)) return;
        foreach (var entry in owner.Entries.Values.ToArray()) CancelEntry(entry);
        owner.Entries.Clear();
        owner.Session = null;
    }

    private void CancelEntry(Entry entry)
    {
        _due.Remove(entry);
        entry.Cancellation.Cancel();
        ClearEntry(entry);
    }

    private static void ClearEntry(Entry entry)
    {
        entry.Owner = null;
        entry.SessionCallback = null;
        entry.GlobalCallback = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            // While accepting, ticker removal and collection teardown execute together on the
            // game loop. After quiesce no new ticks run, but an admitted callback may still finish.
            if (_loop.TryRemove(this, CompleteClear)) return;
        }
        catch
        {
            DeferClearUntilWorkerExited();
            throw;
        }
        DeferClearUntilWorkerExited();
    }

    private void DeferClearUntilWorkerExited()
    {
        if (_loop.IsOnLoop || !_loop.IsWorkerActive) CompleteClear();
        else _ = _loop.WorkerExited.ContinueWith(_ => CompleteClear(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CompleteClear()
    {
        try
        {
            ClearEntries();
            _disposalComplete.TrySetResult();
        }
        catch (Exception e)
        {
            _loop.ReportCleanupFailure("deadline disposal: " + e);
            _disposalComplete.TrySetException(e);
        }
    }

    private void ClearEntries()
    {
        foreach (var session in _owners.Keys.ToArray()) DetachEntries(session);
        foreach (var entry in _global.Values.ToArray()) CancelEntry(entry);
        _global.Clear();
    }

    private sealed class Detached { }

    private sealed class Owner(GameSession session, long generation)
    {
        public GameSession? Session = session;
        public long Generation { get; } = generation;
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Entry(DateTimeOffset dueUtc, long generation, Owner? owner, string key,
        DeadlineLease lease,
        Func<GameSession, DateTimeOffset, DeadlineLease, CancellationToken, Task>? sessionCallback,
        Func<DateTimeOffset, DeadlineLease, CancellationToken, Task>? globalCallback)
    {
        public DateTimeOffset DueUtc { get; } = dueUtc;
        public bool RealmScope { get; init; }
        public bool CallbackOwnsScope { get; init; }
        public long Generation { get; } = generation;
        public Owner? Owner = owner;
        public string Key { get; } = key;
        public DeadlineLease Lease { get; } = lease;
        public CancellationTokenSource Cancellation { get; } = new();
        public Func<GameSession, DateTimeOffset, DeadlineLease, CancellationToken, Task>? SessionCallback = sessionCallback;
        public Func<DateTimeOffset, DeadlineLease, CancellationToken, Task>? GlobalCallback = globalCallback;
    }

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static EntryComparer Instance { get; } = new();
        public int Compare(Entry? x, Entry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var byDue = x.DueUtc.CompareTo(y.DueUtc);
            return byDue != 0 ? byDue : x.Generation.CompareTo(y.Generation);
        }
    }
}

/// <summary>Attachment, role and optional service-schedule generation captured before asynchronous work.</summary>
public readonly record struct DeadlineLease(
    long AttachmentGeneration, long ScheduleGeneration, long? RoleId, int AccountEntitySerial,
    string? Key, bool IsGlobal);
