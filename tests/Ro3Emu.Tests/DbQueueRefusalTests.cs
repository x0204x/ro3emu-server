using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>
/// A plan the writer did not admit (queue full) wrote nothing, so an economic commit refused at admission fails
/// definitely: it never goes down the unknown-outcome path that reads the receipt back and quarantines the owners.
/// </summary>
public sealed class DbQueueRefusalTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly long _role;

    public DbQueueRefusalTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "db-refusal-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _role = _roles.Create("refusal-account", 1, "Ref" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private RoleItemPlanBuilder Kill(string operation, long exp)
    {
        var mutation = new EconomyMutation(operation, "refusal-account", _role, "monster-kill", "kill/roll/empty",
            "monster:" + operation, 1_000_000, [], [], Encoding.UTF8.GetBytes("{\"drops\":[],\"exp\":" + exp + "}"),
            new EconomyRewardEvidence(1, exp - 10, 1, exp, 1, 0, 1, 0, 10, 0));
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        var progress = _roles.LoadProgress(_role);
        progress.Exp = exp;
        tx.Progress(_role, progress);
        tx.WithEconomy(mutation);
        return tx;
    }

    [Fact]
    public async Task AnEconomicCommitTheWriterRefusesFailsDefinitelyAndTheRetrySucceeds()
    {
        string operation = Guid.NewGuid().ToString("N");
        long before = _roles.LoadProgress(_role).Exp;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = _accounts.WriteAsync<int>(new HoldPlan(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var fill = _accounts.WriteAsync<int>(new SizedPlan(DbWriter.MaxDurableBytes - HoldPlan.Size));
            // Were the receipt read back, it would fail and the outcome would be unknown.
            _accounts.FailNextEconomyResolutionForTest();
            Assert.Throws<DbQueueFullException>(() => _roles.Commit(Kill(operation, 10), "test/refused-kill"));
            Assert.True(_accounts.ConsumeEconomyResolutionFaultForTest()); // no resolution was attempted
            release.Set();
            await Task.WhenAll(hold, fill);
        }
        finally { release.Set(); }

        Assert.Equal(before, _roles.LoadProgress(_role).Exp);
        _roles.Commit(Kill(operation, 10), "test/refused-kill-retry");
        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
    }

    [Fact]
    public void RefusalsAndConflictsArePersistenceErrorsNotProgramErrors()
    {
        Assert.False(typeof(InvalidOperationException).IsAssignableFrom(typeof(DbQueueFullException)));
        Assert.False(typeof(InvalidOperationException).IsAssignableFrom(typeof(OwnerRevisionConflictException)));
        Assert.True(typeof(PersistenceException).IsAssignableFrom(typeof(DbQueueFullException)));
        Assert.True(typeof(PersistenceException).IsAssignableFrom(typeof(OwnerRevisionConflictException)));
    }

    private sealed class HoldPlan(ManualResetEventSlim entered, ManualResetEventSlim release) : DbPlan
    {
        internal const int Size = 64;
        internal override int Bytes => Size;
        internal override string Label => "test/hold";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Writer test handshake timed out.");
            return 1;
        }
    }

    private sealed class SizedPlan(int bytes) : DbPlan
    {
        internal override int Bytes => bytes;
        internal override string Label => "test/sized";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => bytes;
    }
}
