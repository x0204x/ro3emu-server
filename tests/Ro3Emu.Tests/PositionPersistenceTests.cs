using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Game.Persistence;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>C/D 18: lazy public position - no loop write per walk, bounded window, never older than a barrier.</summary>
public sealed class PositionPersistenceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pos-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
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

    private async Task<(BotClient Bot, GameSession Session)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        await Eventually(() => !session.SceneLoading);
        return (bot, session);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private float StoredX(long role) => Service<RoleStore>().Get(role)!.X;
    private long QueuedWriterJobs()
        => JsonSerializer.SerializeToElement(Service<AccountStore>().AccessMetrics.Snapshot()).GetProperty("writerJobs").GetInt64();

    [Fact]
    public async Task AStopIsWrittenLazilyWithoutAnyLoopBlockingWrite()
    {
        var (bot, s) = await InWorld("Stopper");
        await using var _ = bot;
        var start = await OnLoop(() => s.Position.Clone());
        int loopPositionWrites = 0;
        Service<AccountStore>().WriteSyncObserved = (label, onLoop) =>
        { if (onLoop && label == "role/Position") Interlocked.Increment(ref loopPositionWrites); };
        float x = start.X + 0.5f;
        await bot.WalkAsync(x, start.Y, start.Z, stop: true);
        await Eventually(() => Math.Abs(StoredX(s.Role!.Id) - x) < 0.01f);
        Service<AccountStore>().WriteSyncObserved = null;
        Assert.Equal(0, loopPositionWrites); // the walk's position write never blocked the loop
    }

    [Fact]
    public async Task SustainedWalkingIsWrittenWithinTheWindowFromTheFirstChange()
    {
        var (bot, s) = await InWorld("Walker");
        await using var _ = bot;
        var positions = Service<PositionPersistence>();
        positions.Interval = TimeSpan.FromMilliseconds(400);
        var start = await OnLoop(() => s.Position.Clone());
        float stored = StoredX(s.Role!.Id);
        DateTime? firstChange = null, persistedAt = null;
        // Keep walking (never a stop) for longer than the window: continuous updates must not postpone the write.
        for (int i = 1; i <= 60 && persistedAt == null; i++)
        {
            await bot.WalkAsync(start.X + i * 0.05f, start.Y, start.Z);
            await Task.Delay(50);
            if (firstChange == null && await OnLoop(() => s.PositionPart != null)) firstChange = DateTime.UtcNow;
            if (Math.Abs(StoredX(s.Role.Id) - stored) > 0.001f) persistedAt = DateTime.UtcNow;
        }
        Assert.NotNull(firstChange);
        Assert.NotNull(persistedAt);
        var delay = persistedAt.Value - firstChange.Value;
        output.WriteLine($"first change -> durable while walking: {delay.TotalMilliseconds:0} ms (window 400 ms)");
        Assert.True(delay < TimeSpan.FromMilliseconds(1200), $"{delay.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task AnOvertakenLazyCaptureNeverOverwritesANewerBarrierPosition()
    {
        var (bot, s) = await InWorld("Barrier");
        await using var _ = bot;
        var accounts = Service<AccountStore>();
        var roles = Service<RoleStore>();
        var positions = Service<PositionPersistence>();
        var loop = Service<GameLoop>();
        long role = s.Role!.Id;
        var start = await OnLoop(() => s.Position.Clone());
        float lazyX = start.X + 0.5f, barrierX = start.X + 1.0f;

        // Hold the lazy capture between its revision capture and its submission to the writer (no writer or loop
        // blocking), commit a newer barrier position meanwhile, then let the stale capture reach the writer.
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLazy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeLazyWriteForTest = async label =>
        {
            if (label != "role/Position") return;
            captured.TrySetResult();
            await releaseLazy.Task;
        };
        try
        {
            await loop.InvokeAsync(() =>
            {
                s.Position = new Romsg.Vec3 { X = lazyX, Y = start.Y, Z = start.Z };
                positions.MarkDirty(s, urgent: true);
                return Task.CompletedTask;
            });
            await captured.Task.WaitAsync(Wait);
            await loop.InvokeAsync(() =>
            {
                s.Position = new Romsg.Vec3 { X = barrierX, Y = start.Y, Z = start.Z };
                roles.SavePosition(role, s.SceneEntry, barrierX, start.Y, start.Z, s.Face);
                positions.Persisted(s);
                return Task.CompletedTask;
            });
            releaseLazy.TrySetResult();
            await Eventually(() => s.PositionPart is { InFlight: false });
        }
        finally { releaseLazy.TrySetResult(); accounts.BeforeLazyWriteForTest = null; }
        Assert.Equal(barrierX, StoredX(role), 3);
        var stats = JsonSerializer.SerializeToElement(positions.Snapshot());
        Assert.True(stats.GetProperty("overtaken").GetInt64() >= 1);
    }

    [Fact]
    public async Task HandoverPositionBarrierRetriesWhenLazyWriteCommitsAfterFreshCapture()
    {
        var (bot, s) = await InWorld("LateLazy");
        // SceneLoaded still has writer-backed work after its self end-frame. Drain
        // that handler before holding the writer and waiting for disconnect cleanup.
        await bot.SendGameAsync(CharacterService.ItemUse,
            new Romsg.ItemUseReq { ItemGuid = long.MaxValue, ItemCount = 1 });
        var ready = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemUse), Wait);
        Assert.Equal(ErrorCodes.ItemMissing, Romsg.ItemUseResp.Parser.ParseFrom(ready.Body).Result);
        var accounts = Service<AccountStore>();
        var roles = Service<RoleStore>();
        var positions = Service<PositionPersistence>();
        var loop = Service<GameLoop>();
        var owner = s.Owner!;
        long role = s.Role!.Id;
        var start = await OnLoop(() => s.Position.Clone());
        float lazyX = start.X + 0.5f, finalX = start.X + 1.0f;
        var lazyAtCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierObserved = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLazy = new ManualResetEventSlim();
        int heldLazy = 0, barrierAttempts = 0;
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "role/Position" || Interlocked.Exchange(ref heldLazy, 1) != 0) return;
            lazyAtCommit.TrySetResult();
            if (!releaseLazy.Wait(Wait)) throw new TimeoutException("lazy position writer was not released");
        };
        try
        {
            await loop.InvokeAsync(() =>
            {
                s.Position = new Romsg.Vec3 { X = lazyX, Y = start.Y, Z = start.Z };
                positions.MarkDirty(s, urgent: true);
                return Task.CompletedTask;
            });
            await lazyAtCommit.Task.WaitAsync(Wait);
            long initialRevision = roles.ReadRevisionVector(role).Revision("public_position");
            await loop.InvokeAsync(() =>
            {
                s.Position = new Romsg.Vec3 { X = finalX, Y = start.Y, Z = start.Z };
                positions.MarkDirty(s, urgent: true);
                return Task.CompletedTask;
            });
            accounts.WriteSyncPlanObserved = (plan, onLoop) =>
            {
                if (plan.Label != "role/Position") return;
                int attempt = Interlocked.Increment(ref barrierAttempts);
                if (attempt != 1) return;
                barrierObserved.TrySetResult(owner.Detaching ?? Task.FromException(
                    new InvalidOperationException("cleanup has no handover task")));
                if (!onLoop) throw new InvalidOperationException("cleanup position barrier left the loop");
                releaseLazy.Set();
                var until = DateTime.UtcNow + Wait;
                while (roles.ReadRevisionVector(role).Revision("public_position") == initialRevision)
                {
                    if (DateTime.UtcNow > until) throw new TimeoutException("lazy position commit did not advance the revision");
                    Thread.Sleep(5);
                }
            };
            await bot.DisposeAsync();
            Task detach = await barrierObserved.Task.WaitAsync(Wait);
            await detach.WaitAsync(Wait);
            Assert.Equal(2, Volatile.Read(ref barrierAttempts)); // refused capture, then one fresh retry
            Assert.Equal(RoleOwnerState.Detached, owner.State);
            Assert.Equal(finalX, StoredX(role), 3);

            await using var again = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
            await again.LoginAsync("latelazy", "pw");
            await again.EnterGateAsync();
            var self = await again.EnterWorldAsync(role);
            Assert.Equal(finalX, self.MovementInfo.Pos.X, 3);
        }
        finally
        {
            releaseLazy.Set();
            accounts.BeforeCommitForTest = null;
            accounts.WriteSyncPlanObserved = null;
            await bot.DisposeAsync();
        }
    }

    [Fact]
    public async Task FreshPositionRetryCannotCrossDeletedRoleEpoch()
    {
        var (bot, _) = await InWorld("EpochOwner");
        await using var dispose = bot;
        var accounts = Service<AccountStore>();
        var roles = Service<RoleStore>();
        var offline = roles.Create("epochowner", 1, "EpochOffline", 1, 1, 0)!;
        var before = roles.ReadRevisionVector(offline.Id);
        float originalX = roles.Get(offline.Id)!.X;
        int attempts = 0;
        accounts.WriteSyncPlanObserved = (plan, onLoop) =>
        {
            if (plan.Label != "role/Position") return;
            if (Interlocked.Increment(ref attempts) != 1) return;
            Assert.False(onLoop);
            Assert.True(roles.Delete("epochowner", offline.Id));
        };
        try
        {
            Assert.Throws<OwnerRevisionConflictException>(() =>
                roles.SavePosition(offline.Id, RoleStore.SpawnSceneEntry, originalX + 1, 1.5f, 200f, 0));
        }
        finally { accounts.WriteSyncPlanObserved = null; }
        Assert.Equal(1, attempts);
        var after = roles.ReadRevisionVector(offline.Id);
        Assert.True(after.Deleted);
        Assert.Equal(before.Epoch + 1, after.Epoch);
        Assert.Equal(before.Revision("public_position"), after.Revision("public_position"));
    }

    [Fact]
    public async Task FreshMixedEntityConflictDoesNotOverwriteNewerFriendGroupName()
    {
        var (bot, s) = await InWorld("GroupOwner");
        await using var dispose = bot;
        var accounts = Service<AccountStore>();
        var friends = Service<FriendStore>();
        var group = friends.CreateGroup(s.Role!.Id, "original", [s.Role.Id]);
        int attempts = 0;
        accounts.WriteSyncPlanObserved = (plan, onLoop) =>
        {
            if (plan.Label != "friend/RenameGroup") return;
            if (Interlocked.Increment(ref attempts) != 1) return;
            Assert.False(onLoop);
            Assert.NotEmpty(plan.RolePins);
            Assert.NotEmpty(plan.EntityWrites);
            friends.RenameGroup(group.Id, "newer");
        };
        try
        {
            Assert.Throws<OwnerRevisionConflictException>(() => friends.RenameGroup(group.Id, "stale"));
        }
        finally { accounts.WriteSyncPlanObserved = null; }
        Assert.Equal(2, attempts); // outer and nested writes; no retry of the stale outer plan
        Assert.Equal("newer", friends.Group(group.Id)!.Name);
    }
}
