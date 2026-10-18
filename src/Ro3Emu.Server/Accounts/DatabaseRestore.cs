using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Offline restore of a backup made by <see cref="DatabaseBackup"/> (C/D 19): <c>Ro3Emu.Server --restore-backup &lt;file&gt;</c>.
/// - The backup must pass <c>PRAGMA quick_check</c> and hold the realm's core tables.
/// - The realm must not be open: the restore takes the data dir's <see cref="RealmLock"/>, which a running server holds.
/// - **Prepare first:** the backup is copied beside the live file as <c>.restoring</c>, flushed and checked again before
///   anything live is touched, so a failed or bad copy leaves the realm as it was.
/// - **Journalled swap:** <c>ro3emu.db.restore-pending</c> is written (and flushed) before the live database with its
///   WAL is moved to <c>&lt;data&gt;/restore-saved/</c> (never deleted) and the copy renamed in. A failed swap moves the
///   old files back. Only a completed or rolled-back restore removes the marker; while it exists
///   <see cref="ThrowIfUnfinished"/> refuses to open the realm, so a server never creates an empty database in the gap.
/// </summary>
public static class DatabaseRestore
{
    private static readonly string[] CoreTables = ["account", "role"];
    private static readonly string[] Sidecars = ["", "-wal", "-shm"];
    internal const string IncomingSuffix = ".restoring", MarkerName = "ro3emu.db.restore-pending";

    public static string MarkerPath(string dataDir) => Path.Combine(dataDir, MarkerName);

    public static int RunCli(string backup, ServerOptions options, TextWriter output)
    {
        try
        {
            var saved = Restore(options.DataDir, backup);
            output.WriteLine($"Restored {backup} into {Path.Combine(options.DataDir, "ro3emu.db")}");
            if (saved != null) output.WriteLine($"The replaced database is kept in {saved}");
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or SqliteException or UnauthorizedAccessException)
        {
            output.WriteLine("Restore refused: " + e.Message);
            return 1;
        }
    }

    /// <summary>Restores <paramref name="backup"/>; returns the folder holding the replaced database (null if none).</summary>
    public static string? Restore(string dataDir, string backup) => Restore(dataDir, backup, null);

    /// <param name="stepForTest">Called at each step ("copied", "moved", "moved-wal", "moved-shm", "swap", "rollback",
    /// "swapped"); a test throws from it to fail the restore there.</param>
    internal static string? Restore(string dataDir, string backup, Action<string>? stepForTest)
    {
        backup = Path.GetFullPath(backup);
        if (!File.Exists(backup)) throw new IOException($"No backup at {backup}");
        Validate(backup);
        var live = Path.Combine(dataDir, "ro3emu.db");
        if (string.Equals(Path.GetFullPath(live), backup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backup is the live database.");
        using var held = RealmLock.Acquire(dataDir); // a running server holds it; none may start mid-restore

        // 1. Prepare: a flushed, checked copy beside the live file. Nothing live has been touched yet.
        var incoming = live + IncomingSuffix;
        try
        {
            CopyDurably(backup, incoming);
            stepForTest?.Invoke("copied");
            Validate(incoming);
        }
        catch
        {
            DeleteQuietly(incoming);
            throw;
        }
        finally
        {
            // A read-only open of a WAL-mode file may leave empty sidecars; they must not follow the copy in.
            DeleteQuietly(incoming + "-wal");
            DeleteQuietly(incoming + "-shm");
        }

        // 2. Journal, then swap. The marker outlives a crash at any point below.
        var marker = MarkerPath(dataDir);
        string? saved = File.Exists(live) ? Path.Combine(dataDir, "restore-saved", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")) : null;
        WriteMarker(marker, backup, saved);
        var moved = new List<string>();
        try
        {
            if (saved != null)
            {
                Directory.CreateDirectory(saved);
                foreach (var suffix in Sidecars)
                {
                    if (!File.Exists(live + suffix)) continue;
                    File.Move(live + suffix, Path.Combine(saved, "ro3emu.db" + suffix));
                    moved.Add(suffix);
                    stepForTest?.Invoke("moved" + suffix);
                }
            }
            stepForTest?.Invoke("swap");
            File.Move(incoming, live);
        }
        catch (Exception failure)
        {
            // Put the previous database back exactly as it was; only then is the restore over.
            try
            {
                stepForTest?.Invoke("rollback");
                foreach (var suffix in moved) File.Move(Path.Combine(saved!, "ro3emu.db" + suffix), live + suffix);
            }
            catch (Exception rollback)
            {
                throw new IOException($"The restore failed ({failure.Message}) and putting the previous database back failed too " +
                    $"({rollback.Message}). {RecoveryHelp(marker)}", failure);
            }
            File.Delete(marker);
            DeleteQuietly(incoming);
            throw;
        }
        stepForTest?.Invoke("swapped");
        File.Delete(marker);
        return saved;
    }

    /// <summary>
    /// Refuses to open the realm while a restore is unfinished (C/D 19): the live database may be missing or be the old
    /// one, and opening it would create an empty realm or hide the half-done restore.
    /// </summary>
    public static void ThrowIfUnfinished(string dataDir)
    {
        var marker = MarkerPath(dataDir);
        if (!File.Exists(marker)) return;
        string journal;
        try { journal = File.ReadAllText(marker).Trim(); }
        catch (IOException e) { journal = "(unreadable: " + e.Message + ")"; }
        throw new InvalidOperationException($"An unfinished database restore was found in {dataDir}; the server will not open the realm. " +
            $"{RecoveryHelp(marker)}{Environment.NewLine}{journal}");
    }

    private static string RecoveryHelp(string marker) =>
        "To recover, either run the restore again (Ro3Emu.Server --restore-backup <file>), or move the previous database " +
        $"(ro3emu.db with its -wal/-shm) back from the restore-saved folder named in {marker} into the data dir; then delete {marker}.";

    private static void WriteMarker(string marker, string backup, string? saved)
    {
        // An earlier unfinished restore's journal is kept: its saved folder may hold the only copy of the old database.
        string earlier = File.Exists(marker) ? File.ReadAllText(marker) : "";
        using var file = new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.None);
        using (var writer = new StreamWriter(file, leaveOpen: true))
        {
            writer.Write(earlier);
            writer.WriteLine($"started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"backup: {backup}");
            writer.WriteLine($"previous database saved in: {saved ?? "(there was no live database)"}");
        }
        file.Flush(flushToDisk: true);
    }

    private static void CopyDurably(string source, string target)
    {
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        from.CopyTo(to);
        to.Flush(flushToDisk: true);
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Throws unless the file passes quick_check and holds the realm's core tables.</summary>
    public static void Validate(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        if (DatabaseBackup.QuickCheck(db) is { } problem) throw new InvalidDataException($"{path} fails its integrity check: {problem}");
        using var tables = db.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var present = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = tables.ExecuteReader()) while (reader.Read()) present.Add(reader.GetString(0));
        var missing = CoreTables.Where(t => !present.Contains(t)).ToArray();
        if (missing.Length != 0) throw new InvalidDataException($"{path} is not a realm database (missing {string.Join(", ", missing)})");
    }
}
