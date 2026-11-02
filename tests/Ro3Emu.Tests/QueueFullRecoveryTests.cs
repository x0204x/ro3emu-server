using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// A database queue that is full for a few seconds (a disk stall) refuses work without writing anything, so it must
/// not lock anybody out: the handover waits and retries, and a read that could not run asks again instead of ending
/// in an unknown outcome. "Not read" still never counts as "not committed".
/// </summary>
public sealed class QueueFullRecoveryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>Takes a writer job first, so the bytes after it fill the durable queue until it is released.</summary>
    private sealed class HoldWrite(ManualResetEventSlim entered, ManualResetEventSlim release) : DbPlan
    {
        internal const int Size = 64;
        internal override int Bytes => Size;
        internal override string Label => "test/hold";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            entered.Set();
            if (!release.Wait(Wait)) throw new TimeoutException("Writer test handshake timed out.");
            return 1;
        }
    }

    private sealed class SizedWrite(int bytes) : DbPlan
    {
        internal override int Bytes => bytes;
        internal override string Label => "test/sized";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => bytes;
    }

    private sealed class HoldRead(ManualResetEventSlim release, CountdownEvent? entered = null) : DbReadPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/hold-read";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            entered?.Signal();
            if (!release.Wait(Wait)) throw new TimeoutException("Reader test handshake timed out.");
            return 1;
        }
    }

    /// <summary>Takes every byte the durable queue has left; other writes of the running server may share it.</summary>
    private static void FillWriter(AccountStore accounts, List<Task> fill)
    {
        for (int size = 1024 * 1024; size > 0; size /= 2)
        {
            while (true)
            {
                var write = accounts.WriteAsync<int>(new SizedWrite(size));
                if (write.IsFaulted) { Assert.IsType<DbQueueFullException>(write.Exception!.InnerException); break; }
                fill.Add(write);
            }
        }
    }

    /// <summary>Both read workers held and every waiting slot taken, until <paramref name="release"/> is set.</summary>
    private static Task[] FillReaders(AccountStore accounts, ManualResetEventSlim release)
    {
        using var entered = new CountdownEvent(DbReadPool.WorkerCount);
        var held = Enumerable.Range(0, DbReadPool.WorkerCount)
            .Select(_ => (Task)accounts.ReadAsync<int>(new HoldRead(release, entered))).ToList();
        Assert.True(entered.Wait(Wait));
        held.AddRange(Enumerable.Range(0, DbReadPool.MaxWaitingJobs).Select(_ => (Task)accounts.ReadAsync<int>(new HoldRead(release))));
        return [.. held];
    }

    private static async Task<RoleOwnerState> StateAfterDisconnect(SyntheticHost host, BotClient bot, long role)
    {
        var owners = host.Service<OwnerRegistry>();
        var loop = host.Service<GameLoop>();
        await bot.DisposeAsync();
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var state = await loop.InvokeAsync(() => Task.FromResult(owners.Get(role).State));
            if (state is RoleOwnerState.Detached or RoleOwnerState.RecoveryRequired) return state;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Role {role} stayed in {state}");
            await Task.Delay(25);
        }
    }

    /// <summary>The cleanup met a full writer queue: it waits for room, the role detaches, and its owner logs in again.</summary>
    [Fact]
    public async Task ADisconnectDuringAFullWriterQueueStillDetachesAndTheRoleLogsInAgain()
    {
        await using var host = await SyntheticHost.StartAsync();
        var first = await host.InWorldAsync("QueueFullWriter");
        long role = first.Roles.Single().Id;
        var accounts = host.Service<AccountStore>();
        var refused = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The cleanup's first step meets the full queue for real, whatever the role had to save; the later steps meet it
        // on their own while it stays full.
        host.Service<RoleSessionService>().CleanupFailureForTest = (_, _) =>
        {
            if (refused.Task.IsCompleted) return null;
            var refusal = accounts.WriteAsync<int>(new SizedWrite(1)).Exception?.InnerException;
            refused.TrySetResult(refusal);
            return refusal;
        };
        // The login's own writes finish first: an operation still queued behind the hold would keep the handover waiting.
        await Task.Delay(1000);
        await accounts.WriteAsync<int>(new SizedWrite(1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = accounts.WriteAsync<int>(new HoldWrite(entered, release));
        Assert.True(entered.Wait(Wait));
        var fill = new List<Task>();
        try
        {
            FillWriter(accounts, fill);
            var state = StateAfterDisconnect(host, first, role);
            await refused.Task.WaitAsync(Wait);
            Assert.IsType<DbQueueFullException>(await refused.Task);
            await Task.Delay(300); // the queue stays full for a while, as in a disk stall
            release.Set();
            Assert.Equal(RoleOwnerState.Detached, await state);
        }
        finally
        {
            release.Set();
            await hold;
            await Task.WhenAll(fill);
        }
        host.Service<RoleSessionService>().CleanupFailureForTest = null;
        await using var again = await host.InWorldAsync("QueueFullWriter", create: false);
        Assert.Equal(role, again.Roles.Single().Id);
    }

    /// <summary>The revision fence and the cleanup's reads met a full reader queue: the handover waits for them too.</summary>
    [Fact]
    public async Task ADisconnectDuringAFullReaderQueueStillDetaches()
    {
        await using var host = await SyntheticHost.StartAsync();
        var first = await host.InWorldAsync("QueueFullReader");
        long role = first.Roles.Single().Id;
        var accounts = host.Service<AccountStore>();
        var handover = host.Service<RoleHandover>();
        using var release = new ManualResetEventSlim();
        Task[] readers = [];
        // Filled right before the fence so the loop is not stuck behind a full reader for the whole login teardown.
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handover.PhaseForTest = (phase, _) =>
        {
            if (phase != "BeforeFence" || filled.Task.IsCompleted) return;
            readers = FillReaders(accounts, release);
            filled.TrySetResult();
        };
        var state = StateAfterDisconnect(host, first, role);
        await filled.Task.WaitAsync(Wait);
        await Task.Delay(200);
        release.Set();
        Assert.Equal(RoleOwnerState.Detached, await state);
        await Task.WhenAll(readers);
        handover.PhaseForTest = null;
        await using var again = await host.InWorldAsync("QueueFullReader", create: false);
    }

    private static (AccountStore Accounts, RoleStore Roles, string Directory) Store()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ro3emu-queuefull-" + Guid.NewGuid().ToString("N"));
        var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        return (accounts, new RoleStore(accounts), directory);
    }

    private static void Clean(AccountStore accounts, string directory)
    {
        accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    /// <summary>A kill or a gather asks whether its source committed; a full reader queue only delays the answer.</summary>
    [Fact]
    public async Task AFullReaderQueueDelaysTheSourceQuestionInsteadOfFailingIt()
    {
        var (accounts, roles, directory) = Store();
        using var release = new ManualResetEventSlim();
        Task[] readers = [];
        try
        {
            readers = FillReaders(accounts, release);
            var answer = roles.RewardSourceCommittedAsync("queue-full/none");
            await Task.Delay(150);
            Assert.False(answer.IsCompleted);
            release.Set();
            Assert.False(await answer.WaitAsync(Wait));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(readers);
            Clean(accounts, directory);
        }
    }

    /// <summary>When the queue stays full the question fails: "not read" never reads as "not committed".</summary>
    [Fact]
    public async Task ASourceQuestionThatNeverGetsReadFailsInsteadOfAnsweringNo()
    {
        var (accounts, roles, directory) = Store();
        roles.RewardSourcePatience = TimeSpan.FromMilliseconds(100);
        using var release = new ManualResetEventSlim();
        Task[] readers = [];
        try
        {
            readers = FillReaders(accounts, release);
            await Assert.ThrowsAsync<DbQueueFullException>(() => roles.RewardSourceCommittedAsync("queue-full/none"));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(readers);
            Clean(accounts, directory);
        }
    }

    /// <summary>Runs <paramref name="refused"/> against a full writer queue; what it throws must be the queue's refusal.</summary>
    private static async Task AgainstAFullWriter(SyntheticHost host, Func<Task> refused)
    {
        var accounts = host.Service<AccountStore>();
        // The login's own writes finish first.
        await Task.Delay(1000);
        await accounts.WriteAsync<int>(new SizedWrite(1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = accounts.WriteAsync<int>(new HoldWrite(entered, release));
        Assert.True(entered.Wait(Wait));
        var fill = new List<Task>();
        try
        {
            FillWriter(accounts, fill);
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => host.Service<GameLoop>().InvokeAsync(async () => { await refused(); return 0; }));
            Assert.True(QueueFullPatience.Refused(thrown), thrown.ToString());
        }
        finally
        {
            release.Set();
            await hold;
            await Task.WhenAll(fill);
        }
    }

    /// <summary>The final AFK save was refused: the state stays, so the retried step still has it to save.</summary>
    [Fact]
    public async Task ARefusedAfkSaveIsNotLost()
    {
        await using var host = await SyntheticHost.StartAsync();
        var bot = await host.InWorldAsync("QueueFullAfk");
        long role = bot.Roles.Single().Id;
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        var afk = host.Service<WildAfkService>();
        var loop = host.Service<GameLoop>();
        var state = await loop.InvokeAsync(() => Task.FromResult(session.WildAfk!));

        // Changed just here: the service's own periodic save would write it before the queue is full.
        await AgainstAFullWriter(host, () =>
        {
            state.Info.HighMultiplierTimeSeconds = 123;
            return afk.LeaveAsync(session);
        });
        Assert.Same(state, await loop.InvokeAsync(() => Task.FromResult(session.WildAfk)));

        await loop.InvokeAsync(async () => { await afk.LeaveAsync(session); return 0; });
        Assert.Null(await loop.InvokeAsync(() => Task.FromResult(session.WildAfk)));
        var saved = System.Text.Json.JsonSerializer.Deserialize<WildAfkService.State>(host.Service<RoleStore>().LoadWildAfk(role)!)!;
        Assert.Equal(123, saved.Info.HighMultiplierTimeSeconds);
    }

    /// <summary>The tavern's stay was refused: its seconds go back, so the retried step still has them to write.</summary>
    [Fact]
    public async Task ARefusedTavernStayIsNotLost()
    {
        await using var host = await SyntheticHost.StartAsync();
        var bot = await host.InWorldAsync("QueueFullStay");
        long role = bot.Roles.Single().Id;
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        var tavern = host.Service<TavernService>();
        var loop = host.Service<GameLoop>();
        long before = await loop.InvokeAsync(() => Task.FromResult((long)session.Tavern!.TodaySeconds));
        tavern.AddStayForTest(role, 7);

        async Task Leave()
        {
            using var scope = await host.Service<OwnerRegistry>().EnterAsync("test/tavern leave", ReservationKey.Role(role));
            scope.Context.Pin(session.Owner!);
            await tavern.LeaveRoleAsync(session);
        }
        await AgainstAFullWriter(host, Leave);

        await loop.InvokeAsync(async () => { await Leave(); return 0; });
        Assert.Equal(before + 7, await loop.InvokeAsync(() => Task.FromResult((long)session.Tavern!.TodaySeconds)));
    }
}
