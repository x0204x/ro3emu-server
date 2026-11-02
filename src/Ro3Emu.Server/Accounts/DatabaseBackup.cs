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
/// - **Big databases:** from <c>Backup:BigDatabaseMegabytes</c> (4096), a restart makes no copy while the newest is younger
///   than the interval, and a scheduled copy is checked once per <c>Backup:BigDatabaseCheckHours</c> (24): on a large file
///   the check takes far longer than the copy. A manual backup is always checked, and a restore always checks its file.
/// - **Disk:** a copy never takes the space the live database needs to grow: when a copy and <c>Backup:ReserveMegabytes</c>
///   do not fit, the oldest backups go first, and a failed copy is deleted at once.
/// </summary>
public sealed partial class DatabaseBackup(IOptions<ServerOptions> options, AccountStore accounts, ILogger<DatabaseBackup> log, Diagnostics.ServerMetrics? metrics = null) : BackgroundService
{
    internal const string Prefix = "ro3emu-", Extension = ".db", CompressedExtension = ".db.gz", PartialExtension = ".partial", CorruptExtension = ".corrupt";
    /// <summary>Copies a backup never removes to make room for a new one (inferred; server tuning).</summary>
    private const int KeepWhenShort = 2;
    private const string IntegrityMarker = ".backup-integrity-checked";
    private BackupOptions Settings => options.Value.Backup;
    private TimeSpan Interval => TimeSpan.FromHours(Settings.IntervalHours > 0 ? Settings.IntervalHours : 6);
    private readonly object _backupGate = new();
    private string? _lastName;
    private int _sameNameCount;
    private long _backups, _failures, _lastBytes;
    private double _lastMs;
    private DateTime? _lastAt;

    public string Directory => Path.Combine(options.Value.DataDir, "backups");

    /// <summary>Free bytes on the disk holding the directory; a location that cannot report it counts as having room.</summary>
    internal Func<string, long> FreeBytes { get; set; } = dir =>
    {
        try { return new DriveInfo(Path.GetFullPath(dir)).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }
    };

    /// <summary>Size of the live database file.</summary>
    internal Func<long> DatabaseBytes { get; set; } = () => new FileInfo(accounts.DatabasePath).Length;

    private bool BigDatabase => Settings.BigDatabaseMegabytes > 0 && DatabaseBytes() >= Settings.BigDatabaseMegabytes * 1024L * 1024;

    /// <summary>How long a starting host waits for its first copy: none for a small database, else what is left of the interval
    /// since the newest backup, compressed or not.</summary>
    internal TimeSpan UntilNextBackup(DateTime utcNow)
    {
        if (!BigDatabase || !System.IO.Directory.Exists(Directory) || Completed(Directory).FirstOrDefault() is not { } newest) return TimeSpan.Zero;
        var left = File.GetLastWriteTimeUtc(newest) + Interval - utcNow;
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left > Interval ? Interval : left;
    }

    /// <summary>Whether a scheduled copy runs the integrity check: always on a small database, else when the last passed check
    /// is older than <c>Backup:BigDatabaseCheckHours</c>.</summary>
    internal bool IntegrityCheckDue(DateTime utcNow)
    {
        var marker = new FileInfo(Path.Combine(options.Value.DataDir, IntegrityMarker));
        return !BigDatabase || !marker.Exists
            || utcNow - marker.LastWriteTimeUtc >= TimeSpan.FromHours(Math.Max(1, Settings.BigDatabaseCheckHours));
    }

    /// <summary>Test-only: runs on the empty destination before the copy starts.</summary>
    internal Action<SqliteConnection>? BeforeCopyForTest { get; set; }

    /// <summary>A copy of the live file and its WAL (the most a copy can take) plus the room the live database keeps to grow.</summary>
    internal long RequiredFreeBytes()
    {
        var wal = new FileInfo(accounts.DatabasePath + "-wal");
        return new FileInfo(accounts.DatabasePath).Length + (wal.Exists ? wal.Length : 0) + Math.Max(0L, Settings.ReserveMegabytes) * 1024 * 1024;
    }

    /// <summary>Unfinished copies of a crashed run are gone before the host reports itself started, under the same
    /// gate as a backup, so neither the startup backup nor a manual one can meet a half-written file.</summary>
    public override Task StartAsync(CancellationToken ct)
    {
        lock (_backupGate) RemoveStalePartials();
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var wait = UntilNextBackup(DateTime.UtcNow);
        if (wait > TimeSpan.Zero)
        {
            LogFirstBackupWaits(log, wait.TotalMinutes);
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Backup(IntegrityCheckDue(DateTime.UtcNow));
            }
            catch (Exception e) when (e is IOException or SqliteException or InvalidDataException)
            {
                log.LogError(e, "Database backup failed");
            }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public string Backup() => Backup(verify: true);

    internal string Backup(bool verify)
    {
        // Startup and manual requests serialize the copy, the check and the pruning.
        lock (_backupGate)
        {
            try { return BackupCore(verify); }
            catch { Interlocked.Increment(ref _failures); throw; }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Big database: the newest backup is recent, the first copy of this run waits {Minutes:F0} minutes")]
    private static partial void LogFirstBackupWaits(ILogger logger, double minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Big database: the backup {Target} was not integrity checked, a check ran within the last day")]
    private static partial void LogNotChecked(ILogger logger, string target);

    private string BackupCore(bool verify)
    {
        var dir = Directory;
        PrivateFiles.Directory(dir);
        MakeRoom(dir);
        var target = Path.Combine(dir, NextName());
        var partial = target + PartialExtension;
        var started = Stopwatch.GetTimestamp();
        try { Copy(partial, target, verify); }
        catch
        {
            // A failed copy can be as large as the database: leaving it would hold the disk the live file needs.
            try { File.Delete(partial); } catch (Exception ignored) when (ignored is IOException or UnauthorizedAccessException) { }
            throw;
        }
        File.Move(partial, target);
        PrivateFiles.Files(target);
        if (!verify) LogNotChecked(log, target);
        else
        {
            // Lost, the next scheduled copy just checks again.
            try { File.WriteAllBytes(Path.Combine(options.Value.DataDir, IntegrityMarker), []); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LogMarkerFailed(log, e); }
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the backup integrity check")]
    private static partial void LogMarkerFailed(ILogger logger, Exception exception);

    private void Copy(string partial, string target, bool verify)
    {
        using var source = accounts.Open();
        using var destination = new SqliteConnection($"Data Source={partial};Pooling=False");
        destination.Open();
        Diagnostics.SqliteMetrics.Attach(destination, metrics);
        Persistence.DbWriteGuard.AttachBackup(destination, accounts.AccessMetrics, accounts.DatabasePath);
        BeforeCopyForTest?.Invoke(destination);
        source.BackupDatabase(destination);
        if (verify && QuickCheck(destination) is { } problem)
        {
            destination.Close();
            var corrupt = target + CorruptExtension;
            File.Move(partial, corrupt, overwrite: true);
            log.LogCritical("Database backup failed its integrity check ({Problem}); the copy is kept as {Corrupt}", problem, corrupt);
            throw new InvalidDataException($"Backup integrity check failed: {problem}");
        }
    }

    /// <summary>
    /// Removes the oldest backups, never the newest <see cref="KeepWhenShort"/>, until a copy fits beside the live database's
    /// reserve. When even that is not enough nothing is removed and the backup is skipped.
    /// </summary>
    private void MakeRoom(string dir)
    {
        long required = RequiredFreeBytes();
        long free = FreeBytes(dir);
        if (free >= required) return;
        var oldestFirst = Completed(dir).Skip(KeepWhenShort).Reverse().ToList();
        if (free + oldestFirst.Sum(f => new FileInfo(f).Length) < required)
        {
            LogNoRoom(log, free, required);
            throw new IOException($"Not enough free disk space for a backup: {free} bytes free, {required} needed");
        }
        foreach (var old in oldestFirst)
        {
            if (FreeBytes(dir) >= required) break;
            LogRemovedForRoom(log, old);
            File.Delete(old);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database backup skipped: {Free} bytes free, a copy and the live database's reserve need {Required}")]
    private static partial void LogNoRoom(ILogger logger, long free, long required);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the old backup {Backup} to keep disk space for the live database")]
    private static partial void LogRemovedForRoom(ILogger logger, string backup);

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
                PrivateFiles.Files(compressed);
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
