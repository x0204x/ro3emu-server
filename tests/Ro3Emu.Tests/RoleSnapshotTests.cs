using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>C/D 14: one-snapshot role reads, invalidation by committed writes, and login without loop SQL reads.</summary>
public sealed class RoleSnapshotTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-snap-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    private long NewRole(string name) => Service<RoleStore>().Create(name.ToLowerInvariant(), 1, name, 1, 1000, 1)!.Id;

    private static byte[] Marker(int i) => BitConverter.GetBytes(i);

    [Fact]
    public async Task EverySnapshotIsOneVersionWhileTwoColumnsAlternateTogether()
    {
        long role = NewRole("Snappy");
        var accounts = Service<AccountStore>();
        using var stop = new CancellationTokenSource();
        int written = 0;
        // One transaction writes the same marker into two blobs; a mixed pair would be a torn snapshot.
        var writer = Task.Run(() =>
        {
            for (int i = 1; !stop.IsCancellationRequested; i++)
            {
                FixtureSql.Execute(accounts, "UPDATE role SET daily=$v, tasks=$v WHERE id=$id", ("$v", Marker(i)), ("$id", role));
                written = i;
            }
        });
        int checkedSnapshots = 0;
        try
        {
            while (Volatile.Read(ref written) < 50) await Task.Delay(1);
            for (int n = 0; n < 200; n++)
            {
                var snapshot = await accounts.ReadAsync<RoleSnapshot>(new RoleSnapshotReadPlan(role));
                var daily = snapshot.Blobs["daily"];
                var tasks = snapshot.Blobs["tasks"];
                Assert.NotNull(daily);
                Assert.Equal(daily, tasks);
                checkedSnapshots++;
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
        Assert.Equal(200, checkedSnapshots);
        Assert.True(written > 50, $"only {written} alternations");
    }

    [Fact]
    public async Task ServedReadsEqualSqlForNewAndCorruptOrEmptyColumns()
    {
        long role = NewRole("Fresh");
        var roles = Service<RoleStore>();
        var accounts = Service<AccountStore>();
        FixtureSql.Execute(accounts, "UPDATE role SET lottery=$v WHERE id=$id", ("$v", new byte[] { 0xFF, 0x00, 0x13 }), ("$id", role));
        Assert.True(await roles.PrepareLoginSnapshotAsync(role, "fresh"));
        try
        {
            Assert.Null(roles.LoadTasks(role)); // a new role has no quest blob: served null, not missing
            Assert.Equal(new byte[] { 0xFF, 0x00, 0x13 }, roles.LoadLottery(role)); // undecodable bytes are served as stored
            var progress = roles.LoadProgress(role);
            Assert.Equal(1000, progress.Career);
            Assert.Empty(Service<ItemStore>().Load(role));
            Assert.Equal("Fresh", roles.Get(role)!.Name);
        }
        finally { roles.ReleaseLoginSnapshot(role); }
        Assert.True(accounts.Snapshots.Served >= 5);
        Assert.False(await roles.PrepareLoginSnapshotAsync(role, "someone-else")); // never installed for another account
    }

    [Fact]
    public async Task CommittedWritesInvalidateTheirPartsAndARacingReadNeverServesOlderData()
    {
        long role = NewRole("Racer");
        var roles = Service<RoleStore>();
        var accounts = Service<AccountStore>();
        var loop = Service<GameLoop>();
        Task Write(Action action) => loop.InvokeAsync(() => { action(); return Task.CompletedTask; });

        // Installed, then written by the role's own save: written through (C/D 16 part 4), served with the new bytes.
        await Write(() => roles.SaveDaily(role, Marker(1)));
        await Write(() => roles.SaveTower(role, Marker(1)));
        Assert.True(await roles.PrepareLoginSnapshotAsync(role, "racer"));
        await Write(() => roles.SaveDaily(role, Marker(2)));
        long servedBefore = accounts.Snapshots.Served;
        Assert.Equal(Marker(2), roles.LoadDaily(role));
        Assert.Equal(servedBefore + 1, accounts.Snapshots.Served);
        Assert.Equal(Marker(1), roles.LoadTower(role));
        Assert.Equal(servedBefore + 2, accounts.Snapshots.Served);
        // Written by another path (a transaction): the part is dropped and comes from SQL.
        var other = roles.NewTransaction();
        other.RoleBlob(role, "daily", Marker(5));
        await loop.InvokeAsync(() => { roles.Commit(other, "test/other-daily-writer"); return Task.CompletedTask; });
        servedBefore = accounts.Snapshots.Served;
        Assert.Equal(Marker(5), roles.LoadDaily(role));
        Assert.Equal(servedBefore, accounts.Snapshots.Served);
        roles.ReleaseLoginSnapshot(role);

        // Read began, a write committed, then install: that part must be dropped although the snapshot missed it.
        long started = accounts.Snapshots.Sequence;
        var snapshot = await accounts.ReadAsync<RoleSnapshot>(new RoleSnapshotReadPlan(role));
        await Write(() => roles.SaveTower(role, Marker(3)));
        accounts.Snapshots.Install(snapshot, started);
        try { Assert.Equal(Marker(3), roles.LoadTower(role)); }
        finally { roles.ReleaseLoginSnapshot(role); }
    }

    [Fact]
    public async Task LoginReadsTheRoleFromTheSnapshotInsteadOfLoopSql()
    {
        var (withSnapshot, served) = await LoginLoopReads("Snapshotted", snapshots: true);
        var (without, _) = await LoginLoopReads("Unsnapshotted", snapshots: false);
        await LoginLoopReads("Snapshotted", snapshots: true, returning: true);
        output.WriteLine($"loop-blocking role-data SQL reads per login: before {without}, after {withSnapshot}; snapshot values served {served}; " +
            $"snapshot {Service<RoleStore>().LastLoginSnapshot.Bytes} bytes read in {Service<RoleStore>().LastLoginSnapshot.ReadMs:0.0} ms");
        Assert.True(served >= 20, $"only {served} reads served");
        Assert.Equal(0, withSnapshot); // every role blob/progress/item/identity read of the login came from the snapshot
        Assert.True(without >= 50, $"before {without}");
    }

    [Fact]
    public async Task AnAttachedRoleKeepsItsSnapshotForTheSessionWithItsOwnSavesWrittenThrough()
    {
        var accounts = Service<AccountStore>();
        var roles = Service<RoleStore>();
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("sessionsnap", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("SessionSnap")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var loop = Service<GameLoop>();
        int blobReads = 0;
        accounts.ReadSyncObserved = (label, onLoop) => { if (onLoop && label == "role/read-blob") Interlocked.Increment(ref blobReads); };
        byte[]? before = null, after = null;
        await loop.InvokeAsync(() =>
        {
            before = roles.LoadTasks(id);          // after login: still served from the session's snapshot
            roles.SaveTasks(id, [42, 43]);
            after = roles.LoadTasks(id);           // the role's own committed save, written through
            return Task.CompletedTask;
        });
        accounts.ReadSyncObserved = null;
        if (!RoleSnapshots.VerifyAgainstSql) Assert.Equal(0, blobReads); // verify mode re-reads every served value from SQL
        Assert.Equal(new byte[] { 42, 43 }, after);
        Assert.True(accounts.Snapshots.TryBlob(id, "tasks", out _));
        Assert.True(accounts.Snapshots.IsInstalled(id));
        // Another writer of the part (a transaction) invalidates it: the next read goes to SQL and sees it.
        var other = roles.NewTransaction();
        roles.SaveTasks(id, [7], other);
        roles.Commit(other, "test/other-tasks-writer");
        Assert.False(accounts.Snapshots.TryBlob(id, "tasks", out _));
        Assert.Equal(new byte[] { 7 }, await loop.InvokeAsync(() => Task.FromResult(roles.LoadTasks(id))));
        // Leaving the world releases the snapshot.
        await bot.DisposeAsync();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (accounts.Snapshots.IsInstalled(id))
        {
            Assert.True(DateTime.UtcNow < until, "the role's snapshot was not released on leave");
            await Task.Delay(20);
        }
    }

    private async Task<(long LoopReads, long Served)> LoginLoopReads(string name, bool snapshots, bool returning = false)
    {
        var accounts = Service<AccountStore>();
        Service<RoleStore>().LoginSnapshots = snapshots;
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = returning ? bot.Roles.Single().Id : (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        var labels = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        accounts.ReadSyncObserved = (label, onLoop) => { if (onLoop) labels.AddOrUpdate(label, 1, (_, n) => n + 1); };
        var writes = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        accounts.WriteSyncObserved = (label, onLoop) => { if (onLoop) writes.AddOrUpdate(label, 1, (_, n) => n + 1); };
        long servedBefore = accounts.Snapshots.Served, unchangedBefore = accounts.Snapshots.PersistedSkips,
            skippedBefore = accounts.Snapshots.SkippedWrites;
        await bot.EnterWorldAsync(id);
        long served = accounts.Snapshots.Served - servedBefore;
        long unchanged = accounts.Snapshots.PersistedSkips - unchangedBefore
            + accounts.Snapshots.SkippedWrites - skippedBefore;
        accounts.ReadSyncObserved = null;
        accounts.WriteSyncObserved = null;
        // Role-owned data only: realm maintenance (market, guild) in the same window is unrelated to the login.
        long reads = labels.Where(x => x.Key is "role/read-blob" or "role/read-progress" or "item/read" or "role/micro-projection")
            .Sum(x => x.Value);
        // The test verification mode reads every served value from SQL as well; those are not login reads.
        // So does every skipped unchanged save (C/D 16 part 3).
        if (RoleSnapshots.VerifyAgainstSql) reads -= served + unchanged;
        if (returning) name += " (returning)";
        output.WriteLine($"{name}: " + string.Join(", ", labels.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}")));
        output.WriteLine($"{name}: loop-blocking writes " + string.Join(", ", writes.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}")));
        await bot.DisposeAsync();
        return (reads, served);
    }

    private static long LoopReads(AccountStore accounts)
    {
        var m = JsonSerializer.SerializeToElement(accounts.AccessMetrics.Snapshot());
        return m.GetProperty("legacyLoopSync").GetInt64() - m.GetProperty("legacyWriterLoopSync").GetInt64();
    }

    [Fact]
    public void ASnapshotReadOlderThanThePrunedWriteHistoryIsNotInstalled()
    {
        // Audit 11.10: the commit history keeps only the last 4096 writes once it exceeds 8192. A login snapshot read
        // that began before the forgotten writes could not tell that one of them touched its role.
        var snapshots = new RoleSnapshots();
        long readStartedAt = snapshots.Sequence;
        snapshots.Committed([new RoleRevisionKey(1, "daily")]); // newer than the read
        for (long other = 2; other < 8300; other++) snapshots.Committed([new RoleRevisionKey(other, "daily")]);
        var identity = new Role(1, "acct", 1, "Old", 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0);
        snapshots.Install(new RoleSnapshot { RoleId = 1, Identity = identity, Blobs = { ["daily"] = [1] } }, readStartedAt);
        Assert.False(snapshots.TryBlob(1, "daily", out _));
        Assert.Equal(1, snapshots.StaleInstalls);

        // A read that began after the pruning is installed as before.
        snapshots.Install(new RoleSnapshot { RoleId = 1, Identity = identity, Blobs = { ["daily"] = [2] } }, snapshots.Sequence);
        Assert.True(snapshots.TryBlob(1, "daily", out var served));
        Assert.Equal(new byte[] { 2 }, served);
    }
}
