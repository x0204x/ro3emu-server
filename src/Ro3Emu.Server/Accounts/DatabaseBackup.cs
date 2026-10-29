using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Copies ro3emu.db to &lt;data&gt;/backups at startup and every <c>Backup:IntervalHours</c> (6), keeping the newest
/// <c>Backup:Keep</c> (20) (C/D 19).
/// - **Cut:** one SQLite online backup step reads a single WAL snapshot, so a copy holds exactly the transactions
///   committed before it started, however fast the writer advances meanwhile.
/// - **Atomic:** the copy is written as <c>.partial</c>, checked with <c>PRAGMA quick_check</c> and only then renamed
///   to <c>ro3emu-*.db</c>. A crash or a failed check never leaves a file that pruning counts as a good backup.
/// - **Startup validation:** the startup backup is the realm's integrity check. A copy that fails it is kept as
///   <c>.corrupt</c> and logged as critical (the live file is never touched).
/// - **Compression:** all but the newest <c>Backup:Uncompressed</c> (2) copies become <c>ro3emu-*.db.gz</c>, written as
///   <c>.partial</c> too and renamed only once complete, with the copy's time kept so pruning orders both kinds alike.
///   <see cref="DatabaseRestore"/> takes either.
/// </summary>
public sealed partial class DatabaseBackup(IOptions<ServerOptions> options, AccountStore accounts, ILogger<DatabaseBackup> log, Diagnostics.ServerMetrics? metrics = null) : BackgroundService
{
    internal const string Prefix = "ro3emu-", Extension = ".db", CompressedExtension = ".db.gz", PartialExtension = ".partial", CorruptExtension = ".corrupt";
    private BackupOptions Settings => options.Value.Backup;
    private TimeSpan Interval => TimeSpan.FromHours(Settings.IntervalHours > 0 ? Settings.IntervalHours : 6);
    private readonly object _backupGate = new();
    private string? _lastName;
    private int _sameNameCount;
    private long _backups, _failures, _lastBytes;
    private double _lastMs;
    private DateTime? _lastAt;

    public string Directory => Path.Combine(options.Value.DataDir, "backups");

    /// <summary>Unfinished copies of a crashed run are gone before the host reports itself started, under the same
    /// gate as a backup, so neither the startup backup nor a manual one can meet a half-written file.</summary>
    public override Task StartAsync(CancellationToken ct)
    {
        lock (_backupGate) RemoveStalePartials();
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Backup();
            }
            catch (Exception e) when (e is IOException or SqliteException or InvalidDataException)
            {
                log.LogError(e, "Database backup failed");
            }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public string Backup()
    {
        // Startup and manual requests serialize the copy, the check and the pruning.
        lock (_backupGate)
        {
            try { return BackupCore(); }
            catch { Interlocked.Increment(ref _failures); throw; }
        }
    }

    private string BackupCore()
    {
        var dir = Directory;
        System.IO.Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, NextName());
        var partial = target + PartialExtension;
        var started = Stopwatch.GetTimestamp();
        using (var source = accounts.Open())
        using (var destination = new SqliteConnection($"Data Source={partial};Pooling=False"))
        {
            destination.Open();
            Diagnostics.SqliteMetrics.Attach(destination, metrics);
            Persistence.DbWriteGuard.AttachBackup(destination, accounts.AccessMetrics, accounts.DatabasePath);
            source.BackupDatabase(destination);
            if (QuickCheck(destination) is { } problem)
            {
                destination.Close();
                var corrupt = target + CorruptExtension;
                File.Move(partial, corrupt, overwrite: true);
                log.LogCritical("Database backup failed its integrity check ({Problem}); the copy is kept as {Corrupt}", problem, corrupt);
                throw new InvalidDataException($"Backup integrity check failed: {problem}");
            }
        }
        File.Move(partial, target);
        _lastMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _lastBytes = new FileInfo(target).Length;
        _lastAt = DateTime.UtcNow;
        Interlocked.Increment(ref _backups);
        log.LogInformation("Database backed up to {Target} ({Bytes} bytes, {Ms:F0} ms)", target, _lastBytes, _lastMs);
        int keep = Math.Max(1, Settings.Keep);
        foreach (var old in Completed(dir).Skip(keep)) File.Delete(old);
        CompressOlder(dir, Math.Clamp(Settings.Uncompressed, 1, keep));
        return target;
    }

    /// <summary>
    /// Compresses the plain copies past the newest <paramref name="uncompressed"/>. A failure leaves that copy plain (the
    /// next backup tries again) and never touches the copy itself; the plain file goes only after its .gz is complete.
    /// </summary>
    private void CompressOlder(string dir, int uncompressed)
    {
        foreach (var plain in Completed(dir).Skip(uncompressed).Where(f => f.EndsWith(Extension, StringComparison.Ordinal)).ToList())
        {
            var compressed = plain[..^Extension.Length] + CompressedExtension;
            var partial = compressed + PartialExtension;
            try
            {
                using (var from = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var gzip = new GZipStream(to, CompressionLevel.Fastest, leaveOpen: true)) from.CopyTo(gzip);
                    to.Flush(flushToDisk: true);
                }
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(plain));
                File.Move(partial, compressed, overwrite: true);
                File.Delete(plain);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                try { File.Delete(partial); } catch (Exception ignored) when (ignored is IOException or UnauthorizedAccessException) { }
                LogCompressFailed(log, e, plain);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not compress the backup {Backup}; it stays uncompressed")]
    private static partial void LogCompressFailed(ILogger logger, Exception exception, string backup);

    /// <summary>Second-based names, made unique within a second, so concurrent requests never share a file.</summary>
    private string NextName()
    {
        var name = $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmss}";
        if (name == _lastName) return $"{name}-{++_sameNameCount}{Extension}";
        _lastName = name;
        _sameNameCount = 0;
        return name + Extension;
    }

    /// <summary>Completed backups, plain or compressed, newest first.</summary>
    internal static IEnumerable<string> Completed(string dir) =>
        System.IO.Directory.GetFiles(dir, Prefix + "*").Where(IsBackup)
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(f => f, StringComparer.Ordinal);

    private static bool IsBackup(string file) =>
        file.EndsWith(Extension, StringComparison.Ordinal) || file.EndsWith(CompressedExtension, StringComparison.Ordinal);

    internal static bool IsCompressed(string file) => file.EndsWith(CompressedExtension, StringComparison.OrdinalIgnoreCase);

    private void RemoveStalePartials()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var partial in System.IO.Directory.GetFiles(Directory, Prefix + "*" + PartialExtension))
        {
            log.LogWarning("Removing an unfinished backup {Partial}", partial);
            File.Delete(partial);
        }
    }

    /// <summary>Null when SQLite's quick_check reports "ok", else its first findings.</summary>
    internal static string? QuickCheck(SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "PRAGMA quick_check(5)";
        using var reader = check.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows is ["ok"] ? null : string.Join("; ", rows);
    }

    public object Snapshot() => new
    {
        backups = Interlocked.Read(ref _backups), failures = Interlocked.Read(ref _failures),
        lastAt = _lastAt, lastMs = _lastMs, lastBytes = _lastBytes,
    };
}
