using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// D1: every realm connection has explicit access. The native authorizer denies writes from read handles
/// and from a writer borrowed on another thread. It is active in Release as well as Debug.
/// Account bootstrap is private and ends before the store can be used; backups target a different file.
/// </summary>
internal static class DbWriteGuard
{
    private enum Access { Read, Writer, Bootstrap, Backup }
    private sealed class State(DbAccessMetrics metrics, Access access, string source)
    {
        internal readonly DbAccessMetrics Metrics = metrics;
        internal readonly Access Mode = access;
        internal readonly int Thread = Environment.CurrentManagedThreadId;
        internal string Source = source;
        internal bool RoleWriteAuthorized;
    }
    private static readonly ConditionalWeakTable<SqliteConnection, State> States = [];
    private static readonly delegate_authorizer Authorizer = Authorize;
    private static readonly HashSet<string> ReadArgumentPragmas = new(StringComparer.OrdinalIgnoreCase)
    { "table_info", "table_xinfo", "index_info", "index_xinfo", "index_list", "foreign_key_list",
        "foreign_key_check", "integrity_check", "quick_check" };

    internal static void AttachRead(SqliteConnection db, DbAccessMetrics metrics, string source) =>
        Attach(db, new(metrics, Access.Read, source));
    internal static void AttachWriter(SqliteConnection db, DbAccessMetrics metrics) =>
        Attach(db, new(metrics, Access.Writer, "writer/startup"));
    internal static void AttachBootstrap(SqliteConnection db, DbAccessMetrics metrics) =>
        Attach(db, new(metrics, Access.Bootstrap, "account/bootstrap"));
    internal static void AttachBackup(SqliteConnection db, DbAccessMetrics metrics, string realmPath)
    {
        if (string.Equals(Path.GetFullPath(db.DataSource), Path.GetFullPath(realmPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A backup destination must not be the realm database.");
        Attach(db, new(metrics, Access.Backup, "backup/destination"));
    }

    private static void Attach(SqliteConnection db, State state)
    {
        States.Add(db, state);
        int result = raw.sqlite3_set_authorizer(db.Handle!, Authorizer, state);
        if (result != raw.SQLITE_OK)
        {
            States.Remove(db);
            throw new InvalidOperationException($"SQLite write-guard registration failed: {result}");
        }
        if (state.Mode == Access.Read && raw.sqlite3_db_readonly(db.Handle!, "main") != 1)
            throw new InvalidOperationException("Read handles must open the database in read-only mode.");
    }

    internal static void Plan(SqliteConnection db, DbPlan plan)
    {
        Plan(db, plan.Label);
        if (States.TryGetValue(db, out var state))
            state.RoleWriteAuthorized = plan.RoleWrites.Count != 0 || plan.AllowsUnversionedRoleMutation;
    }

    internal static void Plan(SqliteConnection db, string label)
    {
        AssertWriter(db);
        if (States.TryGetValue(db, out var state))
        {
            state.Source = label.Length <= 96 ? label : label[..96];
            state.RoleWriteAuthorized = false;
        }
    }

    internal static void ReadPlan(SqliteConnection db, string label)
    {
        if (States.TryGetValue(db, out var state)) state.Source = label.Length <= 96 ? label : label[..96];
    }

    /// <summary>Managed debug check, deliberately outside the native callback ABI.</summary>
    [Conditional("DEBUG")]
    internal static void AssertWriter(SqliteConnection db)
    {
        if (!States.TryGetValue(db, out var state) || state.Mode != Access.Writer || state.Thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Only the owning DbWriter thread may execute a write plan.");
    }

    private static int Authorize(object user, int action, utf8z first, utf8z second, utf8z database, utf8z trigger)
    {
        State? state = null;
        try
        {
            state = (State)user;
            bool write = action is >= raw.SQLITE_CREATE_INDEX and <= raw.SQLITE_DELETE
                or >= raw.SQLITE_DROP_INDEX and <= raw.SQLITE_INSERT
                or raw.SQLITE_UPDATE or raw.SQLITE_ATTACH or raw.SQLITE_DETACH or raw.SQLITE_ALTER_TABLE
                or raw.SQLITE_REINDEX or raw.SQLITE_ANALYZE or raw.SQLITE_CREATE_VTABLE or raw.SQLITE_DROP_VTABLE;
            if (action == raw.SQLITE_PRAGMA)
            {
                string pragma = first.utf8_to_string() ?? "";
                write = pragma.Equals("wal_checkpoint", StringComparison.OrdinalIgnoreCase)
                    || pragma.Equals("optimize", StringComparison.OrdinalIgnoreCase)
                    || pragma.Equals("incremental_vacuum", StringComparison.OrdinalIgnoreCase)
                    || second.utf8_to_string() is not null && !ReadArgumentPragmas.Contains(pragma);
            }
            if (!write) return raw.SQLITE_OK;
            int thread = Environment.CurrentManagedThreadId;
            if (state.Mode == Access.Writer && action is raw.SQLITE_INSERT or raw.SQLITE_UPDATE or raw.SQLITE_DELETE
                && (first.utf8_to_string() is "role" or "item") && !state.RoleWriteAuthorized)
            {
                state.Metrics.WriteDenied(state.Source, action, first.utf8_to_string() ?? "", thread);
                return raw.SQLITE_DENY;
            }
            if (state.Mode != Access.Read && state.Thread == thread)
            {
                state.Metrics.WriteAuthorized(state.Mode == Access.Writer, state.Mode == Access.Bootstrap, state.Source, thread);
                return raw.SQLITE_OK;
            }
            state.Metrics.WriteDenied(state.Source, action, first.utf8_to_string() ?? "", thread);
            return raw.SQLITE_DENY;
        }
        catch
        {
            try { state?.Metrics.WriteGuardError(); } catch { }
            return raw.SQLITE_DENY; // Fail closed; nothing may throw across SQLite's native callback ABI.
        }
    }
}
