using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>C/D 19: backup cut while writes advance, atomic completed files, offline restore.</summary>
public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-backup-" + Guid.NewGuid().ToString("N"));
    private ServerOptions Options() => new() { DataDir = _dir };
    private AccountStore Store() => new(Microsoft.Extensions.Options.Options.Create(Options()));
    private DatabaseBackup Backup(AccountStore accounts) =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), accounts, NullLogger<DatabaseBackup>.Instance);

    private static (double X, long Revision) PositionAndRevision(string path, long role)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.pos_x, COALESCE((SELECT revision FROM owner_revision WHERE kind='role' AND id=$id AND part=$part), 0)
            FROM role r WHERE r.id=$role
            """;
        command.Parameters.AddWithValue("$id", role.ToString());
        command.Parameters.AddWithValue("$part", RevisionStore.Position);
        command.Parameters.AddWithValue("$role", role);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetDouble(0), reader.GetInt64(1));
    }

    [Fact]
    public async Task EveryBackupTakenWhileTheWriterAdvancesIsOneCommittedCut()
    {
        using var accounts = Store();
        var roles = new RoleStore(accounts);
        var role = roles.Create("cut", 1, "Cut", 1, 1000, 1)!;
        roles.SavePosition(role.Id, 1, 1, 0, 0, 0);
        var first = PositionAndRevision(accounts.DatabasePath, role.Id);
        Assert.True(first.Revision > 0, "the position write must bump its revision in the same transaction");
        long offset = (long)first.X - first.Revision;

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int x = 1;
            while (!stop.IsCancellationRequested) roles.SavePosition(role.Id, 1, ++x, 0, 0, 0);
            return x;
        });
        var backup = Backup(accounts);
        var copies = new List<string>();
        for (int i = 0; i < 6; i++) { copies.Add(backup.Backup()); await Task.Delay(20); }
        stop.Cancel();
        int last = await writer;

        long previous = 0;
        foreach (var copy in copies)
        {
            var (x, revision) = PositionAndRevision(copy, role.Id);
            Assert.Equal(offset, (long)x - revision); // the row and its revision come from the same commit
            Assert.True(revision >= previous, "later backups never go back in time");
            previous = revision;
        }
        Assert.True(PositionAndRevision(copies[^1], role.Id).X <= last);
        Assert.Equal(copies.Count, copies.Distinct().Count());
        Assert.Empty(Directory.GetFiles(backup.Directory, "*" + DatabaseBackup.PartialExtension));
    }

    [Fact]
    public void PruningCountsOnlyCompletedBackupsAndStartupClearsUnfinishedOnes()
    {
        using var accounts = Store();
        new RoleStore(accounts).Create("prune", 1, "Prune", 1, 1000, 1);
        var backup = Backup(accounts);
        Directory.CreateDirectory(backup.Directory);
        // A crash mid-copy leaves a partial file; it must never count as one of the kept backups.
        string stale = Path.Combine(backup.Directory, "ro3emu-20000101-000000.db" + DatabaseBackup.PartialExtension);
        File.WriteAllText(stale, "half a database");
        var made = Enumerable.Range(0, 22).Select(_ => backup.Backup()).ToList();
        var kept = DatabaseBackup.Completed(backup.Directory).ToList();
        Assert.Equal(20, kept.Count);
        Assert.Equal(made.TakeLast(20).OrderBy(f => f), kept.OrderBy(f => f));
        foreach (var file in kept) DatabaseRestore.Validate(file);
        Assert.True(File.Exists(stale)); // only the service's startup removes it (no backup can be running then)
    }

    [Fact]
    public void RestoreRefusesWhileTheRealmIsOpenThenReplacesItAndKeepsTheOldFile()
    {
        string copy;
        long id;
        using (RealmLock.Acquire(_dir)) // what a running server holds
        using (var accounts = Store())
        {
            var roles = new RoleStore(accounts);
            id = roles.Create("restore", 1, "Before", 1, 1000, 1)!.Id;
            copy = Backup(accounts).Backup();
            roles.Create("restore", 1, "After", 1, 1000, 1);
            var refused = Assert.Throws<InvalidOperationException>(() => DatabaseRestore.Restore(_dir, copy));
            Assert.Contains("in use", refused.Message);
            Assert.Throws<InvalidOperationException>(() => RealmLock.Acquire(_dir)); // nor may a second server start
            Assert.Single(new RoleStore(accounts).ForAccount("restore"), r => r.Name == "After");
        }
        SqliteConnection.ClearAllPools();

        string? saved = DatabaseRestore.Restore(_dir, copy);
        Assert.NotNull(saved);
        Assert.True(File.Exists(Path.Combine(saved!, "ro3emu.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "ro3emu.db.restoring")));
        using var restored = Store();
        var names = new RoleStore(restored).ForAccount("restore").Select(r => r.Name).ToList();
        Assert.Equal(["Before"], names);
        Assert.NotNull(new RoleStore(restored).Get(id));
    }

    [Fact]
    public async Task ARunningServerHoldsItsDataDirAndItsStartupBackupClearsUnfinishedCopies()
    {
        string stale = Path.Combine(_dir, "backups", "ro3emu-20000101-000000.db" + DatabaseBackup.PartialExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "half a database");
        var (http, micro, game) = TestPorts.FreePorts();
        void Configure(ServerOptions o) { o.DataDir = _dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game; }
        var app = ServerHost.Create([], Configure);
        await app.StartAsync();
        string copy;
        try
        {
            var backup = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DatabaseBackup>().Single();
            copy = backup.Backup();
            Assert.False(File.Exists(stale));
            Assert.Contains("in use", Assert.Throws<InvalidOperationException>(() => ServerHost.Create([], Configure)).Message);
            Assert.Contains("in use", Assert.Throws<InvalidOperationException>(() => DatabaseRestore.Restore(_dir, copy)).Message);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        SqliteConnection.ClearAllPools();
        Assert.NotNull(DatabaseRestore.Restore(_dir, copy));
    }

    [Fact]
    public async Task LoopWaitInventoryCountsOnlySynchronousWaitsOnTheLoopByLabel()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        await using var app = ServerHost.Create([], o => { o.DataDir = _dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game; });
        await app.StartAsync();
        var accounts = app.Services.GetRequiredService<AccountStore>();
        var roles = app.Services.GetRequiredService<RoleStore>();
        long id = roles.Create("inventory", 1, "Inventory", 1, 1000, 1)!.Id;
        var before = accounts.AccessMetrics.LoopWaitCounts();
        roles.Get(id); // off the loop: not a loop wait
        var mid = accounts.AccessMetrics.LoopWaitCounts();
        await app.Services.GetRequiredService<Ro3Emu.Server.Game.GameLoop>().InvokeAsync(() =>
        {
            for (int i = 0; i < 3; i++) roles.Get(id);
            roles.SaveSettings(id, [7]);
            return Task.CompletedTask;
        });
        var after = accounts.AccessMetrics.LoopWaitCounts();
        // The server's own loop may read in the background: assert on the label these calls use, not on the whole map.
        string read = Assert.Single(after.Where(p => p.Key.StartsWith("R ", StringComparison.Ordinal)
            && p.Value - mid.GetValueOrDefault(p.Key) >= 3).Select(p => p.Key));
        Assert.Equal(before.GetValueOrDefault(read), mid.GetValueOrDefault(read)); // the off-loop call did not count
        Assert.Contains(after, p => p.Key.StartsWith("W ", StringComparison.Ordinal) && p.Value > mid.GetValueOrDefault(p.Key));
        Assert.NotEmpty(accounts.AccessMetrics.LoopWaits(5));
        await app.StopAsync();
    }

    [Fact]
    public void RestoreRefusesACorruptOrForeignFileAndLeavesTheRealmAlone()
    {
        using (var accounts = Store()) new RoleStore(accounts).Create("keep", 1, "Keep", 1, 1000, 1);
        SqliteConnection.ClearAllPools();
        string junk = Path.Combine(_dir, "junk.db");
        File.WriteAllBytes(junk, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        Assert.ThrowsAny<Exception>(() => DatabaseRestore.Restore(_dir, junk));
        string foreign = Path.Combine(_dir, "foreign.db");
        using (var db = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            db.Open();
            using var create = db.CreateCommand();
            create.CommandText = "CREATE TABLE something(id INTEGER)";
            create.ExecuteNonQuery();
        }
        Assert.Contains("not a realm database", Assert.Throws<InvalidDataException>(() => DatabaseRestore.Restore(_dir, foreign)).Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "restore-saved")));
        using var accounts2 = Store();
        Assert.Single(new RoleStore(accounts2).ForAccount("keep"));
    }

    [Fact]
    public void RestoreCliReportsTheOutcome()
    {
        string copy;
        using (var accounts = Store()) { new RoleStore(accounts).Create("cli", 1, "Cli", 1, 1000, 1); copy = Backup(accounts).Backup(); }
        SqliteConnection.ClearAllPools();
        var output = new StringWriter();
        Assert.Equal(0, DatabaseRestore.RunCli(copy, Options(), output));
        Assert.Contains("Restored", output.ToString());
        output = new StringWriter();
        Assert.Equal(1, DatabaseRestore.RunCli(Path.Combine(_dir, "missing.db"), Options(), output));
        Assert.Contains("Restore refused", output.ToString());
    }

    private static List<string> RoleNames(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM role ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>A realm holding "After" and a backup of it taken while it held only "Before".</summary>
    private string RealmWithAnOlderBackup()
    {
        string copy;
        using (var accounts = Store())
        {
            var roles = new RoleStore(accounts);
            roles.Create("fault", 1, "Before", 1, 1000, 1);
            copy = Backup(accounts).Backup();
            roles.Create("fault", 1, "After", 1, 1000, 1);
        }
        SqliteConnection.ClearAllPools();
        return copy;
    }

    private string Live => Path.Combine(_dir, "ro3emu.db");

    [Fact]
    public void RestoreThatFailsWhilePreparingTheCopyNeverTouchesTheLiveDatabase()
    {
        string copy = RealmWithAnOlderBackup();
        // The copy fails outright, then a copy that lands damaged fails its check: both before the live file is touched.
        Assert.Throws<IOException>(() => DatabaseRestore.Restore(_dir, copy, step =>
        {
            if (step == "copied") throw new IOException("disk full");
        }));
        Assert.ThrowsAny<Exception>(() => DatabaseRestore.Restore(_dir, copy, step => // quick_check or SQLite itself rejects it
        {
            if (step != "copied") return;
            using var file = new FileStream(Live + DatabaseRestore.IncomingSuffix, FileMode.Open);
            file.Position = 4096;
            file.Write(Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        }));
        Assert.False(File.Exists(DatabaseRestore.MarkerPath(_dir)));
        Assert.False(File.Exists(Live + DatabaseRestore.IncomingSuffix));
        Assert.False(Directory.Exists(Path.Combine(_dir, "restore-saved")));
        Assert.Equal(["After", "Before"], RoleNames(Live));
        using var accounts = Store();
        Assert.Equal(2, new RoleStore(accounts).ForAccount("fault").Count());
    }

    [Theory]
    [InlineData("moved")]
    [InlineData("swap")]
    public void RestoreThatFailsMidSwapPutsTheOldDatabaseBack(string failAt)
    {
        string copy = RealmWithAnOlderBackup();
        Assert.Throws<IOException>(() => DatabaseRestore.Restore(_dir, copy, step =>
        {
            if (step == failAt) throw new IOException("rename failed");
        }));
        Assert.False(File.Exists(DatabaseRestore.MarkerPath(_dir)));
        Assert.False(File.Exists(Live + DatabaseRestore.IncomingSuffix));
        Assert.Equal(["After", "Before"], RoleNames(Live));
        using (var accounts = Store()) Assert.Equal(2, new RoleStore(accounts).ForAccount("fault").Count());
        SqliteConnection.ClearAllPools();
        Assert.NotNull(DatabaseRestore.Restore(_dir, copy)); // nothing was left half-done: a clean retry works
        Assert.Equal(["Before"], RoleNames(Live));
    }

    [Fact]
    public void ARestoreThatCannotRollBackLeavesAMarkerThatRefusesStartUntilItIsFinished()
    {
        string copy = RealmWithAnOlderBackup();
        // The swap fails and so does the rollback: the live file is gone and only the marker says so.
        var failed = Assert.Throws<IOException>(() => DatabaseRestore.Restore(_dir, copy, step =>
        {
            if (step is "swap" or "rollback") throw new IOException("volume went away");
        }));
        Assert.Contains("To recover", failed.Message);
        Assert.True(File.Exists(DatabaseRestore.MarkerPath(_dir)));
        Assert.False(File.Exists(Live));

        // Neither a store nor a whole server may open the realm: either would create an empty database here.
        var refused = Assert.Throws<InvalidOperationException>(() => Store());
        Assert.Contains("unfinished database restore", refused.Message);
        Assert.Contains("restore-saved", refused.Message);
        Assert.Contains("unfinished database restore", Assert.Throws<InvalidOperationException>(() => ServerHost.Create([], o =>
        {
            var (http, micro, game) = TestPorts.FreePorts();
            o.DataDir = _dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        })).Message);
        Assert.False(File.Exists(Live));

        // The previous database is intact in restore-saved, and running the restore again finishes the job.
        string saved = Assert.Single(Directory.GetDirectories(Path.Combine(_dir, "restore-saved")));
        Assert.Equal(["After", "Before"], RoleNames(Path.Combine(saved, "ro3emu.db")));
        Assert.Null(DatabaseRestore.Restore(_dir, copy)); // no live file to set aside this time
        Assert.False(File.Exists(DatabaseRestore.MarkerPath(_dir)));
        using var accounts = Store();
        Assert.Equal(["Before"], new RoleStore(accounts).ForAccount("fault").Select(r => r.Name).ToList());
        Assert.True(File.Exists(Path.Combine(saved, "ro3emu.db")));
    }

    /// <summary>
    /// The state an earlier restore leaves when it is cut between moving ro3emu.db and its WAL: the database in a saved
    /// folder named by the marker, and its WAL, holding a committed change the database file does not have yet, still
    /// in the data dir. Returns the saved folder.
    /// </summary>
    private string InterruptedBetweenTheDatabaseAndItsWal()
    {
        string saved = Path.Combine(_dir, "restore-saved", "20261018-000000-000");
        Directory.CreateDirectory(saved);
        string wal = Path.Combine(_dir, "orphan-wal");
        using (var db = new SqliteConnection($"Data Source={Live};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint=0; UPDATE role SET name='InWal' WHERE name='After';";
            command.ExecuteNonQuery();
            // Copied while the change is only in the WAL (closing the connection would checkpoint it).
            CopyShared(Live, Path.Combine(saved, "ro3emu.db"));
            CopyShared(Live + "-wal", wal);
        }
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Live + suffix);
        File.Move(wal, Live + "-wal");
        File.WriteAllText(DatabaseRestore.MarkerPath(_dir), $"started: earlier{Environment.NewLine}backup: x{Environment.NewLine}" +
            $"previous database saved in: {saved}{Environment.NewLine}");
        return saved;

        static void CopyShared(string from, string to)
        {
            using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = File.Create(to);
            source.CopyTo(target);
        }
    }

    [Fact]
    public void ResumingARestoreCutBeforeTheWalMovedNeverReplaysTheOldWalIntoTheBackup()
    {
        string copy = RealmWithAnOlderBackup();
        string earlier = InterruptedBetweenTheDatabaseAndItsWal();
        Assert.Contains("unfinished database restore", Assert.Throws<InvalidOperationException>(() => Store()).Message);

        // The database itself was already set aside; its leftover WAL joins it there.
        Assert.Equal(earlier, DatabaseRestore.Restore(_dir, copy));
        Assert.False(File.Exists(DatabaseRestore.MarkerPath(_dir)));
        Assert.Equal(["Before"], RoleNames(Live)); // exactly the backup, nothing from the old WAL
        using (var accounts = Store()) Assert.Equal(["Before"], new RoleStore(accounts).ForAccount("fault").Select(r => r.Name).ToList());
        SqliteConnection.ClearAllPools();
        // The old WAL went back to its own database: that set is complete, with the change only the WAL held.
        Assert.True(File.Exists(Path.Combine(earlier, "ro3emu.db-wal")));
        Assert.Equal(["Before", "InWal"], RoleNames(Path.Combine(earlier, "ro3emu.db")));
    }

    [Fact]
    public void AFailedRetryOfAnUnfinishedRestoreKeepsRefusingStart()
    {
        string copy = RealmWithAnOlderBackup();
        string earlier = InterruptedBetweenTheDatabaseAndItsWal();
        Assert.Throws<IOException>(() => DatabaseRestore.Restore(_dir, copy, step =>
        {
            if (step == "swap") throw new IOException("rename failed");
        }));
        // Still no database in the data dir: the marker stays and nothing may open (and create) an empty realm.
        Assert.False(File.Exists(Live));
        Assert.True(File.Exists(DatabaseRestore.MarkerPath(_dir)));
        Assert.Contains("unfinished database restore", Assert.Throws<InvalidOperationException>(() => Store()).Message);
        SqliteConnection.ClearAllPools();
        Assert.Equal(earlier, DatabaseRestore.Restore(_dir, copy)); // a clean retry finishes it
        Assert.Equal(["Before"], RoleNames(Live));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
