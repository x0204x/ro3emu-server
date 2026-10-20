using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game.Ownership;

/// <summary>Loop-owned role identities and migration reservations; SessionRegistry remains a connection directory.</summary>
public sealed class OwnerRegistry
{
    private readonly GameLoop _loop;
    private readonly RoleStore _roles;
    private readonly Dictionary<long, RoleOwner> _rolesById = [];
    private long _operation, _admissionSequence;
    public ReservationCoordinator Reservations { get; } = new();
    public long Deferred { get; private set; }
    public double MaxQueueAgeMs { get; private set; }
    public double TotalQueueWaitMs { get; private set; }
    public long RealmScopes { get; private set; }
    public long RuntimeIntents { get; private set; }
    public long HandoverCount { get; internal set; }
    /// <summary>Replacement admissions that encountered an unresolved predecessor handover/readiness fence.</summary>
    public long HandoverConflicts { get; internal set; }
    internal Action<string>? DeferredForTest { get; set; }
    public OwnerRegistry(GameLoop loop, RoleStore roles)
    {
        _loop = loop; _roles = roles; loop.Owners = this;
        // Diagnostics (C/D 15 inventories): RO3EMU_OWNER_AUDIT=<file> appends every uncovered expansion to the file
        // and lets it proceed under its parent, so one full test run lists every dependency of a narrowed scope.
        if (Environment.GetEnvironmentVariable("RO3EMU_OWNER_AUDIT") is { Length: > 0 } auditFile)
            UncoveredForTest = line => { lock (AuditFileLock) File.AppendAllText(auditFile, line + Environment.NewLine); };
    }
    private static readonly object AuditFileLock = new();
    public RoleOwner Get(long id)
    {
        AssertLoop();
        if (!_rolesById.TryGetValue(id, out var owner))
        {
            var vector = _roles.ReadRevisionVector(id);
            if (vector.Deleted) throw new InvalidOperationException($"Deleted role {id}.");
            _rolesById.Add(id, owner = new RoleOwner(id, vector.Epoch) { Revisions = vector.Parts });
        }
        return owner;
    }
    internal RoleOwner? Cached(long id)
    { AssertLoop(); return _rolesById.GetValueOrDefault(id); }
    /// <summary>Register an offline identity from a read-worker snapshot without another loop read.
    /// An existing owner's vector is never replaced: the caller must revalidate its snapshot after reservation.</summary>
    internal RoleOwner GetOrRegister(Accounts.Persistence.RoleRevisionVector snapshot)
    {
        AssertLoop();
        if (snapshot.Deleted) throw new Accounts.Persistence.OwnerRevisionConflictException();
        if (!_rolesById.TryGetValue(snapshot.RoleId, out var owner))
            _rolesById.Add(snapshot.RoleId, owner = new RoleOwner(snapshot.RoleId, snapshot.Epoch)
            { Revisions = snapshot.Parts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) });
        if (owner.LifecycleEpoch != snapshot.Epoch || owner.State == RoleOwnerState.Deleted)
            throw new Accounts.Persistence.OwnerRevisionConflictException();
        return owner;
    }
    /// <summary>Tick work is speculative: a captured attachment must still be the published owner
    /// when the timer is ready to enter its mutating section. Handover cleanup uses its own capability.</summary>
    internal bool IsCurrentAttachedTimer(GameSession session, RoleOwner? owner, AttachmentLease? lease)
    {
        AssertLoop();
        return owner != null && lease != null && !lease.Cancelled &&
            ReferenceEquals(session.Owner, owner) && ReferenceEquals(session.Attachment, lease) &&
            ReferenceEquals(owner.Attachment, lease) && owner.State == RoleOwnerState.Attached &&
            session.Role?.Id == owner.RoleId;
    }
    internal long BeginQuiescing(RoleOwner owner, AttachmentLease lease)
    {
        AssertLoop();
        if (!ReferenceEquals(owner.Attachment, lease))
            throw new InvalidOperationException("Only the current role attachment can quiesce its owner.");
        if (owner.State is RoleOwnerState.Deleting or RoleOwnerState.Deleted or RoleOwnerState.RecoveryRequired)
            throw new InvalidOperationException($"Role {owner.RoleId} cannot begin handover from {owner.State}.");
        owner.State = RoleOwnerState.Quiescing;
        owner.QuiesceAdmission = _admissionSequence;
        return owner.QuiesceAdmission;
    }
    internal void RoleDeleted(long roleId, long epoch)
    {
        AssertLoop();
        if (_rolesById.TryGetValue(roleId, out var owner))
        {
            owner.LifecycleEpoch = epoch;
            owner.State = RoleOwnerState.Deleted;
            owner.Attachment = null;
            owner.Revisions = new Dictionary<string, long>();
        }
    }
    internal void BindLegacySelection(GameSession session)
    {
        AssertLoop();
        var owner = Get(session.Role!.Id);
        if (owner.State != RoleOwnerState.Detached || owner.Attachment != null || owner.Detaching != null)
            throw new InvalidOperationException($"Role {owner.RoleId} still has an attachment or recovery debt.");
        var vector = _roles.ReadRevisionVector(owner.RoleId);
        if (vector.Deleted || vector.Epoch != owner.LifecycleEpoch)
            throw new InvalidOperationException($"Role {owner.RoleId} changed lifecycle during selection.");
        owner.Revisions = vector.Parts;
        Current?.Pin(owner);
        session.Owner = owner;
        if (session.Attachment is { } previous) previous.Cancelled = true;
        session.Attachment = new(session.Account!, owner.RoleId, ++owner.NextGeneration,
            _loop.CurrentWork?.Id ?? throw new InvalidOperationException("Selection has no admitted request."));
        owner.Attachment = session.Attachment;
        owner.State = RoleOwnerState.Loading;
    }
    internal void RecordCommittedRole(Accounts.Persistence.RoleRevisionVector before, IEnumerable<string> parts)
    {
        AssertLoop();
        if (!_rolesById.TryGetValue(before.RoleId, out var owner)) return;
        if (owner.LifecycleEpoch != before.Epoch) throw new InvalidOperationException("Commit applied to another role lifecycle.");
        var next = owner.Revisions.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (string part in parts.Distinct()) next[part] = checked(before.Revision(part) + 1);
        owner.Revisions = next;
    }
    internal OwnerContext? Current => _loop.CurrentWork?.Ownership is { Active: true } context ? context : null;
    internal void BindPresentation(OwnerContext context, GameSession session)
    {
        AssertCurrent(context);
        var lease = session.Attachment ?? throw new InvalidOperationException("Owned presentation needs an attachment.");
        var permit = _loop.CurrentWork ?? throw new InvalidOperationException("Owned presentation needs an admitted operation.");
        var bound = permit.PresentationLeases ??= new(ReferenceEqualityComparer.Instance);
        if (bound.TryGetValue(session, out var existing) && !ReferenceEquals(existing, lease))
            throw new InvalidOperationException("One operation cannot present two attachments of the same session.");
        bound[session] = lease;
    }
    internal bool AllowsPresentation(GameSession session)
    {
        // Off-loop transport calls and unmigrated work keep their existing policy.
        var leases = _loop.CurrentWork?.PresentationLeases;
        if (leases == null || !leases.TryGetValue(session, out var lease)) return true;
        return !lease.Cancelled && ReferenceEquals(session.Attachment, lease);
    }
    internal void AssertLoop()
    { if (!_loop.IsOnLoop) throw new InvalidOperationException("Owner state must run on its game loop."); }
    // Deferred admissions by origin (C/D 16 diagnostics): count, total and max queued time, and which origin held a
    // conflicting key when the admission first had to wait. Read off the loop by the metrics endpoint, hence the lock.
    private readonly Dictionary<long, string> _origins = [];
    private sealed class WaitUse { public long Count; public double WaitMs, MaxWaitMs; public readonly Dictionary<string, long> BlockedBy = []; }
    private readonly Dictionary<string, WaitUse> _waitByOrigin = [];
    internal void Forget(long operation) => _origins.Remove(operation);
    // The blocking origin and the kind of key it holds: entity kind only, never an id or an account name, so there is
    // one label per conflict shape and the 16 slots per origin do not fill with individual keys.
    private string BlockerLabel(ReservationKey[] keys) => Reservations.Blocker(keys) is { } blocker
        ? $"{_origins.GetValueOrDefault(blocker.Operation, "?")} @ {blocker.Key.Kind}:{blocker.Key.Kind switch
        {
            ReservationKind.Entity => blocker.Key.Id.Split(':')[0],
            ReservationKind.Realm => blocker.Key.Id,
            _ => "#",
        }}"
        : "?";
    private WaitUse WaitOf(string origin)
    {
        if (_waitByOrigin.TryGetValue(origin, out var use)) return use;
        if (_waitByOrigin.Count >= MaxRealmOrigins) origin = "(other)";
        if (!_waitByOrigin.TryGetValue(origin, out use)) _waitByOrigin[origin] = use = new WaitUse();
        return use;
    }
    private void RecordWait(string origin, string? blockedBy, long queuedAt)
    {
        double waited = System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        lock (_waitByOrigin)
        {
            var use = WaitOf(origin);
            use.Count++; use.WaitMs += waited; use.MaxWaitMs = Math.Max(use.MaxWaitMs, waited);
            if (blockedBy != null && (use.BlockedBy.Count < 16 || use.BlockedBy.ContainsKey(blockedBy)))
                use.BlockedBy[blockedBy] = use.BlockedBy.GetValueOrDefault(blockedBy) + 1;
        }
    }
    private void NoteBlocked(string origin, string blockedBy)
    {
        lock (_waitByOrigin)
        {
            var use = WaitOf(origin);
            if (use.BlockedBy.Count < 16 || use.BlockedBy.ContainsKey(blockedBy))
                use.BlockedBy[blockedBy] = use.BlockedBy.GetValueOrDefault(blockedBy) + 1;
        }
    }
    private object WaitByOrigin()
    {
        lock (_waitByOrigin)
            return _waitByOrigin.OrderByDescending(x => x.Value.WaitMs).Take(40).ToDictionary(x => x.Key, x => new
            {
                count = x.Value.Count, waitMs = Math.Round(x.Value.WaitMs, 1), maxWaitMs = Math.Round(x.Value.MaxWaitMs, 1),
                blockedBy = x.Value.BlockedBy.OrderByDescending(b => b.Value).Take(5).ToDictionary(b => b.Key, b => b.Value),
            });
    }

    // Realm admissions by origin: count, time queued for Realm, time Realm was held (every other reservation waits for
    // it). Read by the metrics endpoint off the loop, hence the lock.
    private sealed class RealmUse { public long Count; public double WaitMs, HeldMs, MaxHeldMs; }
    private readonly Dictionary<string, RealmUse> _realmByOrigin = [];
    private const int MaxRealmOrigins = 256;

    private long RealmAdmitted(string origin, ReservationKey[] keys, long queuedAt)
    {
        if (!keys.Any(x => x.Kind == ReservationKind.Realm)) return 0;
        RealmScopes++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_realmByOrigin)
        {
            if (RealmOrigin(origin) is { } use)
            {
                use.Count++;
                if (queuedAt != 0) use.WaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt, now).TotalMilliseconds;
            }
        }
        return now;
    }

    internal void RealmReleased(OwnerContext context)
    {
        double held = System.Diagnostics.Stopwatch.GetElapsedTime(context.RealmAdmittedAt).TotalMilliseconds;
        lock (_realmByOrigin)
        {
            if (RealmOrigin(context.RealmLabel ?? context.Origin) is not { } use) return;
            use.HeldMs += held;
            use.MaxHeldMs = Math.Max(use.MaxHeldMs, held);
        }
    }

    private RealmUse? RealmOrigin(string origin)
    {
        if (_realmByOrigin.TryGetValue(origin, out var use)) return use;
        if (_realmByOrigin.Count >= MaxRealmOrigins) origin = "(other)";
        if (!_realmByOrigin.TryGetValue(origin, out use)) _realmByOrigin[origin] = use = new RealmUse();
        return use;
    }

    /// <summary>For a service's own retry loop, once its scope is admitted (records the wait under that scope).</summary>
    internal void RecordQueueAge(long queuedAt)
    {
        if (Current is { } admitted) RecordWait(admitted.Origin, null, queuedAt);
        RecordQueueAgeCore(queuedAt);
    }
    private void RecordQueueAgeCore(long queuedAt)
    {
        AssertLoop();
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        MaxQueueAgeMs = Math.Max(MaxQueueAgeMs, elapsed);
        TotalQueueWaitMs += elapsed;
    }
    internal void AssertCurrent(OwnerContext context)
    {
        AssertLoop();
        if (!context.Active || context.Registry != this || !ReferenceEquals(Current, context))
            throw new InvalidOperationException("Owner capability is inactive or belongs to another operation.");
    }
    /// <summary>C/D 15 audit (tests only): when set, a nested request or write its scope does not cover is reported
    /// here and proceeds under the parent instead of throwing, so one run lists every expansion.</summary>
    internal Action<string>? UncoveredForTest { get; set; }
    internal bool AuditUncovered(string what)
    {
        if (UncoveredForTest is not { } audit) return false;
        audit(what);
        return true;
    }
    /// <summary>True outside a durable scope, or when the current durable scope already holds the keys (a nested
    /// request for them borrows instead of throwing).</summary>
    internal bool CurrentCovers(params ReservationKey[] keys) =>
        Current is not { RuntimeOnly: false } parent || Reservations.Covers(parent.Operation, keys);
    public Task<LegacyOwnerScope> EnterAsync(string origin, params ReservationKey[] keys)
        => EnterAsync(origin, keys, CancellationToken.None);
    internal async Task<LegacyOwnerScope> EnterAsync(string origin, ReservationKey[] keys, CancellationToken cancellation,
        string? realmLabel = null)
    {
        AssertLoop();
        cancellation.ThrowIfCancellationRequested();
        var permit = _loop.CurrentWork ?? throw new InvalidOperationException("Owner scope needs an admitted operation.");
        var request = Current;
        if (request?.RuntimeOnly == true) request.AssertRequest();
        if (Current is { RuntimeOnly: false } parent)
        {
            if (!Reservations.Covers(parent.Operation, keys) &&
                !AuditUncovered($"{origin} expands {parent.Origin}: {string.Join(",", keys)}"))
                throw new InvalidOperationException($"{origin} expands {parent.Origin}'s reservations after mutation admission.");
            return new(this, permit, parent, owns: false);
        }
        long operation = ++_operation;
        long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        bool deferred = false;
        string? blockedBy = null;
        try
        {
            while (!Reservations.TryReserve(operation, keys))
            {
                if (!deferred)
                {
                    Deferred++; deferred = true; DeferredForTest?.Invoke(origin);
                    blockedBy = BlockerLabel(keys);
                }
                await Reservations.Changed.WaitAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (request?.RuntimeOnly == true) request.AssertRequest();
            }
        }
        catch { Reservations.Abandon(operation); throw; }
        if (deferred) { RecordQueueAgeCore(queuedAt); RecordWait(realmLabel ?? origin, blockedBy!, queuedAt); }
        _origins[operation] = realmLabel ?? origin;
        return new(this, permit, new OwnerContext(this, operation, ++_admissionSequence, origin)
        {
            RequestCurrent = request?.RequestCurrent, RequestSession = request?.RequestSession,
            RealmAdmittedAt = RealmAdmitted(realmLabel ?? origin, keys, deferred ? queuedAt : 0),
            RealmLabel = realmLabel ?? origin,
        }, owns: true);
    }
    /// <summary>One operation id across a caller's own admission retries, so a writer mark (ReservationCoordinator)
    /// survives them. Dispose it when the loop ends: a retry that never got in drops its mark.</summary>
    internal sealed class AdmissionRetry(OwnerRegistry registry, long operation) : IDisposable
    {
        internal long Operation => operation;
        internal long QueuedAt;
        internal string? BlockedBy;
        public void Dispose() => registry.Reservations.Abandon(operation);
    }
    internal AdmissionRetry Retries() { AssertLoop(); return new(this, ++_operation); }
    internal LegacyOwnerScope? TryEnter(string origin, params ReservationKey[] keys) => TryEnter(origin, keys, null);
    /// <summary>One attempt; with <paramref name="retry"/> the wait is recorded once, at admission.</summary>
    internal LegacyOwnerScope? TryEnter(string origin, ReservationKey[] keys, AdmissionRetry? retry)
    {
        AssertLoop();
        var permit = _loop.CurrentWork ?? throw new InvalidOperationException("Owner scope needs an admitted operation.");
        var request = Current;
        if (request?.RuntimeOnly == true) request.AssertRequest();
        if (Current is { RuntimeOnly: false } parent)
        {
            if (!Reservations.Covers(parent.Operation, keys) &&
                !AuditUncovered($"{origin} (try) expands {parent.Origin}: {string.Join(",", keys)}"))
                throw new InvalidOperationException("Nested ticker expanded its scope.");
            return new(this, permit, parent, owns: false);
        }
        long operation = retry?.Operation ?? ++_operation;
        if (!Reservations.TryReserve(operation, keys))
        {
            if (retry == null)
            {
                Reservations.Abandon(operation);
                Deferred++; DeferredForTest?.Invoke(origin);
                // A retry loop records its wait at admission (RecordQueueAge); note who blocked this attempt.
                NoteBlocked(origin, BlockerLabel(keys));
            }
            else if (retry.QueuedAt == 0)
            {
                retry.QueuedAt = System.Diagnostics.Stopwatch.GetTimestamp(); retry.BlockedBy = BlockerLabel(keys);
                Deferred++; DeferredForTest?.Invoke(origin);
            }
            return null;
        }
        if (retry is { QueuedAt: not 0 }) { RecordQueueAgeCore(retry.QueuedAt); RecordWait(origin, retry.BlockedBy, retry.QueuedAt); }
        _origins[operation] = origin;
        return new(this, permit, new OwnerContext(this, operation, ++_admissionSequence, origin)
        {
            RequestCurrent = request?.RequestCurrent, RequestSession = request?.RequestSession,
            RealmAdmittedAt = RealmAdmitted(origin, keys, 0),
            RealmLabel = origin,
        }, owns: true);
    }
    /// <summary>Declare the complete scope together with an old semaphore. Independent intents do not
    /// retain owner keys while queued at that gate. At wake-up, TryEnter either admits both in the same
    /// loop turn or releases the gate before waiting for an owner. The caller releases the gate.</summary>
    internal async Task<LegacyOwnerScope> EnterWithGateAsync(string origin, SemaphoreSlim gate,
        ReservationKey[] keys, CancellationToken cancellation = default)
    {
        AssertLoop();
        if (Current is { RuntimeOnly: false })
        {
            var borrowed = await EnterAsync(origin, keys, cancellation);
            try { await gate.WaitAsync(cancellation); return borrowed; }
            catch { borrowed.Dispose(); throw; }
        }
        using var retry = Retries();
        while (true)
        {
            await gate.WaitAsync(cancellation);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (TryEnter(origin, keys, retry) is { } scope) return scope;
            }
            catch { gate.Release(); throw; }
            var changed = Reservations.Changed;
            gate.Release();
            await changed.WaitAsync(cancellation);
        }
    }
    public async Task RunAsync(string origin, ReservationKey[] keys, Func<Task> work)
    { using var scope = await EnterAsync(origin, keys); await work(); }
    /// <summary>Combat/scene continuations retain their existing runtime guards. Their durable sections
    /// must enter an exclusive scope; a suspended publication cannot reserve resources needed to finish itself.</summary>
    internal async Task RunRuntimeAsync(string origin, Func<Task> work)
    {
        AssertLoop();
        var permit = _loop.CurrentWork ?? throw new InvalidOperationException("Runtime intent needs an admitted operation.");
        if (Current is { RuntimeOnly: false })
            throw new InvalidOperationException("A runtime root cannot borrow an exclusive operation.");
        long operation = ++_operation;
        if (!Reservations.TryReserve(operation, [])) throw new InvalidOperationException("Empty runtime declaration conflicted.");
        using var scope = new LegacyOwnerScope(this, permit, new OwnerContext(this, operation, ++_admissionSequence, origin, runtimeOnly: true), owns: true);
        RuntimeIntents++;
        await work();
    }
    internal Task RunEconomicRequestAsync(GameSession session, Func<Task> work)
    {
        var role = session.Role;
        var owner = session.Owner;
        var attachment = session.Attachment;
        return RunRuntimeAsync("dispatcher/economic request", async () =>
        {
            var context = Current!;
            context.RequestSession = session;
            context.RequestCurrent = () => ReferenceEquals(session.Role, role) && ReferenceEquals(session.Owner, owner)
                && ReferenceEquals(session.Attachment, attachment) && attachment?.Cancelled != true;
            await work();
        });
    }
    internal async Task RunSessionAsync(string origin, ReservationKey[] keys, GameSession session, Func<Task> work,
        string? realmLabel = null)
    {
        var owner = session.Owner;
        long? role = session.Role?.Id, epoch = owner?.LifecycleEpoch;
        var attachment = session.Attachment;
        using var scope = await EnterAsync(origin, keys, CancellationToken.None, realmLabel);
        if (session.Role?.Id != role || !ReferenceEquals(session.Owner, owner) ||
            !ReferenceEquals(session.Attachment, attachment) || attachment?.Cancelled == true || owner?.LifecycleEpoch != epoch)
            throw new OperationCanceledException("Role attachment changed while the legacy intent waited for its reservation.");
        if (owner != null) scope.Context.Pin(owner);
        await work();
    }
    public async Task<T> RunAsync<T>(string origin, ReservationKey[] keys, Func<Task<T>> work)
    { using var scope = await EnterAsync(origin, keys); return await work(); }
    public object Snapshot() => new
    {
        conflicts = Reservations.Conflicts, deferred = Deferred, maxQueueAgeMs = MaxQueueAgeMs,
        totalQueueWaitMs = TotalQueueWaitMs, realmScopes = RealmScopes, runtimeIntents = RuntimeIntents,
        handovers = HandoverCount, handoverConflicts = HandoverConflicts, realmByOrigin = RealmByOrigin(), waitByOrigin = WaitByOrigin()
    };

    private object RealmByOrigin()
    {
        lock (_realmByOrigin)
            return _realmByOrigin.OrderByDescending(x => x.Value.HeldMs + x.Value.WaitMs).Take(40)
                .ToDictionary(x => x.Key, x => new
                {
                    count = x.Value.Count, waitMs = Math.Round(x.Value.WaitMs, 1),
                    heldMs = Math.Round(x.Value.HeldMs, 1), maxHeldMs = Math.Round(x.Value.MaxHeldMs, 1)
                });
    }
}
