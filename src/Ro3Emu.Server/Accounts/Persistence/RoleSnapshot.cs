using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// One role as stored at a single SQLite read snapshot (C/D 14): identity, progress with its revision vector, every
/// role blob column and the item rows, read in one read transaction on a reader worker. Values are detached copies;
/// the same decoders as the per-store reads are used, so a served value is byte-for-byte what that read returns.
/// </summary>
public sealed class RoleSnapshot
{
    internal long RoleId { get; init; }
    internal Role? Identity { get; init; }
    internal RoleProgress? Progress { get; init; }
    internal Dictionary<string, byte[]?> Blobs { get; init; } = new(StringComparer.Ordinal);
    internal List<InvItem>? Items { get; init; }
    internal long Bytes { get; init; }
    internal double ReadMs { get; init; }
}

internal sealed class RoleSnapshotReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "role/read-snapshot";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long started = Stopwatch.GetTimestamp();
        var identity = ((List<Role>)new RoleProjectionRead(RoleProjectionKind.ById, id: role).Execute(db, tx)!).FirstOrDefault();
        if (identity == null) return new RoleSnapshot { RoleId = role };
        var progress = (RoleProgress)new RoleProgressReadPlan(role).Execute(db, tx)!;
        var columns = RoleColumns.Blobs.ToArray();
        var blobs = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        long bytes = 0;
        using (var cmd = DbSql.Command(db, tx, $"SELECT {string.Join(',', columns)} FROM role WHERE id=$id"))
        {
            cmd.Parameters.AddWithValue("$id", role);
            using var row = cmd.ExecuteReader();
            if (row.Read())
                for (int i = 0; i < columns.Length; i++)
                {
                    byte[]? value = row.IsDBNull(i) ? null : row.GetValue(i) as byte[];
                    blobs[columns[i]] = value == null ? null : (byte[])value.Clone();
                    bytes += value?.Length ?? 0;
                }
        }
        var items = ItemStore.ReadItems(db, tx, role);
        return new RoleSnapshot
        {
            RoleId = role, Identity = identity, Progress = progress, Blobs = blobs, Items = items,
            Bytes = bytes + items.Count * 160L, ReadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }
}

/// <summary>
/// Installed role snapshots, served by RoleStore/ItemStore reads during a role's login instead of synchronous SQL.
/// Every committed role write invalidates the parts it declares (writer thread, right after COMMIT). A snapshot read
/// that raced a write is installed without the parts written after its read began (<see cref="Sequence"/>), so a
/// served value is never older than a committed write. Progress-part writes also invalidate the progress columns,
/// and any write of the role drops its identity row. Mutable session objects (progress, items, identity) are handed
/// out once; blobs are copied on every read.
/// </summary>
public sealed class RoleSnapshots
{
    // Columns read by RoleProgressReadPlan: a write of any of them, or of the "progress" part, stales progress.
    private static readonly HashSet<string> ProgressColumns = new(StringComparer.Ordinal)
        { "career", "level", "job_level", "exp", "job_exp", "stats", "bonus_points", "currency", "starter_kit", "skills", "skill_bar",
          "paragon_level", "paragon_exp" };

    private sealed class Entry(RoleSnapshot snapshot)
    {
        public readonly RoleSnapshot Snapshot = snapshot;
        public readonly Dictionary<string, byte[]?> Blobs = new(snapshot.Blobs, StringComparer.Ordinal);
        public bool Progress = snapshot.Progress != null, Items = snapshot.Items != null, Identity = snapshot.Identity != null;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _installed = new();
    private readonly Dictionary<(long Role, string Part), long> _writtenAt = new();
    private long _sequence;
    private long _prunedThrough; // _writtenAt no longer knows commits at or before this sequence
    private long _served, _invalidated, _installs, _staleInstalls;
    // C/D 16 part 3: the last value this process persisted for a role part (its SHA-256), valid until any other
    // commit touches the part. A runtime save of identical bytes is then skipped instead of written on the loop.
    private sealed class PendingSave { public int Commits; }
    private readonly Dictionary<(long Role, string Part), byte[]> _persisted = new();
    private readonly Dictionary<(long Role, string Part), PendingSave> _pending = new();
    private long _persistedSkips;
    private const int MaxPersisted = 65536, MaxInstalled = 4096;

    /// <summary>Test switch: every served value is also read from SQL and must be identical.</summary>
    internal static bool VerifyAgainstSql { get; } = Environment.GetEnvironmentVariable("RO3EMU_SNAPSHOT_VERIFY") == "1";

    /// <summary>Take before starting a snapshot read; pass to <see cref="Install"/>.</summary>
    internal long Sequence { get { lock (_gate) return _sequence; } }
    internal long Served => Interlocked.Read(ref _served);
    internal long Invalidated => Interlocked.Read(ref _invalidated);
    internal long StaleInstalls => Interlocked.Read(ref _staleInstalls);

    internal void Install(RoleSnapshot snapshot, long readStartedAt)
    {
        if (snapshot.Identity == null) return;
        var entry = new Entry(snapshot);
        lock (_gate)
        {
            // A read older than the pruned history cannot be checked against the commits since it began: not served.
            if (readStartedAt < _prunedThrough) { Interlocked.Increment(ref _staleInstalls); return; }
            // Parts committed after the read began may be missing from it: never serve them.
            foreach (var ((role, part), at) in _writtenAt)
                if (role == snapshot.RoleId && at > readStartedAt) Drop(entry, part);
            // Entries live for the role's session (C/D 16 part 4); a missed release only costs memory, never
            // correctness (every commit still invalidates), so a bound simply starts over.
            if (_installed.Count >= MaxInstalled && !_installed.ContainsKey(snapshot.RoleId)) _installed.Clear();
            _installed[snapshot.RoleId] = entry;
        }
        Interlocked.Increment(ref _installs);
    }

    internal long PersistedSkips => Interlocked.Read(ref _persistedSkips);

    /// <summary>True when <paramref name="value"/> is byte-identical to what this process last persisted for the
    /// part and no other commit touched the part since; the caller may skip the write.</summary>
    internal bool PersistedEqual(long role, string part, byte[] value)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(value);
        lock (_gate)
        {
            if (!_persisted.TryGetValue((role, part), out var known) || !known.AsSpan().SequenceEqual(hash)) return false;
        }
        Interlocked.Increment(ref _persistedSkips);
        return true;
    }

    /// <summary>Call before submitting a save of the part; pass the token to <see cref="EndSave"/> after it returns.</summary>
    internal object BeginSave(long role, string part)
    {
        var token = new PendingSave();
        lock (_gate)
        {
            _persisted.Remove((role, part));
            _pending[(role, part)] = token;
        }
        return token;
    }

    /// <summary>Remembers the saved value only if the save was the single commit of the part since BeginSave.</summary>
    internal void EndSave(long role, string part, object token, byte[] value, bool committed)
    {
        byte[]? hash = committed ? System.Security.Cryptography.SHA256.HashData(value) : null;
        lock (_gate)
        {
            if (!_pending.TryGetValue((role, part), out var pending) || !ReferenceEquals(pending, token)) return;
            _pending.Remove((role, part));
            if (hash == null || pending.Commits != 1) return;
            if (_persisted.Count >= MaxPersisted) _persisted.Clear(); // bounded: losing it only costs skips
            _persisted[(role, part)] = hash;
            // C/D 16 part 4 write-through: the session's snapshot serves the bytes just committed (the only commit of
            // the part since BeginSave, so they are exactly what SQL holds).
            if (_installed.TryGetValue(role, out var entry)) entry.Blobs[part] = (byte[])value.Clone();
        }
    }

    internal bool IsInstalled(long role) { lock (_gate) return _installed.ContainsKey(role); }

    internal void Remove(long role)
    {
        lock (_gate) _installed.Remove(role);
    }

    internal void Committed(IEnumerable<RoleRevisionKey> writes)
    {
        lock (_gate)
        {
            foreach (var key in writes)
            {
                _writtenAt[(key.RoleId, key.Part)] = ++_sequence;
                if (_pending.TryGetValue((key.RoleId, key.Part), out var pending)) pending.Commits++;
                if (key.Part == RevisionStore.Identity)
                {
                    // Lifecycle/identity writes (login touch, delete, create): forget everything known for the role.
                    foreach (var stale in _persisted.Keys.Where(k => k.Role == key.RoleId).ToArray()) _persisted.Remove(stale);
                }
                else _persisted.Remove((key.RoleId, key.Part));
                if (_installed.TryGetValue(key.RoleId, out var entry))
                {
                    Drop(entry, key.Part);
                    Interlocked.Increment(ref _invalidated);
                }
            }
            // Remember at least the last 4096 writes; a read that began before the forgotten ones is refused by Install.
            if (_writtenAt.Count > 8192)
            {
                long keepAfter = _sequence - 4096;
                _prunedThrough = keepAfter;
                foreach (var key in _writtenAt.Where(e => e.Value <= keepAfter).Select(e => e.Key).ToArray()) _writtenAt.Remove(key);
            }
        }
    }

    private static void Drop(Entry entry, string part)
    {
        entry.Identity = false;
        if (part == RevisionStore.Inventory) { entry.Items = false; return; }
        if (part == RevisionStore.Progress || ProgressColumns.Contains(part))
        {
            entry.Progress = false;
            foreach (var column in ProgressColumns) entry.Blobs.Remove(column);
        }
        entry.Blobs.Remove(part);
    }

    internal bool TryBlob(long role, string column, out byte[]? value)
    {
        lock (_gate)
        {
            value = null;
            if (!_installed.TryGetValue(role, out var entry) || !entry.Blobs.TryGetValue(column, out var stored)) return false;
            value = stored == null ? null : (byte[])stored.Clone();
        }
        Interlocked.Increment(ref _served);
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> equals the stored bytes of a still-valid snapshot column: the database
    /// already holds exactly this value (any later write would have invalidated it), so writing it is a no-op.
    /// </summary>
    internal bool Unchanged(long role, string column, byte[] value)
    {
        lock (_gate)
        {
            if (!_installed.TryGetValue(role, out var entry) || !entry.Blobs.TryGetValue(column, out var stored) || stored == null) return false;
            if (!stored.AsSpan().SequenceEqual(value)) return false;
        }
        Interlocked.Increment(ref _skippedWrites);
        return true;
    }
    private long _skippedWrites;
    internal long SkippedWrites => Interlocked.Read(ref _skippedWrites);

    internal bool TryProgress(long role, out RoleProgress? progress)
    {
        lock (_gate)
        {
            progress = null;
            if (!_installed.TryGetValue(role, out var entry) || !entry.Progress) return false;
            progress = entry.Snapshot.Progress;
            entry.Progress = false;
        }
        Interlocked.Increment(ref _served);
        return true;
    }

    internal bool TryItems(long role, out List<InvItem>? items)
    {
        lock (_gate)
        {
            items = null;
            if (!_installed.TryGetValue(role, out var entry) || !entry.Items) return false;
            items = entry.Snapshot.Items;
            entry.Items = false;
        }
        Interlocked.Increment(ref _served);
        return true;
    }

    internal bool TryIdentity(long role, out Role? identity)
    {
        lock (_gate)
        {
            identity = null;
            if (!_installed.TryGetValue(role, out var entry) || !entry.Identity) return false;
            identity = entry.Snapshot.Identity;
            entry.Identity = false;
        }
        Interlocked.Increment(ref _served);
        return true;
    }

    public object Snapshot()
    {
        lock (_gate) return new { installs = Interlocked.Read(ref _installs), served = Served, invalidated = Invalidated, staleInstalls = StaleInstalls, installed = _installed.Count };
    }
}

/// <summary>Test-only shadow check of a served snapshot value against the same SQL read.</summary>
internal static class SnapshotVerify
{
    internal static T Same<T>(T served, Func<T> sql, string what)
    {
        if (!RoleSnapshots.VerifyAgainstSql) return served;
        var fresh = sql();
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        string a = served is byte[] x ? Convert.ToBase64String(x) : System.Text.Json.JsonSerializer.Serialize(served, options);
        string b = fresh is byte[] y ? Convert.ToBase64String(y) : System.Text.Json.JsonSerializer.Serialize(fresh, options);
        if (a != b) throw new InvalidOperationException($"Stale role snapshot served for {what}.");
        return served;
    }
}
