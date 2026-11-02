using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// One attachment at connection creation covers every prepared SQLite statement, including transaction SQL.
/// SQLITE_TRACE_STMT/PROFILE brackets execution using Stopwatch instead of SQLite's millisecond profiler clock.
/// SQL is unexpanded (no values/secrets); operation labels are table/verb. The commit hook counts commit attempts,
/// and their duration is the enclosing statement (an upper envelope for implicit commits).
/// https://sqlite.org/c3ref/c_trace.html
/// </summary>
public static unsafe class SqliteMetrics
{
    private const uint Statement = 1, Profile = 2, Close = 8;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TraceCallback(uint kind, nint context, nint statement, nint detail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommitCallback(nint context);
    private static readonly TraceCallback Trace = OnTrace;
    private static readonly CommitCallback Commit = OnCommit;
    private static readonly ConcurrentDictionary<nint, TraceState> Connections = new();
    private static readonly ConcurrentDictionary<ulong, string> Labels = new();
    /// <summary>Diagnostic aid: RO3EMU_SQL_CALLSITES="role/SELECT,guild/SELECT" samples every 20th such statement's
    /// game-code call site into the metrics (database.callSites). Off unless set; never on a measured run.</summary>
    private static readonly HashSet<string>? TracedOperations = Environment.GetEnvironmentVariable("RO3EMU_SQL_CALLSITES")
        is { Length: > 0 } list ? [.. list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)] : null;
    private static long _sampled;

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_trace_v2(nint db, uint mask, TraceCallback? callback, nint context);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_commit_hook(nint db, CommitCallback? callback, nint context);

    public static void Attach(SqliteConnection connection, ServerMetrics? metrics)
    {
        if (metrics == null || connection.Handle is not { } handle) return;
        // The raw pointer goes to native code: hold a reference on the SafeHandle for the whole registration, or a
        // concurrent release (pool clearing, a finalizer) frees the sqlite3 object mid-call - an access violation
        // that killed the test host (0xC0000005 in sqlite3_trace_v2).
        bool added = false;
        try { handle.DangerousAddRef(ref added); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (handle.IsInvalid || handle.IsClosed) return;
            nint native = handle.DangerousGetHandle();
            // Native pooled connections outlive their ADO wrappers. CLOSE retires the registration, and a new borrower
            // replaces the owner. Never dereference the native handle from ADO StateChange after it may have closed.
            Connections[native] = new TraceState(metrics);
            int result = sqlite3_trace_v2(native, Statement | Profile | Close, Trace, native);
            if (result != 0) { Connections.TryRemove(native, out _); throw new InvalidOperationException($"SQLite trace registration failed: {result}"); }
            sqlite3_commit_hook(native, Commit, native);
        }
        finally
        {
            if (added) handle.DangerousRelease();
            GC.KeepAlive(connection);
        }
    }

    private sealed class TraceState(ServerMetrics metrics)
    {
        public readonly ServerMetrics Metrics = metrics;
        public readonly Dictionary<nint, Measurement> Active = [];
        public nint Current;
    }
    private record struct Measurement(string Operation, long Start, bool OnLoop, int Thread, bool Commit);

    private static int OnTrace(uint kind, nint context, nint statement, nint detail)
    {
        if (kind == Close) { Connections.TryRemove(context, out _); return 0; }
        if (!Connections.TryGetValue(context, out var state)) return 0;
        try
        {
            if (kind == Statement)
            {
                // Trigger callbacks carry SQL comments and share their parent's statement. Do not replace its start.
                if (state.Active.ContainsKey(statement)) return 0;
                var sql = Utf8(detail);
                string operation = Label(sql);
                state.Active[statement] = new(operation, Stopwatch.GetTimestamp(),
                    ReferenceEquals(ServerMetrics.LoopOwner, state.Metrics), Environment.CurrentManagedThreadId,
                    operation is "transaction/COMMIT" or "transaction/END");
                state.Current = statement;
                if (TracedOperations?.Contains(operation) == true && Interlocked.Increment(ref _sampled) % 20 == 0)
                    state.Metrics.RecordDbCallSite(operation, CallSite());
            }
            else if (kind == Profile && state.Active.Remove(statement, out var measurement))
            {
                state.Metrics.RecordDatabase(measurement.Operation, measurement.Start, measurement.OnLoop, measurement.Thread, measurement.Commit);
                if (state.Current == statement) state.Current = 0;
            }
        }
        catch { Interlocked.Increment(ref state.Metrics.Current.DatabaseProfileErrors); }
        return 0; // A diagnostic callback must never change SQLite's result or throw across the native ABI.
    }
    private static string CallSite()
    {
        var frames = new StackTrace(2, false).GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType?.Namespace?.StartsWith("Ro3Emu.Server.Game") == true || m?.DeclaringType?.Namespace?.StartsWith("Ro3Emu.Server.Maps") == true)
            .Select(m => (m!.DeclaringType!.DeclaringType ?? m.DeclaringType)!.Name + "." + m.Name.Replace("MoveNext", m.DeclaringType!.Name))
            .Take(3);
        return string.Join(" < ", frames);
    }

    private static int OnCommit(nint context)
    {
        if (!Connections.TryGetValue(context, out var state)) return 0;
        try { if (state.Active.TryGetValue(state.Current, out var value)) state.Active[state.Current] = value with { Commit = true }; }
        catch { Interlocked.Increment(ref state.Metrics.Current.DatabaseProfileErrors); }
        return 0; // never throw across the native ABI (the process dies with "Fatal error")
    }
    private static ReadOnlySpan<byte> Utf8(nint pointer)
    {
        byte* p = (byte*)pointer;
        int length = 0;
        while (length < 64 * 1024 && p[length] != 0) length++;
        return new(p, length);
    }
    private static string Label(ReadOnlySpan<byte> sql)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in sql) hash = (hash ^ b) * 1099511628211UL;
        if (Labels.TryGetValue(hash, out var label)) return label;
        label = Classify(Encoding.UTF8.GetString(sql));
        if (Labels.Count < 4096) Labels.TryAdd(hash, label);
        return label;
    }
    internal static string Classify(string sql)
    {
        // Label construction occurs once per distinct SQL text on a borrowed connection, never per network message.
        var words = sql.Split([' ', '\r', '\n', '\t', '(', ')', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "unknown/UNKNOWN";
        string verb = words[0].ToUpperInvariant();
        int table = verb switch
        {
            "SELECT" or "DELETE" => Array.FindIndex(words, x => x.Equals("FROM", StringComparison.OrdinalIgnoreCase)) + 1,
            "INSERT" or "REPLACE" => Array.FindIndex(words, x => x.Equals("INTO", StringComparison.OrdinalIgnoreCase)) + 1,
            "UPDATE" => 1,
            "CREATE" or "ALTER" or "DROP" => Array.FindIndex(words, x => x.Equals("TABLE", StringComparison.OrdinalIgnoreCase)) + 1,
            "PRAGMA" => 1,
            _ => 0,
        };
        while (table > 0 && table < words.Length && words[table] is "IF" or "NOT" or "EXISTS") table++;
        string store = table > 0 && table < words.Length ? words[table].Trim('"', '[', ']', '`') : "transaction";
        if (store.Contains('=')) store = store[..store.IndexOf('=')];
        return store + "/" + verb;
    }
}
