using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>A detached database command. Implementations contain values only; the writer owns SQL and its transaction.</summary>
public abstract class DbPlan
{
    internal virtual string? OperationId => null;
    /// <summary>The plan owns replay and outcome resolution in its typed receipt table.</summary>
    internal virtual bool OwnsOperationReceipt => false;
    internal abstract object? Execute(SqliteConnection connection, SqliteTransaction transaction);
    internal abstract int Bytes { get; }
    internal abstract string Label { get; }
    // A role-writing plan must declare every storage part it changes. The writer checks
    // the captured vector and advances each distinct part in its business transaction.
    internal virtual IReadOnlyList<RoleRevisionKey> RoleWrites => [];
    internal virtual IReadOnlyList<long> RolePins => [.. RoleWrites.Select(key => key.RoleId).Distinct()];
    internal virtual IReadOnlyList<OwnerRevisionKey> EntityWrites => [];
    // Writer-evaluated deltas read the latest aggregate inside BEGIN, then CAS-bump its
    // revision. They contain no detached replacement state to relabel at admission.
    internal virtual IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites => [];
    internal virtual IReadOnlyList<RoleRevisionKey> MutatedRoleWrites(object? result) =>
        result is false or 0 or 0L ? [] : RoleWrites;
    internal virtual IReadOnlyList<OwnerRevisionKey> MutatedEntityWrites(object? result) =>
        result is false or 0 or 0L ? [] : EntityWrites;
    internal virtual IReadOnlyList<OwnerRevisionKey> MutatedEntityDeltaWrites(object? result) =>
        result is false or 0 or 0L ? [] : EntityDeltaWrites;
    internal virtual void ResolveRoleWrites(AccountStore accounts) { }
    // Receipt-aware plans resolve a committed replay before C/D revision CAS rejects a stale capture.
    internal virtual bool TryReplay(SqliteConnection connection, SqliteTransaction transaction, out object? result)
    { result = null; return false; }
    internal virtual void AfterCommit() { }
    internal virtual bool IsReplay => false;
    internal virtual bool AllowsUnversionedRoleMutation => false;
    internal IReadOnlyDictionary<long, RoleRevisionVector>? ExpectedRevisions { get; private set; }
    internal IReadOnlyDictionary<OwnerRevisionKey, OwnerRevisionValue>? ExpectedEntities { get; private set; }

    internal void Expect(IEnumerable<RoleRevisionVector> vectors)
    {
        if (ExpectedRevisions is not null) throw new InvalidOperationException("Revision expectation already captured.");
        ExpectedRevisions = vectors.Select(v => new RoleRevisionVector(v.RoleId, v.Epoch, v.Deleted,
            v.Parts.ToFrozenDictionary(StringComparer.Ordinal))).ToDictionary(v => v.RoleId);
    }
    /// <summary>Drop a capture refused by the writer's revision check before recapturing fresh vectors.</summary>
    internal void ResetExpectations() { ExpectedRevisions = null; ExpectedEntities = null; }
    internal void ExpectEntities(IEnumerable<OwnerRevisionValue> values)
    {
        if (ExpectedEntities is not null) throw new InvalidOperationException("Entity expectation already captured.");
        ExpectedEntities = values.ToDictionary(value => value.Key);
    }
}

/// <summary>A detached snapshot query. The reader owns its connection and returns copied values.</summary>
public abstract class DbReadPlan
{
    internal abstract object? Execute(SqliteConnection connection, SqliteTransaction transaction);
    internal abstract int Bytes { get; }
    internal abstract string Label { get; }
}

internal static class DbSql
{
    internal static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }
}
