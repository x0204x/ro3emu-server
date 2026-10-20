using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>C/D 19 writer fault paths: lazy-lane fairness under sustained durable load, post-commit failures.</summary>
public sealed class DbWriterFaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-writer-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _executed = new();

    public DbWriterFaultTests() => Directory.CreateDirectory(_dir);
    private string Database => Path.Combine(_dir, "writer.db");

    private sealed class Plan(string name, ConcurrentQueue<string> executed, Action? during = null, bool failAfterCommit = false) : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/" + name;
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            during?.Invoke();
            using var command = DbSql.Command(db, tx, "CREATE TABLE IF NOT EXISTS done(name TEXT NOT NULL); INSERT INTO done(name) VALUES ($n)");
            command.Parameters.AddWithValue("$n", name);
            command.ExecuteNonQuery();
            executed.Enqueue(name);
            return 1;
        }
        internal override void AfterCommit()
        {
            if (failAfterCommit) throw new IOException("Test-only failure after commit.");
        }
    }

    [Fact]
    public async Task AnOverdueLazyJobRunsBetweenDurableJobsInsteadOfAfterTheWholeBacklog()
    {
        var metrics = new DbAccessMetrics();
        using var writer = new DbWriter($"Data Source={Database}", metrics) { LazyMaxWait = TimeSpan.FromMilliseconds(30) };
        using var hold = new ManualResetEventSlim();
        var blocker = writer.ExecuteAsync<int>(new Plan("block", _executed, () => hold.Wait(TimeSpan.FromSeconds(10))));
        var durable = Enumerable.Range(0, 100).Select(i => writer.ExecuteAsync<int>(new Plan("d" + i, _executed, () => Thread.Sleep(1)))).ToList();
        var lazy = new[] { writer.ExecuteAsync<int>(new Plan("lazy0", _executed), lazy: true), writer.ExecuteAsync<int>(new Plan("lazy1", _executed), lazy: true) };
        await Task.Delay(80); // both lazy jobs are now older than LazyMaxWait
        hold.Set();
        await Task.WhenAll(durable.Append(blocker).Concat(lazy));

        var order = _executed.ToList();
        Assert.Equal(103, order.Count);
        int first = order.IndexOf("lazy0"), second = order.IndexOf("lazy1");
        Assert.InRange(first, 1, 2);            // right after the job that held the writer
        Assert.Equal(first + 2, second);         // alternated with one durable job, not two lazy in a row
        Assert.Equal(Enumerable.Range(0, 100).Select(i => "d" + i), order.Where(n => n.StartsWith('d'))); // durable order kept
        Assert.Equal(2, JsonSerializer.SerializeToElement(metrics.Snapshot()).GetProperty("lazyPromoted").GetInt64());
    }

    [Fact]
    public async Task AFreshLazyJobStillYieldsToDurableWork()
    {
        var metrics = new DbAccessMetrics();
        using var writer = new DbWriter($"Data Source={Database}", metrics) { LazyMaxWait = TimeSpan.FromSeconds(30) };
        using var hold = new ManualResetEventSlim();
        var blocker = writer.ExecuteAsync<int>(new Plan("block", _executed, () => hold.Wait(TimeSpan.FromSeconds(10))));
        var lazy = writer.ExecuteAsync<int>(new Plan("lazy", _executed), lazy: true);
        var durable = Enumerable.Range(0, 5).Select(i => writer.ExecuteAsync<int>(new Plan("d" + i, _executed))).ToList();
        hold.Set();
        await Task.WhenAll(durable.Append(blocker).Append(lazy));
        Assert.Equal("lazy", _executed.Last());
    }

    private sealed class RolePlan(long role, RoleRevisionVector vector, ConcurrentQueue<string> executed) : DbPlan
    {
        private readonly Plan inner = new("role-write", executed);
        internal override int Bytes => 64;
        internal override string Label => "test/role-write";
        internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.Identity)];
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => inner.Execute(db, tx);
        internal RolePlan Captured() { Expect([vector]); return this; }
    }

    [Fact]
    public async Task AFailedSnapshotInvalidationAfterCommitStillReportsTheCommitButAPlanStepKeepsItsFailure()
    {
        long role;
        RoleRevisionVector vector;
        using (var accounts = new Ro3Emu.Server.Accounts.AccountStore(Microsoft.Extensions.Options.Options.Create(new Ro3Emu.Server.ServerOptions { DataDir = _dir })))
        {
            var roles = new Ro3Emu.Server.Accounts.RoleStore(accounts);
            role = roles.Create("commit", 1, "Commit", 1, 1000, 1)!.Id;
            vector = roles.ReadRevisionVector(role);
        }
        SqliteConnection.ClearAllPools();
        var metrics = new DbAccessMetrics();
        using var writer = new DbWriter($"Data Source={Path.Combine(_dir, "ro3emu.db")}", metrics) { RoleWritesCommitted = _ => throw new InvalidOperationException("Test-only invalidation failure.") };
        // The writer's own post-commit step: the write is durable, so it completes as committed (no retry invited).
        Assert.Equal(1, await writer.ExecuteAsync<int>(new RolePlan(role, vector, _executed).Captured()));
        // A plan-defined step keeps its meaning: receipt-backed plans signal a lost acknowledgement this way.
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync<int>(new Plan("plan-step", _executed, failAfterCommit: true)));
        Assert.Equal(1, await writer.ExecuteAsync<int>(new Plan("next", _executed))); // the writer keeps working
        Assert.Equal(["role-write", "plan-step", "next"], _executed);
        var snapshot = JsonSerializer.SerializeToElement(metrics.Snapshot());
        Assert.Equal(1, snapshot.GetProperty("postCommitFailures").GetInt64());
        Assert.False(snapshot.GetProperty("writerTerminal").GetBoolean());
    }

    [Fact]
    public async Task AcceptedWorkFinishesWhenTheWriterStopsAndLaterWorkIsRefused()
    {
        var metrics = new DbAccessMetrics();
        var writer = new DbWriter($"Data Source={Database}", metrics);
        using var hold = new ManualResetEventSlim();
        var accepted = new List<Task<int>> { writer.ExecuteAsync<int>(new Plan("block", _executed, () => hold.Wait(TimeSpan.FromSeconds(10)))) };
        accepted.AddRange(Enumerable.Range(0, 20).Select(i => writer.ExecuteAsync<int>(new Plan("q" + i, _executed))));
        accepted.Add(writer.ExecuteAsync<int>(new Plan("lazy", _executed), lazy: true));
        var stopping = Task.Run(writer.Dispose); // shutdown begins while 21 accepted jobs wait behind the blocker
        await Task.Delay(50);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.ExecuteAsync<int>(new Plan("late", _executed)));
        hold.Set();
        Assert.All(await Task.WhenAll(accepted), result => Assert.Equal(1, result));
        await stopping;

        SqliteConnection.ClearAllPools();
        using var db = new SqliteConnection($"Data Source={Database}");
        db.Open();
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM done";
        Assert.Equal(22L, (long)count.ExecuteScalar()!); // every accepted job is durable, the refused one is not
        Assert.DoesNotContain("late", _executed);
    }

    [Fact]
    public async Task AFailureJustBeforeCommitRollsBackTheWholeJobAndTheWriterContinues()
    {
        var metrics = new DbAccessMetrics();
        using var writer = new DbWriter($"Data Source={Database}", metrics)
        {
            // Stands for a disk-full or I/O error at COMMIT: the job's statements already ran in its transaction.
            BeforeCommitForTest = label => { if (label == "test/doomed") throw new IOException("Test-only failure at commit."); },
        };
        Assert.Equal(1, await writer.ExecuteAsync<int>(new Plan("first", _executed)));
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync<int>(new Plan("doomed", _executed)));
        Assert.Equal(1, await writer.ExecuteAsync<int>(new Plan("after", _executed)));
        Assert.Equal(["first", "doomed", "after"], _executed); // the doomed plan executed, then rolled back

        SqliteConnection.ClearAllPools();
        using var db = new SqliteConnection($"Data Source={Database}");
        db.Open();
        using var rows = db.CreateCommand();
        rows.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM done ORDER BY rowid)";
        Assert.Equal("first,after", (string)rows.ExecuteScalar()!);
        Assert.False(JsonSerializer.SerializeToElement(metrics.Snapshot()).GetProperty("writerTerminal").GetBoolean());
    }

    /// <summary>Writes its row, then fails: its own partial write must be rolled back, nobody else's.</summary>
    private sealed class PartialPlan(string name, ConcurrentQueue<string> executed) : DbPlan
    {
        private readonly Plan inner = new(name, executed);
        internal override int Bytes => 64;
        internal override string Label => "test/" + name;
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            inner.Execute(db, tx);
            throw new InvalidOperationException("Test-only failure after a partial write.");
        }
    }

    private string Rows()
    {
        SqliteConnection.ClearAllPools();
        using var db = new SqliteConnection($"Data Source={Database}");
        db.Open();
        using var rows = db.CreateCommand();
        rows.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM done ORDER BY rowid)";
        return (string)rows.ExecuteScalar()!;
    }

    [Fact]
    public async Task AQueuedBacklogCommitsInGroupsAndAFailedJobRollsBackOnlyItself()
    {
        // Stage G group commit: jobs queued behind a busy writer share one durable commit, each in its own savepoint.
        var metrics = new DbAccessMetrics();
        // A group budget the blocker always overruns and the backlog never does, however fast the machine.
        var budget = TimeSpan.FromMilliseconds(300);
        using var writer = new DbWriter($"Data Source={Database}", metrics)
        {
            MaxGroupExecution = budget,
            BeforeCommitForTest = label => { if (label == "test/doomed") throw new IOException("Test-only failure at commit."); },
        };
        Assert.Equal(1, await writer.ExecuteAsync<int>(new Plan("setup", _executed))); // the table exists
        using var hold = new ManualResetEventSlim();
        var blocker = writer.ExecuteAsync<int>(new Plan("block", _executed, () =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(budget + TimeSpan.FromMilliseconds(50));
        }));
        await Task.Delay(50); // the blocker's group is open; the rest queue behind it
        var before = Enumerable.Range(0, 10).Select(i => writer.ExecuteAsync<int>(new Plan("a" + i, _executed))).ToList();
        var partial = writer.ExecuteAsync<int>(new PartialPlan("partial", _executed));
        var doomed = writer.ExecuteAsync<int>(new Plan("doomed", _executed));
        var after = Enumerable.Range(0, 10).Select(i => writer.ExecuteAsync<int>(new Plan("b" + i, _executed))).ToList();
        hold.Set();
        Assert.All(await Task.WhenAll(before.Concat(after).Append(blocker)), result => Assert.Equal(1, result));
        await Assert.ThrowsAsync<InvalidOperationException>(() => partial);
        await Assert.ThrowsAsync<IOException>(() => doomed);

        Assert.Equal("setup,block," + string.Join(",", Enumerable.Range(0, 10).Select(i => "a" + i)) + "," +
            string.Join(",", Enumerable.Range(0, 10).Select(i => "b" + i)), Rows());
        var snapshot = JsonSerializer.SerializeToElement(metrics.Snapshot());
        long commits = snapshot.GetProperty("writerCommits").GetInt64();
        Assert.Equal(22, snapshot.GetProperty("writerCommittedJobs").GetInt64()); // setup, block, a0-9, b0-9
        Assert.InRange(commits, 3, 4); // setup, the slow blocker alone (time cap), then the backlog in one or two groups
    }

    [Fact]
    public async Task ACompletionIsReleasedOnlyAfterItsGroupCommitted()
    {
        var metrics = new DbAccessMetrics();
        using var hold = new ManualResetEventSlim();
        using var writer = new DbWriter($"Data Source={Database}", metrics)
        {
            // The last job of the group holds the group's commit: an earlier job must not complete before it.
            BeforeCommitForTest = label => { if (label == "test/last") hold.Wait(TimeSpan.FromSeconds(10)); },
        };
        Assert.Equal(1, await writer.ExecuteAsync<int>(new Plan("setup", _executed)));
        using var gate = new ManualResetEventSlim();
        var opener = writer.ExecuteAsync<int>(new Plan("opener", _executed, () => gate.Wait(TimeSpan.FromSeconds(10))));
        await Task.Delay(50);
        var early = writer.ExecuteAsync<int>(new Plan("early", _executed));
        var last = writer.ExecuteAsync<int>(new Plan("last", _executed));
        gate.Set();
        await Task.Delay(150);
        Assert.False(early.IsCompleted); // executed, not yet durable: its group waits on "last"
        hold.Set();
        Assert.Equal(1, await early);
        Assert.Equal(1, await last);
        Assert.Equal(1, await opener);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
