using System.Text.Json;
using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>A complete, detached role/item transaction. Each child owns copied values and runs on the writer.</summary>
internal sealed class RoleItemPlan : DbPlan
{
    private readonly DbPlan[] _guards;
    private readonly DbPlan[] _parts;
    private readonly string _label;
    private readonly string? _operationId;
    internal RoleItemPlan(IReadOnlyList<DbPlan> parts, string label, IReadOnlyList<DbPlan>? guards = null, string? operationId = null)
    {
        _guards = guards?.ToArray() ?? [];
        _parts = parts.ToArray();
        _label = label;
        _operationId = operationId;
    }
    internal override string? OperationId => _operationId;
    internal override int Bytes => 128 + _guards.Sum(part => part.Bytes) + _parts.Sum(part => part.Bytes);
    internal int ChildCount => _parts.Length;
    internal IReadOnlyList<DbPlan> Parts => _parts;
    internal IReadOnlyList<DbPlan> Guards => _guards;
    internal override string Label => _label;
    internal override void ResolveRoleWrites(AccountStore accounts)
    {
        foreach (var part in _parts) part.ResolveRoleWrites(accounts);
    }
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => _parts.SelectMany(part => part.RoleWrites).Distinct().ToArray();
    internal override IReadOnlyList<long> RolePins => _guards.Concat(_parts).SelectMany(part => part.RolePins).Distinct().ToArray();
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => _parts.SelectMany(part => part.EntityWrites).Distinct().ToArray();
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites =>
        _parts.SelectMany(part => part.EntityDeltaWrites).Distinct().ToArray();
    internal override IReadOnlyList<RoleRevisionKey> MutatedRoleWrites(object? result)
    {
        var receipts = (object?[])result!;
        return _parts.SelectMany((part, index) => part.MutatedRoleWrites(receipts[index])).Distinct().ToArray();
    }
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityWrites(object? result)
    {
        var receipts = (object?[])result!;
        return _parts.SelectMany((part, index) => part.MutatedEntityWrites(receipts[index])).Distinct().ToArray();
    }
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityDeltaWrites(object? result)
    {
        var receipts = (object?[])result!;
        return _parts.SelectMany((part, index) => part.MutatedEntityDeltaWrites(receipts[index])).Distinct().ToArray();
    }

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var guard in _guards) { DbWriteGuard.Plan(db, guard); guard.Execute(db, tx); }
        var receipts = new object?[_parts.Length];
        for (int i = 0; i < _parts.Length; i++)
        {
            DbWriteGuard.Plan(db, _parts[i]);
            receipts[i] = _parts[i].Execute(db, tx);
        }
        return receipts;
    }
}

/// <summary>Caller-side transaction capture. The builder itself never enters the writer queue.</summary>
public sealed class RoleItemPlanBuilder
{
    private readonly AccountStore? _accounts;
    private readonly Dictionary<long, RoleRevisionVector> _expected = [];
    private readonly Dictionary<long, HashSet<string>> _capturedParts = [];
    private readonly Dictionary<OwnerRevisionKey, OwnerRevisionValue> _entities = [];
    private readonly List<DbPlan> _guards = [];
    private readonly List<DbPlan> _parts = [];
    private readonly List<Action<object?>?> _apply = [];
    private readonly Dictionary<(long Role, string Column), byte[]?> _blobs = [];
    private EconomyMutation? _economy;
    private EscrowEconomyIntent? _escrowEconomy;
    private readonly List<(EconomyAsset Asset, long Quantity, EconomyReason Reason,
        EconomyCompartment? Compartment, EconomyOwner? Owner)> _escrowSinks = [];
    private EconomyEnvelope? _economyEnvelope;
    private EventEconomyMutation? _eventEconomy;
    private bool _asyncSubmitted;

    public RoleItemPlanBuilder() { }
    internal RoleItemPlanBuilder(AccountStore accounts) => _accounts = accounts;

    /// <summary>Capture an initiating role's lifecycle without pretending its parts changed.</summary>
    internal void PinRole(long role) => Guard(new RoleLifecyclePinPlan(role));

    /// <summary>Use an owner-captured vector when staging began before this builder existed.</summary>
    public void ExpectRole(RoleRevisionVector vector)
    {
        if (_expected.ContainsKey(vector.RoleId)) throw new InvalidOperationException("Role expectation was already captured.");
        _expected.Add(vector.RoleId, new RoleRevisionVector(vector.RoleId, vector.Epoch, vector.Deleted,
            vector.Parts.ToFrozenDictionary(StringComparer.Ordinal)));
        _capturedParts.Add(vector.RoleId, vector.Parts.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private void BindRoleSource(RoleRevisionVector source, string part)
    {
        if (_expected.TryGetValue(source.RoleId, out var prior))
        {
            if (prior.Epoch != source.Epoch || prior.Deleted != source.Deleted)
                throw new OwnerRevisionConflictException();
            var captured = _capturedParts[source.RoleId];
            if (captured.Contains(part))
            {
                if (prior.Revision(part) != source.Revision(part)) throw new OwnerRevisionConflictException();
            }
            else
            {
                _expected[source.RoleId] = prior.WithRevision(part, source.Revision(part));
                captured.Add(part);
            }
        }
        else
        {
            var current = Vector(_accounts ?? throw new InvalidOperationException("Progress source binding needs a bound AccountStore."),
                source.RoleId, [part]);
            if (current.Epoch != source.Epoch || current.Deleted != source.Deleted)
                throw new OwnerRevisionConflictException();
            _expected.Add(source.RoleId, current.WithRevision(part, source.Revision(part)));
            _capturedParts.Add(source.RoleId, new HashSet<string>(StringComparer.Ordinal) { part });
        }
    }

    internal void BindProgressSource(long role, RoleProgress progress)
    {
        if (progress.SourceRevision is not { } source) return;
        if (source.RoleId != role) throw new InvalidOperationException("Progress source role does not match write role.");
        BindRoleSource(source, RevisionStore.Progress);
    }

    internal RoleRevisionVector CommittedVector(long role, string part) => _expected[role].Advance(part);

    internal OwnerRevisionValue CaptureEntity(OwnerRevisionKey key, OwnerRevisionValue? source = null)
    {
        if (source is { } snapshot && snapshot.Key != key)
            throw new InvalidOperationException("Entity snapshot key does not match the staged write.");
        if (_entities.TryGetValue(key, out var prior))
        {
            if (source is { } captured && captured != prior) throw new OwnerRevisionConflictException();
            return prior;
        }
        var value = source ?? (_accounts ?? throw new InvalidOperationException("Entity capture needs a bound AccountStore."))
            .ReadSync<OwnerRevisionValue>(new OwnerRevisionReadPlan(key));
        _entities.Add(key, value);
        return value;
    }

    internal void Guard(DbPlan guard)
    {
        if (_economyEnvelope is not null) throw new InvalidOperationException("E05d child intent is already frozen.");
        if (guard is RoleBlobGuardPlan { CapturedVector: { } blobSource } blob)
            BindRoleSource(blobSource, blob.Column);
        if (guard is MarketRecordGuardPlan { CapturedRevision: { } collection })
        {
            if (_entities.TryGetValue(collection.Key, out var prior) && prior != collection)
                throw new OwnerRevisionConflictException();
            _entities.TryAdd(collection.Key, collection);
        }
        if (guard is MarketRoleGuardPlan market)
        {
            BindRoleSource(market.CapturedVector, RevisionStore.Progress);
            BindRoleSource(market.CapturedVector, RevisionStore.Inventory);
        }
        Capture(guard);
        _guards.Add(guard);
    }

    private void Capture(DbPlan part)
    {
        if (_accounts is null)
        {
            if (part is ItemWritePlan)
                throw new InvalidOperationException("Item owner lookup requires a bound AccountStore.");
            if (part.RolePins.Any(role => !_expected.ContainsKey(role)) ||
                part.RoleWrites.Any(key => !_expected.ContainsKey(key.RoleId)) || part.EntityWrites.Count != 0)
                throw new InvalidOperationException("A staged role/entity plan needs a bound AccountStore or prior role vectors.");
            return;
        }
        var accounts = _accounts;
        part.ResolveRoleWrites(accounts);
        // A staged login blob this part writes is committed before any of its revisions is read below (as RoleBlob
        // does): flushing it at Prepare, after a progress source already pinned the role, left that part one behind.
        foreach (var key in part.RoleWrites) accounts.FlushStaged(key.RoleId, key.Part);
        var newRoles = new HashSet<long>();
        foreach (long role in part.RolePins.Concat(part.RoleWrites.Select(key => key.RoleId)).Distinct())
            if (!_expected.ContainsKey(role))
            {
                _expected.Add(role, Vector(accounts, role, part.RoleWrites.Where(key => key.RoleId == role).Select(key => key.Part)));
                _capturedParts.Add(role, new HashSet<string>(StringComparer.Ordinal));
                newRoles.Add(role);
            }
        foreach (var group in part.RoleWrites.GroupBy(key => key.RoleId))
        {
            var captured = _capturedParts[group.Key];
            string[] freshParts = group.Select(key => key.Part).Distinct()
                .Where(partName => !captured.Contains(partName)).ToArray();
            if (freshParts.Length == 0) continue;
            var current = newRoles.Contains(group.Key) ? _expected[group.Key] : Vector(accounts, group.Key, freshParts);
            var prior = _expected[group.Key];
            if (current.Epoch != prior.Epoch || current.Deleted != prior.Deleted)
                throw new OwnerRevisionConflictException();
            foreach (string partName in freshParts)
            {
                prior = prior.WithRevision(partName, current.Revision(partName));
                captured.Add(partName);
            }
            _expected[group.Key] = prior;
        }
        foreach (var key in part.EntityWrites.Distinct())
            CaptureEntity(key);
    }

    // C/D 16: an attached role's vector comes from its owner, which records every commit made on the loop (exact for
    // parts written under a reservation: legacy captures saw 6500 hits and 0 stale vectors in S1; commits made off the loop mark their roles, see
    // AccountStore.RecordResolvedCommit). Parts the writer's
    // lazy lane commits outside reservations (public position, settings) are still read from SQL.
    private static readonly HashSet<string> LazyParts = new(StringComparer.Ordinal) { RevisionStore.Position, "settings" };
    private static RoleRevisionVector Vector(AccountStore accounts, long role, IEnumerable<string>? parts = null) =>
        accounts.RoleVector(role, sqlOnly: parts != null && parts.Any(LazyParts.Contains));

    private void CaptureRole(long role)
    {
        if (_expected.ContainsKey(role)) return;
        var accounts = _accounts ?? throw new InvalidOperationException("A role read-through needs a bound AccountStore.");
        _expected.Add(role, Vector(accounts, role));
        _capturedParts.Add(role, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Read-your-writes capture, guarded against the original committed bytes before any writes.</summary>
    internal byte[]? ReadRoleBlob(AccountStore accounts, long role, string column)
    {
        RoleColumns.Validate(column);
        accounts.FlushStaged(role, column); // a staged login blob is written before this reads and guards it
        CaptureRole(role);
        var key = (role, column);
        if (!_blobs.TryGetValue(key, out var value))
        {
            var original = accounts.ReadSync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(role, column));
            Guard(new RoleBlobGuardPlan(role, column, original));
            _blobs.Add(key, value = original.Value);
        }
        return value is null ? null : (byte[])value.Clone();
    }

    internal int Add(DbPlan part, Action<object?>? applyReceipt = null)
    {
        if (_economyEnvelope is not null) throw new InvalidOperationException("E05d child intent is already frozen.");
        Capture(part);
        _parts.Add(part);
        _apply.Add(applyReceipt);
        return _parts.Count - 1;
    }

    internal int RoleBlob(long role, string column, byte[] value)
    {
        _accounts?.FlushStaged(role, column); // before this plan captures the part's revision
        _blobs[(role, column)] = (byte[])value.Clone();
        return Add(new RoleBlobWritePlan(role, column, value));
    }
    internal int Progress(long role, RoleProgress value)
    {
        BindProgressSource(role, value);
        return Add(new RoleProgressWritePlan(role, value), _ =>
            value.SourceRevision = _expected[role].Advance(RevisionStore.Progress));
    }
    internal int Items(long role, IReadOnlyList<InvItem> value) => Add(new ItemSnapshotWritePlan(role, value));
    internal int ItemDelta(long role, IReadOnlyList<InvItem> before, IReadOnlyList<InvItem> after) =>
        Add(new ItemDeltaWritePlan(role, before, after));
    internal int ItemUpdate(InvItem value) => Add(new ItemWritePlan(ItemWriteKind.Update, items: [value]));
    internal int FriendMetadata(long role, byte[] value) => Add(new FriendGiftWritePlan(role, 0, value, 0));
    internal int FriendIntimacy(long role, long other, int value) => Add(new FriendGiftWritePlan(role, other, null, value));

    internal void TaskFollowup(long role, string source)
    {
        byte[]? prior = ReadRoleBlob(_accounts ?? throw new InvalidOperationException("Unbound task followup."), role, "task_followup");
        var pending = prior is { Length: > 0 } ? JsonSerializer.Deserialize<HashSet<string>>(prior)! : [];
        pending.Add(source);
        RoleBlob(role, "task_followup", JsonSerializer.SerializeToUtf8Bytes(pending));
    }

    /// <summary>Bind a detached semantic purchase to the already-staged item/progress/quota children.</summary>
    public RoleItemPlanBuilder WithEconomy(EconomyMutation mutation)
    {
        if (HasEconomy || _escrowSinks.Count != 0)
            throw new InvalidOperationException("Economy envelope is already set.");
        _economy = mutation ?? throw new ArgumentNullException(nameof(mutation));
        return this;
    }

    /// <summary>Bind a copied E03 intent after all nested append/purge plans have staged their exact sinks.</summary>
    public RoleItemPlanBuilder WithEscrowEconomy(EscrowEconomyIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (HasEconomy)
            throw new InvalidOperationException("Economy envelope is already set.");
        if (intent.ActorRoleId > 0) CaptureRole(intent.ActorRoleId);
        var bound = intent.BindCopy(_escrowSinks.Select(sink => new EscrowExternalChange(
            EconomyLegKind.Sink, sink.Asset, sink.Quantity, sink.Reason, sink.Compartment, sink.Owner)));
        _escrowSinks.Clear();
        _escrowEconomy = bound;
        return this;
    }

    /// <summary>Capture an expired attachment removed by a nested mail helper before its parent binds E03.</summary>
    public void EscrowSink(EconomyAsset asset, long quantity, EconomyReason reason,
        EconomyCompartment? compartment = null, EconomyOwner? owner = null)
    {
        if (HasEconomy || quantity <= 0 ||
            !EconomyReasonCatalog.Allows(reason, EconomyLegKind.Sink))
            throw new InvalidOperationException("Invalid or late E03 deferred sink.");
        _escrowSinks.Add((asset, quantity, reason, compartment, owner));
    }

    /// <summary>Bind E05d semantic effects to already-staged typed owner children.</summary>
    public RoleItemPlanBuilder WithEconomy(EconomyEnvelope envelope)
    {
        if (HasEconomy || _escrowSinks.Count != 0) throw new InvalidOperationException("Economy envelope is already set.");
        ArgumentNullException.ThrowIfNull(envelope);
        _economyEnvelope = envelope.BindChildIntent(EconomyChildIntent.Capture(_guards, _parts));
        return this;
    }

    internal bool HasEconomy => _economy is not null || _economyEnvelope is not null ||
        _eventEconomy is not null || _escrowEconomy is not null;
    internal bool HasEscrowEconomy => _escrowEconomy is not null;
    internal bool HasAsyncFamilyEconomy => _economy?.IsFamily == true || _economy?.IsProgression == true ||
        _escrowEconomy is not null || _eventEconomy is not null;
    internal bool HasCharacterFamilyEconomy => _economy is not null && CharacterFamilyEconomy.IsKind(_economy.CommandKind);

    /// <summary>E05e opts in only converted event commands; remaining families keep their existing guarded route.</summary>
    public RoleItemPlanBuilder WithEventEconomy(EventEconomyMutation mutation)
    {
        if (HasEconomy || _escrowSinks.Count != 0) throw new InvalidOperationException("Economy envelope is already set.");
        _eventEconomy = mutation ?? throw new ArgumentNullException(nameof(mutation));
        return this;
    }

    internal object?[] Commit(AccountStore accounts, string label)
    {
        if (_escrowSinks.Count != 0) throw new InvalidOperationException("Deferred E03 sinks require an E03 envelope.");
        if (_eventEconomy != null) throw new InvalidOperationException("Converted events must use owned asynchronous commit.");
        var plan = Build(accounts, label, expect: !HasEconomy);
        if (_economy?.IsProgression == true)
            _economy = _economy.BindProgressionPlan(EventPlanFingerprint.Capture(plan));
        if (_escrowEconomy is not null)
        {
            var escrowSubmitted = new EscrowEconomyTransactionPlan(plan, _escrowEconomy, accounts.EscrowCatalog,
                accounts.ConsumeEconomyAcknowledgementFaultForTest());
            if (_expected.Count != 0) escrowSubmitted.Expect(_expected.Values);
            if (_entities.Count != 0) escrowSubmitted.ExpectEntities(_entities.Values);
            object?[] result;
            try { result = accounts.WriteSync<object?[]>(escrowSubmitted); }
            catch (EconomyOperationConflictException) { throw; }
            catch (Exception original)
            {
                object?[]? resolved;
                try { resolved = accounts.ReadSync<object?[]?>(new EscrowEconomyReceiptResolveReadPlan(_escrowEconomy, plan)); }
                catch (EconomyOperationConflictException) { throw; }
                catch (Exception readError)
                {
                    accounts.RequireOwnerRecovery(escrowSubmitted, _escrowEconomy.OperationId);
                    throw new EconomyOutcomeUnknownException(_escrowEconomy.OperationId, original, readError);
                }
                if (resolved is null) throw;
                result = resolved;
                try { accounts.RecordResolvedCommit(escrowSubmitted, result); }
                catch { accounts.RequireOwnerRecovery(escrowSubmitted, _escrowEconomy.OperationId); throw; }
            }
            try { Apply(result); }
            catch { accounts.RequireOwnerRecovery(escrowSubmitted, _escrowEconomy.OperationId); throw; }
            return result;
        }
        if (!HasEconomy)
        {
            object?[] plain = accounts.WriteSync<object?[]>(plan);
            Apply(plain);
            return plain;
        }
        // E01: the economy envelope carries the captured vectors and resolves an unknown outcome from its receipt.
        // Envelopes: E05d semantic, 13c progression, E05b/13a family, E04 reward, 13e owned exchange and E01 NPC.
        DbPlan submitted = _economyEnvelope is { } envelope
            ? new EconomyEnvelopePlan(plan, envelope, accounts.EconomyCatalog,
                accounts.ConsumeEconomyAcknowledgementFaultForTest())
            : _economy!.IsProgression
                ? new ProgressionEconomyTransactionPlan(plan, _economy, accounts.ConsumeEconomyAcknowledgementFaultForTest())
            : _economy.IsFamily
                ? new FamilyEconomyTransactionPlan(plan, _economy, accounts.ConsumeEconomyAcknowledgementFaultForTest())
                : new EconomyTransactionPlan(plan, _economy, accounts.ConsumeEconomyAcknowledgementFaultForTest());
        if (_expected.Count != 0) submitted.Expect(_expected.Values);
        if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
        object?[] receipts;
        try { receipts = accounts.WriteSync<object?[]>(submitted); }
        catch (Exception original)
        {
            object?[]? resolved;
            try
            {
                if (accounts.ConsumeEconomyResolutionFaultForTest())
                    throw new IOException("Test-only economy receipt resolution failure.");
                int expectedIds = _parts.Count > 0 && _parts[0] is ItemDeltaWritePlan delta
                    ? delta.ExpectedReceiptCount : -1;
                resolved = _economyEnvelope is { } e05
                    ? accounts.ReadSync<object?[]?>(new EconomyEnvelopeResolveReadPlan(e05, _parts.Count))
                    : accounts.ReadSync<object?[]?>(new EconomyReceiptResolveReadPlan(_economy!, expectedIds,
                        _economy!.IsFamily || _economy.IsProgression ? _parts.Count : 0));
            }
            catch (EconomyOperationConflictException) { throw; }
            catch (Exception readError)
            {
                throw new EconomyOutcomeUnknownException((_economyEnvelope?.OperationId ?? _economy!.OperationId), original, readError);
            }
            if (resolved is null) throw;
            receipts = resolved;
            accounts.RecordResolvedCommit(submitted, receipts);
        }
        Apply(receipts);
        return receipts;
    }

    /// <summary>All captured participants, including read guards. WholeRole preserves legacy
    /// hook compatibility while removing unrelated roles from market/auction operations. An entity that is only a
    /// read guard is held shared (C/D 16, S3: market queries serialized behind each other on the collection key);
    /// a written one exclusively. The guard's revision check still refuses a read that a writer overtook.</summary>
    internal Game.Ownership.ReservationKey[] ReservationKeys
    {
        get
        {
            var written = _parts.SelectMany(part => part.EntityWrites.Concat(part.EntityDeltaWrites)).ToHashSet();
            return _expected.Keys.Select(role => Game.Ownership.ReservationKey.Role(role))
                .Concat(_entities.Keys.Concat(written).Distinct().Select(key => written.Contains(key)
                    ? Game.Ownership.ReservationKey.Entity(key.Kind, key.Id, key.Part)
                    : Game.Ownership.ReservationKey.SharedEntity(key.Kind, key.Id))).ToArray();
        }
    }

    internal async Task<object?[]> CommitAsync(AccountStore accounts, string label, Action<object?[]>? publish = null)
    {
        if (_escrowSinks.Count != 0) throw new InvalidOperationException("Deferred E03 sinks require an E03 envelope.");
        if (_asyncSubmitted) throw new InvalidOperationException("An owner plan can only be submitted once.");
        if (_economy is not null && !_economy.IsFamily && !_economy.IsProgression && _economy.RewardEvidence is null &&
            _economy.CommandKind != "npc-shop-buy" && !_economy.CommandKind.StartsWith("cd13e/", StringComparison.Ordinal))
            throw new NotSupportedException("The economic command has no asynchronous owner adapter.");
        _asyncSubmitted = true;
        DbPlan submitted;
        object?[] receipts;
        if (_escrowEconomy is not null)
        {
            var plan = Build(accounts, label, expect: false);
            submitted = new EscrowEconomyTransactionPlan(plan, _escrowEconomy, accounts.EscrowCatalog,
                accounts.ConsumeEconomyAcknowledgementFaultForTest());
            if (_expected.Count != 0) submitted.Expect(_expected.Values);
            if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
            receipts = await accounts.WriteEscrowEconomyAsync(submitted, _escrowEconomy, plan);
        }
        else if (_economyEnvelope is { } envelope)
        {
            // E05d semantic envelope on the owned path; an ambiguous outcome resolves from its receipt.
            submitted = new EconomyEnvelopePlan(Build(accounts, label, expect: false), envelope, accounts.EconomyCatalog,
                accounts.ConsumeEconomyAcknowledgementFaultForTest());
            if (_expected.Count != 0) submitted.Expect(_expected.Values);
            if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
            try { receipts = await accounts.WriteOwnedAsync<object?[]>(submitted); }
            catch (Exception original) when (original is not EconomyOperationConflictException)
            {
                object?[]? resolved;
                try
                {
                    if (accounts.ConsumeEconomyResolutionFaultForTest())
                        throw new IOException("Test-only economy receipt resolution failure.");
                    resolved = await accounts.ReadAsync<object?[]?>(new EconomyEnvelopeResolveReadPlan(envelope, _parts.Count));
                }
                catch (Exception readError)
                {
                    accounts.RequireOwnerRecovery(submitted);
                    throw new EconomyOutcomeUnknownException(envelope.OperationId, original, readError);
                }
                if (resolved is null) throw;
                receipts = resolved;
                accounts.RecordResolvedCommit(submitted, receipts);
            }
        }
        else if (_eventEconomy is not null)
        {
            // E05e is a further envelope; it retains its own typed replay and source validation.
            submitted = new EventEconomyTransactionPlan(Build(accounts, label, expect: false), _eventEconomy,
                accounts.ConsumeEconomyAcknowledgementFaultForTest());
            if (_expected.Count != 0) submitted.Expect(_expected.Values);
            if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
            receipts = await accounts.WriteOwnedAsync<object?[]>(submitted);
        }
        else if (_economy is null)
        {
            submitted = Build(accounts, label, Guid.NewGuid().ToString("N"));
            receipts = await accounts.WriteOwnedAsync<object?[]>(submitted);
        }
        else if (_economy.IsProgression)
        {
            var plan = Build(accounts, label, expect: false);
            _economy = _economy.BindProgressionPlan(EventPlanFingerprint.Capture(plan));
            submitted = new ProgressionEconomyTransactionPlan(plan, _economy,
                accounts.ConsumeEconomyAcknowledgementFaultForTest());
            if (_expected.Count != 0) submitted.Expect(_expected.Values);
            if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
            receipts = await accounts.WriteFamilyEconomyAsync(submitted, _economy,
                ((ItemDeltaWritePlan)_parts[0]).ExpectedReceiptCount, _parts.Count);
        }
        else if (_economy.IsFamily || _economy.CommandKind == "npc-shop-buy")
        {
            // E05a/b and E01 keep their original receipt shapes and resolve ambiguous acknowledgements.
            var child = Build(accounts, label, expect: false);
            bool fault = accounts.ConsumeEconomyAcknowledgementFaultForTest();
            submitted = _economy.IsFamily
                ? new FamilyEconomyTransactionPlan(child, _economy, fault)
                : new EconomyTransactionPlan(child, _economy, fault);
            if (_expected.Count != 0) submitted.Expect(_expected.Values);
            if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
            receipts = await accounts.WriteFamilyEconomyAsync(submitted, _economy,
                ((ItemDeltaWritePlan)_parts[0]).ExpectedReceiptCount, _economy.IsFamily ? _parts.Count : 0);
        }
        else
        {
            // E04 reward envelope on the owned asynchronous path.
            submitted = new EconomyTransactionPlan(Build(accounts, label, expect: false), _economy,
                accounts.ConsumeEconomyAcknowledgementFaultForTest(), owned: true);
            if (_expected.Count != 0) submitted.Expect(_expected.Values);
            if (_entities.Count != 0) submitted.ExpectEntities(_entities.Values);
            receipts = _economy.CommandKind.StartsWith("cd13e/", StringComparison.Ordinal)
                ? await accounts.WriteFamilyEconomyAsync(submitted, _economy,
                    _parts[0] is ItemDeltaWritePlan ownedDelta ? ownedDelta.ExpectedReceiptCount : -1, _parts.Count)
                : await accounts.WriteOwnedAsync<object?[]>(submitted);
        }
        try { Apply(receipts); publish?.Invoke(receipts); }
        catch { accounts.RequireOwnerRecovery(submitted, _economy?.OperationId ?? _economyEnvelope?.OperationId ?? _eventEconomy?.OperationId ?? _escrowEconomy?.OperationId); throw; }
        return receipts;
    }

    private void Apply(object?[] receipts)
    {
        for (int i = 0; i < receipts.Length; i++) _apply[i]?.Invoke(receipts[i]);
    }

    private RoleItemPlan Build(AccountStore accounts, string label, string? operationId = null, bool expect = true)
    {
        var plan = new RoleItemPlan(_parts, label, _guards, operationId);
        plan.ResolveRoleWrites(accounts);
        if (plan.RolePins.Any(role => !_expected.ContainsKey(role)) ||
            plan.RoleWrites.Any(key => !_expected.ContainsKey(key.RoleId)))
            throw new InvalidOperationException($"Role-writing builder {label} lacks a captured role vector.");
        if (plan.EntityWrites.Any(key => !_entities.ContainsKey(key)))
            throw new InvalidOperationException($"Entity-writing builder {label} lacks a captured entity vector.");
        _escrowEconomy?.BindChildRoleWrites(plan.Parts.Select(part => part.RoleWrites
            .Select(key => new EscrowRolePart(key.RoleId, key.Part))));
        if (expect && _expected.Count != 0) plan.Expect(_expected.Values);
        if (expect && _entities.Count != 0) plan.ExpectEntities(_entities.Values);
        return plan;
    }
}

internal sealed class RoleLifecyclePinPlan(long role) : DbPlan
{
    internal override int Bytes => 24;
    internal override string Label => "role/lifecycle pin";
    internal override IReadOnlyList<long> RolePins => [role];
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx) => null;
}

internal static class RoleColumns
{
    internal static readonly HashSet<string> Blobs = new(StringComparer.Ordinal)
    {
        "settings", "guides", "emoji_play", "achievements", "achievement_runtime", "adventure_group", "library", "card_plans", "shop", "trade_info", "stalls_info", "daily",
        "new_activities", "activity_runtime", "timed_activity", "timed_battle", "training", "guild_challenge",
        "lottery", "tower", "dungeons", "territory_wars", "territory_escort", "lifejob", "life_restrict",
        "life_runtime", "season_talent", "season_page", "season_page_meta", "season_refine", "pets", "mounts",
        "wardrobe", "totems", "totem_refine_epoch", "genre", "skill_plans", "pet_books", "equip_levels",
        "equip_plans", "auto_decompose", "bag_state", "stat_plans", "world_state", "emblems", "guild_info",
        "mailbox", "auction", "tasks", "task_runtime", "task_followup", "wild_afk", "career_special", "heroic_spirit", "paragon", "title", "area_concur", "growth_class", "first_heart", "personal", "personal_upvote", "popularity", "social_outfit", "emoji_info"
    };

    internal static void Validate(string column)
    {
        if (!Blobs.Contains(column)) throw new ArgumentOutOfRangeException(nameof(column));
    }
}

internal sealed class RoleBlobReadPlan(long role, string column) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "role/read-blob";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        RoleColumns.Validate(column);
        using var cmd = DbSql.Command(db, tx, $"SELECT {column} FROM role WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", role);
        return cmd.ExecuteScalar() is byte[] bytes ? (byte[])bytes.Clone() : null;
    }
}

internal sealed class RoleBlobWritePlan : DbPlan
{
    private readonly long _role;
    private readonly string _column;
    private readonly byte[] _value;
    internal string Column => _column;

    internal RoleBlobWritePlan(long role, string column, byte[] value)
    {
        RoleColumns.Validate(column);
        _role = role; _column = column; _value = (byte[])value.Clone();
    }
    internal override int Bytes => 128 + _value.Length;
    internal override string Label => "role/write-blob";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, _column)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, $"UPDATE role SET {_column}=$value WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", _role);
        cmd.Parameters.AddWithValue("$value", _value);
        int changed = cmd.ExecuteNonQuery();
        if (changed != 1 && _column == "mailbox") throw new InvalidOperationException($"Missing mail recipient role {_role}.");
        return changed;
    }
}

internal sealed record RoleProgressValue(int Career, int Level, int JobLevel, long Exp, long JobExp,
    string Stats, int BonusPoints, string Currency, int StarterKit, string Skills, string SkillBar, int ParagonLevel, long ParagonExp)
{
    internal static RoleProgressValue Capture(RoleProgress p) => new(p.Career, p.Level, p.JobLevel, p.Exp, p.JobExp,
        string.Join(',', p.Stats), p.BonusPoints, string.Join(',', p.Currency.Select(c => $"{c.Key}:{c.Value}")),
        p.StarterKit ? 1 : 0, string.Join(',', p.Skills.Select(s => $"{s.Key}:{s.Value}")),
        string.Join(',', p.SkillBar.Select(s => $"{s.Key}:{s.Value}")), p.ParagonLevel, p.ParagonExp);

    internal void Bind(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$career", Career); cmd.Parameters.AddWithValue("$level", Level);
        cmd.Parameters.AddWithValue("$jl", JobLevel); cmd.Parameters.AddWithValue("$exp", Exp);
        cmd.Parameters.AddWithValue("$jexp", JobExp); cmd.Parameters.AddWithValue("$stats", Stats);
        cmd.Parameters.AddWithValue("$bonus", BonusPoints); cmd.Parameters.AddWithValue("$cur", Currency);
        cmd.Parameters.AddWithValue("$kit", StarterKit); cmd.Parameters.AddWithValue("$skills", Skills);
        cmd.Parameters.AddWithValue("$bar", SkillBar);
        cmd.Parameters.AddWithValue("$plv", ParagonLevel); cmd.Parameters.AddWithValue("$pexp", ParagonExp);
    }
}

internal sealed class RoleProgressWritePlan(long role, RoleProgress value) : DbPlan
{
    private readonly RoleProgressValue _value = RoleProgressValue.Capture(value);
    internal override int Bytes => 256 + (_value.Stats.Length + _value.Currency.Length +
        _value.Skills.Length + _value.SkillBar.Length) * 2;
    internal override string Label => "role/write-progress";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.Progress)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            UPDATE role SET career=$career,level=$level,job_level=$jl,exp=$exp,job_exp=$jexp,stats=$stats,
                bonus_points=$bonus,currency=$cur,starter_kit=$kit,skills=$skills,skill_bar=$bar,
                paragon_level=$plv,paragon_exp=$pexp WHERE id=$id
            """);
        cmd.Parameters.AddWithValue("$id", role);
        _value.Bind(cmd);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>Serialized on the caller; no mutable InvItem, dictionary, or protobuf reaches the writer.</summary>
internal sealed record ItemValue(long Guid, int Entry, int Stack, int Pack, long Sort, int Grid,
    string? Attrs, bool IsLocked, string? Enchantments, string? StringAttrs, int BindType, long BindExpire)
{
    internal static ItemValue Capture(InvItem item) => new(item.Guid, item.Entry, item.Stack, item.Pack, item.Sort,
        item.Grid, item.Attrs is { Count: > 0 } ? JsonSerializer.Serialize(item.Attrs) : null, item.IsLocked,
        item.Enchantments.Count > 0 ? JsonSerializer.Serialize(item.Enchantments) : null,
        item.StringAttrs is { Count: > 0 } ? JsonSerializer.Serialize(item.StringAttrs) : null,
        item.BindType, item.BindExpire);

    internal void Bind(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$e", Entry); cmd.Parameters.AddWithValue("$s", Stack);
        cmd.Parameters.AddWithValue("$p", Pack); cmd.Parameters.AddWithValue("$o", Sort);
        cmd.Parameters.AddWithValue("$g", Grid); cmd.Parameters.AddWithValue("$a", (object?)Attrs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", IsLocked); cmd.Parameters.AddWithValue("$n", (object?)Enchantments ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sa", (object?)StringAttrs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bt", BindType); cmd.Parameters.AddWithValue("$be", BindExpire);
    }
}

internal sealed class ItemSnapshotWritePlan : DbPlan
{
    private readonly long _role;
    private readonly ItemValue[] _items;
    internal int ExpectedReceiptCount => _items.Length;
    internal ItemSnapshotWritePlan(long role, IReadOnlyList<InvItem> items)
    {
        _role = role;
        _items = items.Select(ItemValue.Capture).ToArray();
    }
    internal override int Bytes => 128 + _items.Sum(i => 160 + (i.Attrs?.Length ?? 0) * 2 +
        (i.Enchantments?.Length ?? 0) * 2 + (i.StringAttrs?.Length ?? 0) * 2);
    internal override string Label => "item/write-snapshot";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Inventory)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var old = new HashSet<long>();
        using (var query = DbSql.Command(db, tx, "SELECT guid FROM item WHERE role_id=$r"))
        {
            query.Parameters.AddWithValue("$r", _role);
            using var reader = query.ExecuteReader();
            while (reader.Read()) old.Add(reader.GetInt64(0));
        }
        foreach (var item in _items) if (item.Guid > 0) old.Remove(item.Guid);
        foreach (long guid in old)
        {
            using var delete = DbSql.Command(db, tx, "DELETE FROM item WHERE guid=$id AND role_id=$r");
            delete.Parameters.AddWithValue("$id", guid); delete.Parameters.AddWithValue("$r", _role);
            delete.ExecuteNonQuery();
        }
        var guids = new long[_items.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            using var cmd = DbSql.Command(db, tx, item.Guid > 0
                ? "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE guid=$id AND role_id=$r"
                : "INSERT INTO item(role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire) VALUES($r,$e,$s,$p,$o,$g,$a,$l,$n,$sa,$bt,$be) RETURNING guid");
            cmd.Parameters.AddWithValue("$r", _role);
            if (item.Guid > 0) cmd.Parameters.AddWithValue("$id", item.Guid);
            item.Bind(cmd);
            if (item.Guid > 0)
            {
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"missing award item {item.Guid}");
                guids[i] = item.Guid;
            }
            else guids[i] = (long)cmd.ExecuteScalar()!;
        }
        return guids;
    }

    internal static void ApplyGuids(IReadOnlyList<InvItem> items, long[] guids)
    {
        if (items.Count != guids.Length) throw new InvalidOperationException("item receipt length changed");
        for (int i = 0; i < items.Count; i++) items[i].Guid = guids[i];
    }
}

/// <summary>Only changed rows from a Character exchange; unrelated database items are left alone.</summary>
internal sealed class ItemDeltaWritePlan : DbPlan
{
    private readonly long _role;
    private readonly ItemValue[] _before, _after;
    internal int ExpectedReceiptCount => _after.Length;
    internal ItemDeltaWritePlan(long role, IReadOnlyList<InvItem> before, IReadOnlyList<InvItem> after)
    {
        _role = role;
        _before = before.Select(ItemValue.Capture).ToArray();
        _after = after.Select(ItemValue.Capture).ToArray();
    }
    internal override int Bytes => 128 + _before.Concat(_after).Sum(i => 160 + (i.Attrs?.Length ?? 0) * 2 +
        (i.Enchantments?.Length ?? 0) * 2 + (i.StringAttrs?.Length ?? 0) * 2);
    internal override string Label => "item/write-delta";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Inventory)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var before = _before.ToDictionary(i => i.Guid);
        var kept = _after.Where(i => i.Guid != 0).Select(i => i.Guid).ToHashSet();
        foreach (var old in _before.Where(i => !kept.Contains(i.Guid)))
        {
            using var delete = DbSql.Command(db, tx, "DELETE FROM item WHERE guid=$id AND role_id=$r");
            delete.Parameters.AddWithValue("$id", old.Guid);
            delete.Parameters.AddWithValue("$r", _role);
            if (delete.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        }
        var guids = new long[_after.Length];
        for (int i = 0; i < _after.Length; i++)
        {
            var item = _after[i];
            if (item.Guid != 0 && before.TryGetValue(item.Guid, out var previous) && previous == item)
            {
                guids[i] = item.Guid;
                continue;
            }
            using var cmd = DbSql.Command(db, tx, item.Guid == 0
                ? "INSERT INTO item(role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire) VALUES($r,$e,$s,$p,$o,$g,$a,$l,$n,$sa,$bt,$be) RETURNING guid"
                : "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE guid=$id AND role_id=$r");
            cmd.Parameters.AddWithValue("$r", _role);
            if (item.Guid != 0) cmd.Parameters.AddWithValue("$id", item.Guid);
            item.Bind(cmd);
            if (item.Guid == 0) guids[i] = (long)cmd.ExecuteScalar()!;
            else { if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException(); guids[i] = item.Guid; }
        }
        return guids;
    }
}

internal sealed class FriendGiftWritePlan : DbPlan
{
    private readonly long _role, _other;
    private readonly byte[]? _blob;
    private readonly int _intimacy;
    internal FriendGiftWritePlan(long role, long other, byte[]? blob, int intimacy)
    {
        _role = role; _other = other; _blob = blob is null ? null : (byte[])blob.Clone(); _intimacy = intimacy;
    }
    internal override int Bytes => 128 + (_blob?.Length ?? 0);
    internal override string Label => "friend/gift-enlisted";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role,
        _blob is null ? RevisionStore.FriendRelation : RevisionStore.FriendMetadata)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (_blob is not null)
        {
            using var cmd = DbSql.Command(db, tx, "INSERT INTO friend_metadata(role,data) VALUES($r,$data) ON CONFLICT(role) DO UPDATE SET data=excluded.data");
            cmd.Parameters.AddWithValue("$r", _role); cmd.Parameters.AddWithValue("$data", _blob);
            return cmd.ExecuteNonQuery();
        }
        using (var cmd = DbSql.Command(db, tx, "UPDATE friend SET intimacy=$value WHERE owner=$r AND other=$other"))
        {
            cmd.Parameters.AddWithValue("$r", _role); cmd.Parameters.AddWithValue("$other", _other);
            cmd.Parameters.AddWithValue("$value", _intimacy);
            return cmd.ExecuteNonQuery();
        }
    }
}

internal enum RoleIdentityWriteKind { AllocateInstance, Delete, Create, TouchLogin, Position }

internal sealed class RoleIdentityWritePlan : DbPlan
{
    private readonly RoleIdentityWriteKind _kind;
    private readonly long _id, _now, _scene;
    private readonly string? _account, _name;
    private readonly int _area, _sex, _career, _hair;
    private readonly float _x, _y, _z, _face;

    internal RoleIdentityWritePlan(RoleIdentityWriteKind kind, long id = 0, long now = 0, long scene = 0,
        string? account = null, string? name = null, int area = 0, int sex = 0, int career = 0, int hair = 0,
        float x = 0, float y = 0, float z = 0, float face = 0)
    {
        _kind = kind; _id = id; _now = now; _scene = scene; _account = account; _name = name;
        _area = area; _sex = sex; _career = career; _hair = hair;
        _x = x; _y = y; _z = z; _face = face;
    }
    internal override int Bytes => 192 + (_account?.Length ?? 0) * 2 + (_name?.Length ?? 0) * 2;
    internal override string Label => "role/" + _kind;
    internal override bool AllowsUnversionedRoleMutation => _kind == RoleIdentityWriteKind.Create;
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => _kind switch
    {
        RoleIdentityWriteKind.Delete or RoleIdentityWriteKind.TouchLogin => [RevisionStore.Role(_id, RevisionStore.Identity)],
        RoleIdentityWriteKind.Position => [RevisionStore.Role(_id, RevisionStore.Position)],
        _ => []
    };
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (_kind)
        {
            case RoleIdentityWriteKind.AllocateInstance:
                using (var cmd = DbSql.Command(db, tx, """
                    INSERT INTO server_state(key,value) VALUES ('instance_sequence',$first)
                    ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1 RETURNING value
                    """))
                {
                    cmd.Parameters.AddWithValue("$first", (1L << 50) + 1);
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            case RoleIdentityWriteKind.Delete:
                using (var cmd = DbSql.Command(db, tx, "UPDATE role SET deleted_time=$now,name=name || '~' || id WHERE id=$id AND account=$account AND deleted_time=0"))
                {
                    cmd.Parameters.AddWithValue("$now", _now); cmd.Parameters.AddWithValue("$id", _id);
                    cmd.Parameters.AddWithValue("$account", _account!);
                    if (cmd.ExecuteNonQuery() != 1) return false;
                    using var lifecycle = DbSql.Command(db, tx,
                        "UPDATE role_lifecycle SET epoch=epoch+1,deleted=1 WHERE role_id=$id AND deleted=0");
                    lifecycle.Parameters.AddWithValue("$id", _id);
                    if (lifecycle.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
                    return true;
                }
            case RoleIdentityWriteKind.Create:
                // A spawn scene, when given, is the role's first position, written in the same insert.
                using (var cmd = DbSql.Command(db, tx, _scene > 0 ? """
                    INSERT INTO role(account,area,name,sex,career,level,hair,create_time,last_login_time,
                        scene_entry,pos_x,pos_y,pos_z,face)
                    VALUES($account,$area,$name,$sex,$career,1,$hair,$now,$now,$scene,$x,$y,$z,$face) RETURNING *
                    """ : """
                    INSERT INTO role(account,area,name,sex,career,level,hair,create_time,last_login_time)
                    VALUES($account,$area,$name,$sex,$career,1,$hair,$now,$now) RETURNING *
                    """))
                {
                    if (_scene > 0)
                    {
                        cmd.Parameters.AddWithValue("$scene", _scene); cmd.Parameters.AddWithValue("$x", _x);
                        cmd.Parameters.AddWithValue("$y", _y); cmd.Parameters.AddWithValue("$z", _z);
                        cmd.Parameters.AddWithValue("$face", _face);
                    }
                    cmd.Parameters.AddWithValue("$account", _account!); cmd.Parameters.AddWithValue("$area", _area);
                    cmd.Parameters.AddWithValue("$name", _name!); cmd.Parameters.AddWithValue("$sex", _sex);
                    cmd.Parameters.AddWithValue("$career", _career); cmd.Parameters.AddWithValue("$hair", _hair);
                    cmd.Parameters.AddWithValue("$now", _now);
                    Role created;
                    try { created = RoleStore.Read(cmd).Single(); }
                    catch (SqliteException e) when (e.SqliteErrorCode == 19) { return null; }
                    using (var lifecycle = DbSql.Command(db, tx,
                        "INSERT INTO role_lifecycle(role_id,epoch,deleted) VALUES($id,1,0)"))
                    {
                        lifecycle.Parameters.AddWithValue("$id", created.Id);
                        lifecycle.ExecuteNonQuery();
                    }
                    foreach (string part in RoleColumns.Blobs.Concat(RevisionStore.AdditionalParts))
                    {
                        using var revision = DbSql.Command(db, tx,
                            "INSERT INTO owner_revision(kind,id,part,revision) VALUES('role',$id,$part,0)");
                        revision.Parameters.AddWithValue("$id", created.Id);
                        revision.Parameters.AddWithValue("$part", part);
                        revision.ExecuteNonQuery();
                    }
                    return created;
                }
            case RoleIdentityWriteKind.TouchLogin:
                using (var cmd = DbSql.Command(db, tx, "UPDATE role SET last_login_time=$now WHERE id=$id"))
                {
                    cmd.Parameters.AddWithValue("$now", _now); cmd.Parameters.AddWithValue("$id", _id);
                    return cmd.ExecuteNonQuery();
                }
            case RoleIdentityWriteKind.Position:
                using (var cmd = DbSql.Command(db, tx, "UPDATE role SET scene_entry=$scene,pos_x=$x,pos_y=$y,pos_z=$z,face=$face WHERE id=$id"))
                {
                    cmd.Parameters.AddWithValue("$scene", _scene); cmd.Parameters.AddWithValue("$x", _x);
                    cmd.Parameters.AddWithValue("$y", _y); cmd.Parameters.AddWithValue("$z", _z);
                    cmd.Parameters.AddWithValue("$face", _face); cmd.Parameters.AddWithValue("$id", _id);
                    return cmd.ExecuteNonQuery();
                }
            default: throw new ArgumentOutOfRangeException();
        }
    }
}

internal sealed class RoleProgressReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "role/read-progress";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT career,level,job_level,exp,job_exp,stats,bonus_points,currency,starter_kit,skills,skill_bar,paragon_level,paragon_exp FROM role WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", role);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException($"no role {role}");
        var progress = RoleStore.ReadProgress(row);
        row.Close();
        progress.SourceRevision = (RoleRevisionVector)new RoleRevisionReadPlan(role).Execute(db, tx);
        return progress;
    }
}

internal sealed class ItemReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "item/read";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx) => ItemStore.ReadItems(db, tx, role);
}

internal enum ItemWriteKind { Insert, Update, Delete, ApplyChanges, UpdateBatch, Equipment }

internal sealed class ItemWritePlan : DbPlan
{
    private readonly ItemWriteKind _kind;
    private readonly long _role, _guid;
    private readonly ItemValue[] _items;
    private readonly byte[]? _plans;
    private long[]? _resolvedRoles;
    private Dictionary<long, long>? _resolvedOwners;

    internal ItemWritePlan(ItemWriteKind kind, long role = 0, long guid = 0, IEnumerable<InvItem>? items = null, byte[]? plans = null)
    {
        _kind = kind; _role = role; _guid = guid;
        _items = items?.Select(ItemValue.Capture).ToArray() ?? [];
        _plans = plans is null ? null : (byte[])plans.Clone();
    }
    internal override int Bytes => 128 + _items.Sum(i => 160 + (i.Attrs?.Length ?? 0) * 2 +
        (i.Enchantments?.Length ?? 0) * 2 + (i.StringAttrs?.Length ?? 0) * 2) + (_plans?.Length ?? 0);
    internal override string Label => "item/" + _kind;
    internal override void ResolveRoleWrites(AccountStore accounts)
    {
        if (_resolvedRoles is not null) return;
        if (_role != 0) { _resolvedRoles = [_role]; return; }
        long[] ids = _kind == ItemWriteKind.Delete ? [_guid] : _items.Select(item => item.Guid).ToArray();
        if (ids.Length == 0) { _resolvedRoles = []; return; }
        var owners = accounts.ReadSync<Dictionary<long, long>>(new ItemOwnerReadPlan(ids));
        if (ids.Any(id => id <= 0 || !owners.ContainsKey(id))) throw new RolePlanConflictException();
        _resolvedOwners = owners;
        _resolvedRoles = owners.Values.Distinct().ToArray();
    }
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => (_resolvedRoles ??
        throw new InvalidOperationException("Item owner was not resolved before submission."))
        .SelectMany(role => _kind == ItemWriteKind.Equipment
            ? new[] { RevisionStore.Role(role, RevisionStore.Inventory), RevisionStore.Role(role, "equip_plans") }
            : new[] { RevisionStore.Role(role, RevisionStore.Inventory) }).ToArray();
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (_kind == ItemWriteKind.Delete)
        {
            using var delete = DbSql.Command(db, tx, "DELETE FROM item WHERE guid=$id AND role_id=$r");
            delete.Parameters.AddWithValue("$id", _guid);
            delete.Parameters.AddWithValue("$r", _resolvedOwners![_guid]);
            if (delete.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
            return 1;
        }
        if (_kind == ItemWriteKind.Equipment)
        {
            foreach (var item in _items)
            {
                using var move = DbSql.Command(db, tx, "UPDATE item SET grid=$g,sort=$s WHERE guid=$id AND role_id=$r");
                move.Parameters.AddWithValue("$g", item.Grid); move.Parameters.AddWithValue("$s", item.Sort);
                move.Parameters.AddWithValue("$id", item.Guid); move.Parameters.AddWithValue("$r", _role);
                if (move.ExecuteNonQuery() != 1) throw new InvalidOperationException("Equipment instance no longer owned");
            }
            using var save = DbSql.Command(db, tx, "UPDATE role SET equip_plans=$p WHERE id=$r");
            save.Parameters.AddWithValue("$p", _plans!); save.Parameters.AddWithValue("$r", _role);
            return save.ExecuteNonQuery();
        }
        long[] guids = new long[_items.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            bool insert = _kind == ItemWriteKind.Insert;
            bool remove = _kind == ItemWriteKind.ApplyChanges && item.Stack == 0;
            using var cmd = DbSql.Command(db, tx, insert
                ? "INSERT INTO item(role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire) VALUES($r,$e,$s,$p,$o,$g,$a,$l,$n,$sa,$bt,$be) RETURNING guid"
                : remove ? "DELETE FROM item WHERE guid=$id AND role_id=$r"
                : _kind == ItemWriteKind.ApplyChanges
                    ? "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE guid=$id AND role_id=$r"
                    : "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE guid=$id AND role_id=$r");
            cmd.Parameters.AddWithValue("$r", _role != 0 ? _role : _resolvedOwners![item.Guid]);
            if (!insert) cmd.Parameters.AddWithValue("$id", item.Guid);
            if (!remove) item.Bind(cmd);
            if (insert) guids[i] = (long)cmd.ExecuteScalar()!;
            else
            {
                int changed = cmd.ExecuteNonQuery();
                if (changed != 1)
                    throw new RolePlanConflictException();
                guids[i] = item.Guid;
            }
        }
        return guids;
    }
}

internal sealed class ItemOwnerReadPlan : DbReadPlan
{
    private readonly long[] _ids;
    internal ItemOwnerReadPlan(IEnumerable<long> ids) => _ids = ids.Distinct().ToArray();
    internal override int Bytes => 64 + _ids.Length * sizeof(long);
    internal override string Label => "item/read-owners";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var owners = new Dictionary<long, long>();
        foreach (long id in _ids)
        {
            using var cmd = DbSql.Command(db, tx, "SELECT role_id FROM item WHERE guid=$id");
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteScalar() is long role) owners.Add(id, role);
        }
        return owners;
    }
}
