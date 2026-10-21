using System.Diagnostics;

namespace Ro3Emu.Server.Game.Persistence;

/// <summary>
/// A lazily persisted role part (C/D 17/18). A change only marks the part dirty; this ticker captures the latest
/// value and writes it on the writer's lazy lane, so the loop never waits on SQLite for it.
/// - **When:** an urgent change is written on the next tick; otherwise at most one <see cref="Interval"/> after the
///   first unpersisted change. Sustained changes never postpone it.
/// - **Ordering:** a CAS chain on the part's revision, taken from the owner's committed vector. A capture that was
///   overtaken by any other write of the part (a barrier) is refused by the writer and dropped, so an older value
///   can never overwrite a newer one; the chain is then refreshed and the current value written again.
/// - **Barriers:** saves that must be durable now (leave world, coupled state) stay synchronous and call
///   <see cref="Persisted"/>.
/// </summary>
public abstract class LazyRolePart(GameLoop loop, Accounts.RoleStore roles, ILogger log) : IGameTicker
{
    public sealed class State
    {
        public long Applied, Persisted, Submitted;
        public long DirtySince;          // Stopwatch timestamp of the first change not yet persisted
        public long DirtyAfterCapture;   // first change after the in-flight capture (becomes DirtySince on completion)
        public bool Urgent, InFlight;
        public int FailureStreak;        // consecutive failed writes (not overtaken captures)
    }

    private readonly GameLoop _loop = loop;
    private readonly Accounts.RoleStore _roles = roles;
    private readonly ILogger _log = log;
    private readonly HashSet<GameSession> _dirty = new(ReferenceEqualityComparer.Instance);
    private long _writes, _overtaken, _failures;
    private double _maxDirtyMs;

    /// <summary>The revision part guarded by the CAS chain (RevisionStore part name).</summary>
    protected abstract string RevisionPart { get; }
    /// <summary>The session's state slot for this part.</summary>
    protected abstract State? Get(GameSession s);
    protected abstract void Set(GameSession s, State state);
    /// <summary>Whether this session's current value may be written lazily (e.g. not on a private floor).</summary>
    protected virtual bool Eligible(GameSession s) => s.Role != null;
    /// <summary>Captures the current value now and returns its lazy write (role, epoch, expected revision).</summary>
    protected abstract Func<long, long, long, Task<bool>> Capture(GameSession s);

    internal TimeSpan Interval { get; set; }

    public void MarkDirty(GameSession s, bool urgent)
    {
        if (!Eligible(s)) return;
        var part = Get(s) ?? new State();
        Set(s, part);
        part.Applied++;
        long now = Stopwatch.GetTimestamp();
        if (part.InFlight) { if (part.DirtyAfterCapture == 0) part.DirtyAfterCapture = now; }
        else if (part.DirtySince == 0) part.DirtySince = now;
        part.Urgent |= urgent;
        _dirty.Add(s);
    }

    /// <summary>A barrier saved the session's current value durably: nothing older may be written after it.</summary>
    public void Persisted(GameSession s)
    {
        if (Get(s) is not { } part) return;
        part.Persisted = part.Applied;
        part.DirtySince = part.DirtyAfterCapture = 0;
        part.Urgent = false;
        if (!part.InFlight) _dirty.Remove(s);
    }

    /// <summary>Whether a change has not been persisted yet (a barrier then has something to save).</summary>
    public bool IsDirty(GameSession s) => Get(s) is { } part && part.Applied > part.Persisted;

    public void Tick(GameTime time)
    {
        if (_dirty.Count == 0) return;
        long now = Stopwatch.GetTimestamp();
        foreach (var s in _dirty.ToArray())
        {
            var part = Get(s);
            if (part == null || s.Role == null) { _dirty.Remove(s); continue; }
            if (part.InFlight) continue;
            if (part.Applied <= part.Persisted) { _dirty.Remove(s); part.DirtySince = 0; continue; }
            if (!part.Urgent && Stopwatch.GetElapsedTime(part.DirtySince, now) < Interval) continue;
            Submit(s, part);
        }
    }

    private void Submit(GameSession s, State part)
    {
        var role = s.Role!;
        var owner = s.Owner;
        if (!Eligible(s) || owner == null || owner.State is not (Ownership.RoleOwnerState.Attached or Ownership.RoleOwnerState.Loading)
            || !owner.Revisions.TryGetValue(RevisionPart, out long revision))
        {
            _dirty.Remove(s); // detached or ineligible: the barrier path owns the value now
            return;
        }
        var write = Capture(s);
        long version = part.Applied, dirtySince = part.DirtySince;
        part.InFlight = true;
        part.Urgent = false;
        part.Submitted = version;
        _loop.TrackDetached("lazy/" + RevisionPart, async () =>
        {
            bool written = false, failed = false;
            try
            {
                written = await write(role.Id, owner.LifecycleEpoch, revision);
                if (written)
                {
                    Interlocked.Increment(ref _writes);
                    double ms = Stopwatch.GetElapsedTime(dirtySince).TotalMilliseconds;
                    if (ms > _maxDirtyMs) _maxDirtyMs = ms;
                }
                else
                {
                    // Overtaken by another write of the part (a barrier): drop this capture, refresh the chain from
                    // the committed vector and write the current value on the next tick.
                    Interlocked.Increment(ref _overtaken);
                    var vector = await _roles.ReadRevisionVectorAsync(role.Id);
                    // The read is asynchronous: a barrier recorded meanwhile may be newer. Revisions only grow
                    // within an epoch, so keep the larger one.
                    if (ReferenceEquals(s.Owner, owner) && owner.LifecycleEpoch == vector.Epoch && !vector.Deleted
                        && vector.Parts.TryGetValue(RevisionPart, out long current))
                        owner.Revisions = new Dictionary<string, long>(owner.Revisions, StringComparer.Ordinal)
                        { [RevisionPart] = Math.Max(current, owner.Revisions.GetValueOrDefault(RevisionPart)) };
                    part.Urgent = true;
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                failed = true;
                // A full lazy lane fails every capture: log the first of a streak and every hundredth.
                if (++part.FailureStreak % 100 == 1)
                    _log.LogWarning(e, "Lazy {Part} write for role {Role} failed ({Streak} in a row); it stays dirty",
                        RevisionPart, role.Id, part.FailureStreak);
            }
            finally
            {
                part.InFlight = false;
                if (!failed) part.FailureStreak = 0;
                if (written && part.Persisted < version) part.Persisted = version;
                if (part.Applied > part.Persisted)
                {
                    // Still dirty: the next deadline counts from the first change this capture did not include.
                    // A failure waits one Interval before the retry instead of resubmitting on every tick.
                    part.DirtySince = written ? (part.DirtyAfterCapture != 0 ? part.DirtyAfterCapture : Stopwatch.GetTimestamp())
                        : failed ? Stopwatch.GetTimestamp() : part.DirtySince;
                    if (failed) part.Urgent = false;
                    if (ReferenceEquals(s.Role, role)) _dirty.Add(s);
                }
                else part.DirtySince = 0;
                part.DirtyAfterCapture = 0;
            }
        });
    }

    public object Snapshot() => new
    {
        lazyWrites = Interlocked.Read(ref _writes), overtaken = Interlocked.Read(ref _overtaken),
        failures = Interlocked.Read(ref _failures), dirty = _dirty.Count, maxFirstDirtyToCommitMs = _maxDirtyMs,
    };
}
