using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Where one activity keeps its durable per-role payout obligations (see docs/systems/durable-settlement.md). Every such
/// table has a <c>source_key TEXT PRIMARY KEY</c> (the activity's idempotent source, also the reservation and
/// owner_revision id), a role column, the activity's frozen columns and <c>delivered INTEGER 0/1</c>. The table itself
/// and its schema plan stay the activity's own: existing databases keep their rows and the economy reader its
/// classification; this record only tells the shared plans how to read and mark them.
/// </summary>
/// <param name="Table">The SQL table, e.g. <c>casual_battle_obligation</c>.</param>
/// <param name="RevisionKind">The owner_revision kind and the reservation entity kind of one row (its id is the
/// source key). Usually the table name; MVP keeps its historical <c>mvp_obligation</c>.</param>
/// <param name="RoleColumn">The column holding the beneficiary role id.</param>
/// <param name="Columns">Every column but <c>delivered</c>, in the order of the row's
/// <see cref="IRoleObligationRow{TSelf}.Values"/> and <see cref="IRoleObligationRow{TSelf}.Read"/>; the first is
/// <c>source_key</c>.</param>
/// <param name="PendingOrder">The ORDER BY of a role's pending rows (the order they are paid in).</param>
/// <param name="LabelPrefix">Plan labels: <c>{prefix}/create-obligations</c> and <c>{prefix}/deliver-obligation</c>.</param>
/// <param name="PendingLabel">The label of the pending-rows read.</param>
/// <param name="ScopeColumn">The column grouping one settlement's rows (a match id), or null when the activity has
/// none; <see cref="RoleObligationScopeReadPlan{TRow}"/> reads by it to adopt a save whose acknowledgement was lost.</param>
internal sealed record RoleObligationTable(string Table, string RevisionKind, string RoleColumn, string[] Columns,
    string PendingOrder, string LabelPrefix, string PendingLabel, string? ScopeColumn = null)
{
    internal string Select => string.Join(',', Columns);
}

/// <summary>
/// One frozen obligation row of an activity. The implementing type names its table statically, so the shared plans
/// carry nothing but detached values (EventPlanFingerprint and the economy envelopes capture plan fields).
/// </summary>
internal interface IRoleObligationRow<TSelf> where TSelf : class, IRoleObligationRow<TSelf>
{
    static abstract RoleObligationTable Table { get; }
    /// <summary>Builds a row from a reader positioned on <see cref="RoleObligationTable.Columns"/>.</summary>
    static abstract TSelf Read(SqliteDataReader reader);
    string SourceKey { get; }
    long RoleId { get; }
    /// <summary>The column values, in <see cref="RoleObligationTable.Columns"/> order.</summary>
    object[] Values();
    /// <summary>A copy that shares no mutable buffer with the caller (plans are detached commands).</summary>
    TSelf Detach();
    /// <summary>True when <paramref name="stored"/> is exactly this frozen result (an adopted lost acknowledgement).</summary>
    bool SameFrozen(TSelf stored);
}

/// <summary>Inserts one settlement's obligations (all or none). A duplicate source refuses the transaction.</summary>
internal sealed class RoleObligationCreatePlan<TRow> : DbPlan where TRow : class, IRoleObligationRow<TRow>
{
    private readonly TRow[] rows;
    internal RoleObligationCreatePlan(IEnumerable<TRow> rows)
    {
        this.rows = rows.Select(row => row.Detach()).ToArray();
        if (this.rows.Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).Count() != this.rows.Length)
            throw new ArgumentException($"Duplicate {TRow.Table.Table} source.");
    }
    internal IReadOnlyList<TRow> Rows => rows;
    internal override int Bytes => 256 + rows.Sum(row => row.Values().Sum(value => value switch
    {
        string text => text.Length * 2,
        byte[] bytes => bytes.Length,
        _ => 8,
    }));
    internal override string Label => TRow.Table.LabelPrefix + "/create-obligations";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        rows.Select(row => RevisionStore.Entity(TRow.Table.RevisionKind, row.SourceKey)).ToArray();
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var table = TRow.Table;
        string sql = $"INSERT INTO {table.Table}({table.Select},delivered) VALUES(" +
            string.Join(',', table.Columns.Select((_, i) => "$p" + i)) + ",0)";
        foreach (var row in rows)
        {
            using var cmd = DbSql.Command(db, tx, sql);
            var values = row.Values();
            if (values.Length != table.Columns.Length)
                throw new InvalidOperationException($"{table.Table} row has {values.Length} values for {table.Columns.Length} columns.");
            for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
            if (cmd.ExecuteNonQuery() != 1) throw new OwnerRevisionConflictException();
        }
        return rows.Length;
    }
}

/// <summary>
/// The delivered mark of one obligation, committed in the same transaction as its payment (and its receipt). It
/// refuses unless the row is still undelivered, so a second payment can never commit.
/// </summary>
internal sealed class RoleObligationDeliverPlan<TRow>(string source, long role) : DbPlan
    where TRow : class, IRoleObligationRow<TRow>
{
    internal override int Bytes => 128 + source.Length * 2;
    internal override string Label => TRow.Table.LabelPrefix + "/deliver-obligation";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity(TRow.Table.RevisionKind, source)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var table = TRow.Table;
        using var cmd = DbSql.Command(db, tx,
            $"UPDATE {table.Table} SET delivered=1 WHERE source_key=$source AND {table.RoleColumn}=$role AND delivered=0");
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$role", role);
        if (cmd.ExecuteNonQuery() != 1) throw new OwnerRevisionConflictException();
        return 1;
    }
}

/// <summary>The undelivered obligations of one role, in payment order.</summary>
internal sealed class RoleObligationPendingReadPlan<TRow>(long role) : DbReadPlan
    where TRow : class, IRoleObligationRow<TRow>
{
    internal override int Bytes => 256;
    internal override string Label => TRow.Table.PendingLabel;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var table = TRow.Table;
        using var cmd = DbSql.Command(db, tx,
            $"SELECT {table.Select} FROM {table.Table} WHERE {table.RoleColumn}=$role AND delivered=0 ORDER BY {table.PendingOrder}");
        cmd.Parameters.AddWithValue("$role", role);
        using var reader = cmd.ExecuteReader();
        var rows = new List<TRow>();
        while (reader.Read()) rows.Add(TRow.Read(reader));
        return rows;
    }
}

/// <summary>Every obligation (delivered or not) of one settlement, by the table's scope column.</summary>
internal sealed class RoleObligationScopeReadPlan<TRow>(long scope) : DbReadPlan
    where TRow : class, IRoleObligationRow<TRow>
{
    internal override int Bytes => 256;
    internal override string Label => TRow.Table.LabelPrefix + "/read-scope";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var table = TRow.Table;
        string column = table.ScopeColumn
            ?? throw new InvalidOperationException(table.Table + " has no settlement scope column.");
        using var cmd = DbSql.Command(db, tx,
            $"SELECT {table.Select} FROM {table.Table} WHERE {column}=$scope ORDER BY source_key");
        cmd.Parameters.AddWithValue("$scope", scope);
        using var reader = cmd.ExecuteReader();
        var rows = new List<TRow>();
        while (reader.Read()) rows.Add(TRow.Read(reader));
        return rows;
    }
}
