using System.Diagnostics;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Bounded counters for one AccountStore lifetime. These do not reset with the ServerMetrics epoch;
/// callers measuring an epoch compare snapshots at its start and end.</summary>
public sealed class DbAccessMetrics
{
    private long _writerAdmitted, _writerCompleted, _writerFailed, _writerRefused, _writerExecutionCount,
        _writerExecutionTicks, _revisionConflicts, _readerAdmitted,
        _readerCompleted, _readerFailed, _readerRefused, _legacySync, _legacyLoopSync, _legacyBlockedTicks,
        _legacyLoopBlockedTicks, _writerStopTimedOut, _readerStopTimedOut,
        _legacyWriterSync, _legacyWriterLoopSync, _legacyWriterBlockedTicks, _legacyWriterLoopBlockedTicks;
    private long _writerAuthorizations, _bootstrapAuthorizations, _backupAuthorizations, _nonWriterWriteAttempts, _writeGuardErrors;
    private readonly object _denialGate = new();
    private readonly Queue<object> _writeDenials = new();
    private readonly Dictionary<(string Access, string Source, int Thread), long> _writeOrigins = [];
    private long _unlistedWriteOrigins;
    private int _writerJobs, _writerBytes, _lazyJobs, _lazyBytes, _readerJobs, _writerThreadId;
    private long _maxWriterQueueTicks;
    private long _lazyPromoted, _postCommitFailures, _writerTerminal, _writerCommits, _writerGroupedJobs;
    // D1 inventory (C/D 16): synchronous DB waits on the loop by plan label, bounded to the labels the code has.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LoopWait> _loopWaits = new(StringComparer.Ordinal);
    private sealed class LoopWait { internal long Count, Ticks, MaxTicks; }
    internal void WriterDequeued(long enqueuedAt)
    {
        long age = Stopwatch.GetTimestamp() - enqueuedAt;
        long prior;
        do { prior = Interlocked.Read(ref _maxWriterQueueTicks); if (age <= prior) return; }
        while (Interlocked.CompareExchange(ref _maxWriterQueueTicks, age, prior) != prior);
    }
    internal void WriterAdmitted(bool lazy, int bytes)
    {
        Interlocked.Increment(ref _writerAdmitted);
        if (lazy) { Interlocked.Increment(ref _lazyJobs); Interlocked.Add(ref _lazyBytes, bytes); }
        else { Interlocked.Increment(ref _writerJobs); Interlocked.Add(ref _writerBytes, bytes); }
    }
    internal void WriterFinished(bool lazy, int bytes, bool success)
    {
        Interlocked.Increment(ref _writerCompleted);
        if (!success) Interlocked.Increment(ref _writerFailed);
        if (lazy) { Interlocked.Decrement(ref _lazyJobs); Interlocked.Add(ref _lazyBytes, -bytes); }
        else { Interlocked.Decrement(ref _writerJobs); Interlocked.Add(ref _writerBytes, -bytes); }
    }
    internal void WriterRefused() => Interlocked.Increment(ref _writerRefused);
    internal void LazyPromoted() => Interlocked.Increment(ref _lazyPromoted);
    /// <summary>Stage G group commit: one durable commit of <paramref name="jobs"/> executed jobs.</summary>
    internal void WriterCommitted(int jobs)
    {
        Interlocked.Increment(ref _writerCommits);
        Interlocked.Add(ref _writerGroupedJobs, jobs);
    }
    internal void PostCommitFailure() => Interlocked.Increment(ref _postCommitFailures);
    internal void WriterTerminal() => Interlocked.Exchange(ref _writerTerminal, 1);
    /// <summary>Dequeued writer work through outer commit/rollback, including failed executions; excludes queue wait.</summary>
    internal void WriterExecuted(long elapsedTicks)
    {
        Interlocked.Increment(ref _writerExecutionCount);
        Interlocked.Add(ref _writerExecutionTicks, elapsedTicks);
    }
    internal void RevisionConflict() => Interlocked.Increment(ref _revisionConflicts);
    internal void ReaderAdmitted() { Interlocked.Increment(ref _readerAdmitted); Interlocked.Increment(ref _readerJobs); }
    internal void ReaderFinished(bool success)
    {
        Interlocked.Increment(ref _readerCompleted);
        if (!success) Interlocked.Increment(ref _readerFailed);
        Interlocked.Decrement(ref _readerJobs);
    }
    internal void ReaderRefused() => Interlocked.Increment(ref _readerRefused);
    internal void WriterThread(int id) => Volatile.Write(ref _writerThreadId, id);
    internal void WriteAuthorized(bool writer, bool bootstrap, string source, int thread)
    {
        if (writer) Interlocked.Increment(ref _writerAuthorizations);
        else if (bootstrap) Interlocked.Increment(ref _bootstrapAuthorizations);
        else Interlocked.Increment(ref _backupAuthorizations);
        lock (_denialGate)
        {
            var key = (writer ? "writer" : bootstrap ? "bootstrap" : "backup", source, thread);
            if (_writeOrigins.TryGetValue(key, out long count)) _writeOrigins[key] = count + 1;
            else if (_writeOrigins.Count < 128) _writeOrigins.Add(key, 1);
            else _unlistedWriteOrigins++;
        }
    }
    internal void WriteDenied(string source, int action, string target, int thread)
    {
        Interlocked.Increment(ref _nonWriterWriteAttempts);
        lock (_denialGate)
        {
            if (_writeDenials.Count == 8) _writeDenials.Dequeue();
            _writeDenials.Enqueue(new { source, action, target = target.Length <= 96 ? target : target[..96], thread });
        }
    }
    internal void WriteGuardError() => Interlocked.Increment(ref _writeGuardErrors);
    private object[] WriteDenials() { lock (_denialGate) return [.. _writeDenials]; }
    private object[] WriteOrigins()
    {
        lock (_denialGate) return [.. _writeOrigins.Select(p => (object)new
        { access = p.Key.Access, source = p.Key.Source, thread = p.Key.Thread, authorizations = p.Value })];
    }
    internal void WorkerStopTimedOut(bool writer)
    {
        if (writer) Interlocked.Increment(ref _writerStopTimedOut);
        else Interlocked.Increment(ref _readerStopTimedOut);
    }
    internal void LegacyWait(long started, bool onLoop, bool writer = false, string? label = null)
    {
        long ticks = Stopwatch.GetTimestamp() - started;
        if (onLoop && label != null)
        {
            var wait = _loopWaits.GetOrAdd((writer ? "W " : "R ") + label, _ => new LoopWait());
            Interlocked.Increment(ref wait.Count);
            Interlocked.Add(ref wait.Ticks, ticks);
            long max;
            while (ticks > (max = Interlocked.Read(ref wait.MaxTicks)) && Interlocked.CompareExchange(ref wait.MaxTicks, ticks, max) != max) { }
        }
        Interlocked.Increment(ref _legacySync);
        Interlocked.Add(ref _legacyBlockedTicks, ticks);
        if (writer)
        {
            Interlocked.Increment(ref _legacyWriterSync);
            Interlocked.Add(ref _legacyWriterBlockedTicks, ticks);
            if (onLoop)
            {
                Interlocked.Increment(ref _legacyWriterLoopSync);
                Interlocked.Add(ref _legacyWriterLoopBlockedTicks, ticks);
            }
        }
        if (onLoop)
        {
            Interlocked.Increment(ref _legacyLoopSync);
            Interlocked.Add(ref _legacyLoopBlockedTicks, ticks);
        }
    }
    public object Snapshot() => new
    {
        writerAdmitted = Interlocked.Read(ref _writerAdmitted), writerCompleted = Interlocked.Read(ref _writerCompleted),
        writerFailed = Interlocked.Read(ref _writerFailed), writerRefused = Interlocked.Read(ref _writerRefused),
        writerExecutionCount = Interlocked.Read(ref _writerExecutionCount),
        writerExecutionMs = Interlocked.Read(ref _writerExecutionTicks) * 1000d / Stopwatch.Frequency,
        revisionConflicts = Interlocked.Read(ref _revisionConflicts),
        writerJobs = Volatile.Read(ref _writerJobs), writerBytes = Volatile.Read(ref _writerBytes),
        maxWriterQueueAgeMs = Interlocked.Read(ref _maxWriterQueueTicks) * 1000d / Stopwatch.Frequency,
        lazyJobs = Volatile.Read(ref _lazyJobs), lazyBytes = Volatile.Read(ref _lazyBytes),
        writerThreadId = Volatile.Read(ref _writerThreadId),
        writerWriteAuthorizations = Interlocked.Read(ref _writerAuthorizations),
        bootstrapWriteAuthorizations = Interlocked.Read(ref _bootstrapAuthorizations),
        backupWriteAuthorizations = Interlocked.Read(ref _backupAuthorizations),
        nonWriterWriteAttempts = Interlocked.Read(ref _nonWriterWriteAttempts),
        writeGuardErrors = Interlocked.Read(ref _writeGuardErrors),
        writeDenials = WriteDenials(),
        writeOrigins = WriteOrigins(), unlistedWriteOrigins = Interlocked.Read(ref _unlistedWriteOrigins),
        readerAdmitted = Interlocked.Read(ref _readerAdmitted), readerCompleted = Interlocked.Read(ref _readerCompleted),
        readerFailed = Interlocked.Read(ref _readerFailed), readerRefused = Interlocked.Read(ref _readerRefused),
        readerJobs = Volatile.Read(ref _readerJobs),
        writerStopTimedOut = Interlocked.Read(ref _writerStopTimedOut), readerStopTimedOut = Interlocked.Read(ref _readerStopTimedOut),
        legacySync = Interlocked.Read(ref _legacySync), legacyLoopSync = Interlocked.Read(ref _legacyLoopSync),
        legacyBlockedMs = Interlocked.Read(ref _legacyBlockedTicks) * 1000d / Stopwatch.Frequency,
        legacyLoopBlockedMs = Interlocked.Read(ref _legacyLoopBlockedTicks) * 1000d / Stopwatch.Frequency,
        legacyWriterSync = Interlocked.Read(ref _legacyWriterSync), legacyWriterLoopSync = Interlocked.Read(ref _legacyWriterLoopSync),
        legacyWriterBlockedMs = Interlocked.Read(ref _legacyWriterBlockedTicks) * 1000d / Stopwatch.Frequency,
        legacyWriterLoopBlockedMs = Interlocked.Read(ref _legacyWriterLoopBlockedTicks) * 1000d / Stopwatch.Frequency,
        loopWaitLabels = _loopWaits.Count,
        lazyPromoted = Interlocked.Read(ref _lazyPromoted), postCommitFailures = Interlocked.Read(ref _postCommitFailures),
        writerCommits = Interlocked.Read(ref _writerCommits), writerCommittedJobs = Interlocked.Read(ref _writerGroupedJobs),
        writerTerminal = Interlocked.Read(ref _writerTerminal) != 0,
    };

    /// <summary>Synchronous DB waits on the loop by "R|W label", heaviest first (the C/D 16 work list).</summary>
    public IReadOnlyList<object> LoopWaits(int top) => [.. _loopWaits
        .Select(p => (Label: p.Key, Count: Interlocked.Read(ref p.Value.Count), Ticks: Interlocked.Read(ref p.Value.Ticks), Max: Interlocked.Read(ref p.Value.MaxTicks)))
        .OrderByDescending(w => w.Ticks).Take(top)
        .Select(w => (object)new { label = w.Label, count = w.Count, ms = w.Ticks * 1000d / Stopwatch.Frequency, maxMs = w.Max * 1000d / Stopwatch.Frequency })];
    internal IReadOnlyDictionary<string, long> LoopWaitCounts() => _loopWaits.ToDictionary(p => p.Key, p => Interlocked.Read(ref p.Value.Count), StringComparer.Ordinal);
}
