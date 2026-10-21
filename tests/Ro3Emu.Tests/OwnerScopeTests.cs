using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class OwnerScopeTests : IAsyncLifetime
{
    private sealed class LoopErrors : ILogger<GameLoop>
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Error) Exceptions.Enqueue(exception ?? new InvalidOperationException(formatter(state, exception)));
        }
    }

    private readonly LoopErrors _errors = new();
    private readonly GameLoop _loop;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-owners-" + Guid.NewGuid().ToString("N"));
    private AccountStore _accounts = null!;
    private RoleStore _roles = null!;
    private OwnerRegistry _owners = null!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OwnerScopeTests() => _loop = new(_errors);

    public Task InitializeAsync()
    {
        _accounts = new(Options.Create(new ServerOptions { DataDir = _directory }), loop: _loop);
        _roles = new(_accounts);
        _owners = new(_loop, _roles);
        return _loop.StartAsync(default);
    }
    public async Task DisposeAsync()
    {
        await _loop.StopAsync(default); _loop.Dispose(); _accounts.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("timer")]
    [InlineData("GM")]
    [InlineData("micro")]
    public async Task ConflictingIntentWaitsWithoutReservingDisjointRole(string origin)
    {
        var entered = Signal(); var release = Signal(); var attempted = Signal(); var finished = Signal();
        var held = _loop.InvokeAsync(() => _owners.RunAsync("dispatcher", [ReservationKey.Role(101)], async () =>
        { entered.SetResult(); await release.Task; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var contender = _loop.InvokeAsync(async () =>
        {
            attempted.SetResult();
            await _owners.RunAsync(origin, [ReservationKey.Role(101), ReservationKey.Role(102)], () =>
            { finished.SetResult(); return Task.CompletedTask; });
        });
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await _loop.InvokeAsync(() => _owners.RunAsync("unrelated progress", [ReservationKey.Role(102)], () =>
            { Assert.False(finished.Task.IsCompleted); return Task.CompletedTask; })).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(contender.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(held, contender).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished.Task.IsCompletedSuccessfully);
        Assert.True(_owners.Reservations.Conflicts > 0);
    }

    [Fact]
    public async Task DisjointPartsProceedButLifecycleOverlapsBoth()
    {
        var entered = Signal(); var release = Signal();
        var held = _loop.InvokeAsync(() => _owners.RunAsync("settings", [ReservationKey.Role(1, "settings")], async () =>
        { entered.SetResult(); await release.Task; }));
        await entered.Task;
        Task? deletion = null;
        try
        {
            await _loop.InvokeAsync(() => _owners.RunAsync("progress", [ReservationKey.Role(1, "progress")], () => Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(5));
            var attempted = Signal();
            deletion = _loop.InvokeAsync(async () =>
            { attempted.SetResult(); using var scope = await _owners.EnterAsync("delete", ReservationKey.Lifecycle(1)); });
            await attempted.Task;
            Assert.False(deletion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(held, deletion!).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PostedWorkDoesNotBorrowParentCapabilityAndNestedAwaitDoes()
    {
        var childEntered = Signal(); var release = Signal(); var parentEntered = Signal();
        var parent = _loop.InvokeAsync(() => _owners.RunAsync("parent", [ReservationKey.Role(1)], async () =>
        {
            var context = _owners.Current!;
            await _loop.TrackAsync("nested helper", () => { context.AssertRole(1); return Task.CompletedTask; });
            await Task.Run(() => Assert.Throws<InvalidOperationException>(() => context.AssertRole(1)));
            _loop.Post(async () =>
            {
                Assert.Throws<InvalidOperationException>(() => context.AssertRole(1));
                await _owners.RunAsync("new intent", [ReservationKey.Role(1)], () => { childEntered.SetResult(); return Task.CompletedTask; });
            });
            parentEntered.SetResult(); await release.Task;
        }));
        await parentEntered.Task;
        try { await _loop.InvokeAsync(() => { Assert.False(childEntered.Task.IsCompleted); return Task.CompletedTask; }); }
        finally { release.TrySetResult(); }
        await parent; await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitingSessionIntentCannotRunAgainstAnotherRole()
    {
        var first = _roles.Create("owner", 1, "OwnerBefore", 1, 1000, 1)!;
        var second = _roles.Create("owner", 1, "OwnerAfter", 1, 1000, 1)!;
        var session = new GameSession(null!, null!) { Role = first, Owners = _owners };
        var entered = Signal(); var release = Signal(); var blocked = Signal();
        await _loop.InvokeAsync(() =>
        { session.Owner = _owners.Get(first.Id); _owners.DeferredForTest = origin => { if (origin == "old intent") blocked.TrySetResult(); }; return Task.CompletedTask; });
        var switching = _loop.InvokeAsync(() => _owners.RunAsync("switch", [ReservationKey.Realm], async () =>
        {
            entered.SetResult(); await release.Task;
            session.Role = second; session.Owner = _owners.Get(second.Id);
        }));
        await entered.Task;
        bool ran = false;
        var old = _loop.InvokeAsync(() => _owners.RunSessionAsync("old intent", [ReservationKey.Role(first.Id)], session,
            () => { ran = true; return Task.CompletedTask; }));
        try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await switching;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.False(ran);
    }

    [Fact]
    public async Task ReselectingSameRoleInvalidatesAnAlreadyQueuedTimer()
    {
        var role = _roles.Create("owner", 1, "OwnerReselect", 1, 1000, 1)!;
        var session = new GameSession(null!, null!) { Account = "owner", Role = role, Owners = _owners };
        var entered = Signal(); var release = Signal(); var blocked = Signal(); var completed = Signal();
        bool ran = false, discarded = false;
        var selection = _loop.InvokeAsync(() => _owners.RunAsync("selection", [ReservationKey.Realm], async () =>
        {
            _owners.BindLegacySelection(session);
            _owners.DeferredForTest = origin => { if (origin == "timer/old attachment") blocked.TrySetResult(); };
            _loop.PostOwned(session, () => { ran = true; return Task.CompletedTask; }, "old attachment", () => discarded = true);
            entered.SetResult(); await release.Task;
            // Simulate the completed old fence; a real reselect cannot bind over a live lease.
            session.Attachment!.Cancelled = true;
            session.Owner!.Attachment = null;
            session.Owner.State = RoleOwnerState.Detached;
            session.Attachment = null;
            _owners.BindLegacySelection(session);
            _loop.Post(() => { completed.TrySetResult(); return Task.CompletedTask; });
        }));
        await entered.Task;
        try { await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await selection;
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _loop.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ran);
        Assert.True(discarded);
    }

    [Fact]
    public async Task QuiescingAfterOwnedTimerReservationWaitDiscardsBeforePinWithoutLoopError()
    {
        var role = _roles.Create("owner", 1, "DeferredTick", 1, 1000, 1)!;
        var session = new GameSession(null!, null!) { Account = "owner", Role = role, Owners = _owners };
        await _loop.InvokeAsync(() => _owners.RunAsync("bind", [ReservationKey.Realm], () =>
        {
            _owners.BindLegacySelection(session);
            session.Owner!.State = RoleOwnerState.Attached;
            return Task.CompletedTask;
        }));
        var held = Signal(); var release = Signal(); var deferred = Signal(); var discarded = Signal();
        int discardedCount = 0;
        bool ran = false;
        var blocker = _loop.InvokeAsync(() => _owners.RunAsync("held Realm", [ReservationKey.Realm], async () =>
        { held.TrySetResult(); await release.Task; }));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await _loop.InvokeAsync(() =>
            {
                _owners.DeferredForTest = origin => { if (origin == "timer/deferred tick") deferred.TrySetResult(); };
                _loop.PostOwned(session, () => { ran = true; return Task.CompletedTask; }, "deferred tick",
                    () => { discardedCount++; discarded.TrySetResult(); });
                return Task.CompletedTask;
            });
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _loop.InvokeAsync(() =>
            {
                _owners.BeginQuiescing(session.Owner!, session.Attachment!);
                session.Attachment!.Cancelled = true;
                return Task.CompletedTask;
            });
        }
        finally { release.TrySetResult(); }
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await discarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _loop.InvokeAsync(() => Task.CompletedTask);
        Assert.False(ran);
        Assert.Equal(1, discardedCount);
        Assert.Empty(_errors.Exceptions);
    }

    [Fact]
    public async Task DisjointStoragePartsCanCommitWhileSettingsScopeIsHeld()
    {
        var role = _roles.Create("owner", 1, "OwnerParts", 1, 1000, 1)!;
        var entered = Signal(); var release = Signal();
        var settings = _loop.InvokeAsync(() => _owners.RunAsync("settings", [ReservationKey.Role(role.Id, "settings")], async () =>
        {
            _roles.SaveSettings(role.Id, [1, 2]);
            entered.SetResult(); await release.Task;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await _loop.InvokeAsync(() => _owners.RunAsync("progress", [ReservationKey.Role(role.Id, "progress")], () =>
            {
                var progress = _roles.LoadProgress(role.Id); progress.Exp = 123;
                _roles.SaveProgress(role.Id, progress);
                return Task.CompletedTask;
            })).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(123, _roles.LoadProgress(role.Id).Exp);
            Assert.Equal(new byte[] { 1, 2 }, _roles.LoadSettings(role.Id));
        }
        finally { release.TrySetResult(); }
        await settings;
    }

    [Fact]
    public async Task WaitingLegacyGateDoesNotRetainOwnerOrBlockItsCurrentOwnerCompletion()
    {
        var role = _roles.Create("owner", 1, "GateOwner", 1, 1000, 1)!;
        using var gate = new SemaphoreSlim(0, 1);
        var queued = Signal(); var ownerEntered = Signal();
        var intent = _loop.InvokeAsync(async () =>
        {
            queued.SetResult();
            using var scope = await _owners.EnterWithGateAsync("waiting intent", gate, [ReservationKey.Role(role.Id)]);
            try { _roles.SaveSettings(role.Id, [2]); }
            finally { gate.Release(); }
        });
        await queued.Task;
        var completion = _loop.InvokeAsync(() => _owners.RunAsync("current owner", [ReservationKey.Role(role.Id)], async () =>
        {
            ownerEntered.SetResult();
            await gate.WaitAsync();
            try { _roles.SaveSettings(role.Id, [1]); }
            finally { gate.Release(); }
        }));
        try { await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { gate.Release(); }
        await Task.WhenAll(intent, completion).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 2 }, _roles.LoadSettings(role.Id));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task SuspendedRuntimeCallbackCannotWriteOrBlockAnExclusiveMutation()
    {
        var role = _roles.Create("owner", 1, "RuntimeOwner", 1, 1000, 1)!;
        var entered = Signal(); var release = Signal();
        var callback = _loop.InvokeAsync(() => _owners.RunRuntimeAsync("scene callback", async () =>
        {
            Assert.Throws<InvalidOperationException>(() => _roles.SaveSettings(role.Id, [1]));
            entered.SetResult();
            await release.Task;
            using var scope = await _owners.EnterAsync("durable callback section", ReservationKey.Role(role.Id));
            _roles.SaveSettings(role.Id, [3]);
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await _loop.InvokeAsync(() => _owners.RunAsync("concurrent mutation", [ReservationKey.Role(role.Id)], () =>
            {
                _roles.SaveSettings(role.Id, [2]);
                return Task.CompletedTask;
            })).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 2 }, _roles.LoadSettings(role.Id));
        }
        finally { release.TrySetResult(); }
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 3 }, _roles.LoadSettings(role.Id));
        Assert.Equal(0, _owners.Reservations.Active);
    }

    [Fact]
    public async Task CharacterLockKeepsItsSingleRoleSemaphoreAndValidatesCapability()
    {
        var role = _roles.Create("owner", 1, "OwnerLock", 1, 1000, 1)!;
        var session = new GameSession(null!, null!) { Role = role, Progress = _roles.LoadProgress(role.Id), Owners = _owners };
        await _loop.InvokeAsync(async () =>
        {
            session.Owner = _owners.Get(role.Id);
            using var scope = await _owners.EnterAsync("test", ReservationKey.Role(role.Id));
            using (await CharacterService.Lock(session, scope.Context)) Assert.Equal(0, session.StateLock.CurrentCount);
            Assert.Equal(1, session.StateLock.CurrentCount);
            Assert.Throws<InvalidOperationException>(() => scope.Context.AssertRole(role.Id + 1));
        });
    }

    [Fact]
    public async Task RealmAdmissionsAreReportedByOriginWithQueuedAndHeldTime()
    {
        var entered = Signal(); var release = Signal();
        var holder = _loop.InvokeAsync(() => _owners.RunAsync("role work", [ReservationKey.Role(101)], async () =>
        { entered.SetResult(); await release.Task; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var realm = _loop.InvokeAsync(() => _owners.RunAsync("realm work", [ReservationKey.Realm], () => Task.Delay(30)));
        await Task.Delay(60); // Realm waits for the role reservation
        release.SetResult();
        await Task.WhenAll(holder, realm).WaitAsync(TimeSpan.FromSeconds(5));

        var report = System.Text.Json.JsonSerializer.SerializeToNode(await _loop.InvokeAsync(() => Task.FromResult(_owners.Snapshot())))!;
        var byOrigin = report["realmByOrigin"]!.AsObject();
        Assert.False(byOrigin.ContainsKey("role work"));
        var use = byOrigin["realm work"]!;
        Assert.Equal(1, use["count"]!.GetValue<long>());
        Assert.True(use["waitMs"]!.GetValue<double>() >= 40, use.ToJsonString());
        Assert.True(use["heldMs"]!.GetValue<double>() >= 25, use.ToJsonString());
        Assert.Equal(1, report["realmScopes"]!.GetValue<long>());
    }

    [Fact]
    public void SharedEntityHoldsCoexistExcludeWritersAndAreCoveredByAnExclusiveHold()
    {
        var reservations = new ReservationCoordinator();
        var shared = ReservationKey.SharedEntity("territory_season", "s1");
        var exclusive = ReservationKey.Entity("territory_season", "s1");
        Assert.True(reservations.TryReserve(1, [shared]));
        Assert.True(reservations.TryReserve(2, [shared]));        // readers coexist
        Assert.False(reservations.TryReserve(3, [exclusive]));    // a writer waits for readers
        Assert.True(reservations.TryReserve(4, [ReservationKey.SharedEntity("territory_season", "s2")])); // another entity
        reservations.Release(1); reservations.Release(2);
        Assert.True(reservations.TryReserve(3, [exclusive]));
        Assert.False(reservations.TryReserve(5, [shared]));       // a reader waits for the writer
        Assert.True(reservations.Covers(3, [shared]));            // nested shared under the writer borrows
        reservations.Release(3);
        Assert.True(reservations.TryReserve(5, [shared]));
        Assert.False(reservations.Covers(5, [exclusive]));        // a reader never authorizes a write
    }

    private sealed class BlockingLogin(TaskCompletionSource firstEntered, Task release) : Ro3Emu.Server.Micro.IMicroService
    {
        private int _calls;
        public string Name => "LoginService";
        public async ValueTask<object?> InvokeAsync(Ro3Emu.Server.Micro.MicroSession session, string method, object? args)
        {
            if (Interlocked.Increment(ref _calls) == 1) { firstEntered.SetResult(); await release; }
            return Ro3Emu.Server.Micro.MicroServiceRouter.Fail("test");
        }
    }

    [Fact]
    public async Task UnverifiedMicroLoginsDoNotWaitForEachOther()
    {
        // S4 on 10.10: every unverified session shared one reservation key, so first logins queued behind each
        // other's token resolution. Each unverified session now reserves only itself.
        var entered = Signal(); var release = Signal();
        var router = new Ro3Emu.Server.Micro.MicroServiceRouter([new BlockingLogin(entered, release.Task)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ro3Emu.Server.Micro.MicroServiceRouter>.Instance, _loop, _owners);
        var first = router.InvokeAsync(new Ro3Emu.Server.Micro.MicroSession(null!, 1), "LoginService", "verifySdk", default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await router.InvokeAsync(new Ro3Emu.Server.Micro.MicroSession(null!, 2), "LoginService", "verifySdk", default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AWriterBlockedOnlyByReadersQueuesNewReadersBehindIt()
    {
        // Audit 11.10: overlapping shared holds (logins on the season, activity requests on the Eve row) could keep an
        // entity's writer out for good. A writer blocked only by readers marks the entity; new readers then wait.
        var reservations = new ReservationCoordinator();
        var shared = ReservationKey.SharedEntity("territory_season", "s1");
        var exclusive = ReservationKey.Entity("territory_season", "s1");
        Assert.True(reservations.TryReserve(1, [shared]));
        Assert.False(reservations.TryReserve(2, [exclusive, ReservationKey.Role(7)]));
        Assert.False(reservations.TryReserve(3, [shared]));          // a new reader queues behind the waiting writer
        Assert.True(reservations.TryReserve(4, [ReservationKey.Role(8)])); // unrelated work is not held back
        reservations.Release(1);
        Assert.True(reservations.TryReserve(2, [exclusive, ReservationKey.Role(7)]));
        Assert.False(reservations.TryReserve(3, [shared]));          // now blocked by the writer's own hold
        reservations.Release(2);
        Assert.True(reservations.TryReserve(3, [shared]));
    }

    [Fact]
    public async Task AWriterBlockedByAnythingElseOrGivingUpDoesNotHoldReadersBack()
    {
        var reservations = new ReservationCoordinator();
        var shared = ReservationKey.SharedEntity("activity_eve_global", "117:1");
        var exclusive = ReservationKey.Entity("activity_eve_global", "117:1");
        Assert.True(reservations.TryReserve(1, [shared]));
        Assert.True(reservations.TryReserve(2, [ReservationKey.Role(5)]));
        // Blocked by a reader and by a role hold: no mark, readers keep flowing.
        Assert.False(reservations.TryReserve(3, [exclusive, ReservationKey.Role(5)]));
        Assert.Equal(0, reservations.WaitingWriters);
        Assert.True(reservations.TryReserve(4, [shared]));
        // Blocked only by readers: marked; a reader waits until the writer gives up, which wakes it.
        Assert.False(reservations.TryReserve(6, [exclusive]));
        Assert.Equal(1, reservations.WaitingWriters);
        Assert.False(reservations.TryReserve(7, [shared]));
        var woken = reservations.Changed;
        reservations.Abandon(6);
        await woken.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(reservations.TryReserve(7, [shared]));
    }

    [Fact]
    public void ReleasingAnOperationWithoutKeysWakesNoWaiter()
    {
        var reservations = new ReservationCoordinator();
        Assert.True(reservations.TryReserve(1, []));
        Assert.True(reservations.TryReserve(2, [ReservationKey.Role(1)]));
        var changed = reservations.Changed;
        reservations.Release(1);
        Assert.False(changed.IsCompleted);
        reservations.Release(2);
        Assert.True(changed.IsCompleted);
    }

    [Fact]
    public async Task AScopeDisposedOutOfOrderStillReleasesItsKeys()
    {
        // Two flows on one permit: the second is admitted while the first waits, then the first is admitted on top.
        // Disposing the second is then out of order; it used to throw before releasing, leaking its keys.
        var entered = Signal(); var release = Signal();
        var blocker = _loop.InvokeAsync(() => _owners.RunAsync("blocker", [ReservationKey.Role(301)], async () =>
        { entered.SetResult(); await release.Task; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _loop.InvokeAsync(async () =>
        {
            var first = _owners.EnterAsync("first", ReservationKey.Role(301));
            var second = await _owners.EnterAsync("second", ReservationKey.Role(302));
            release.SetResult();
            var admitted = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(second.Dispose);
            Assert.True(_owners.Reservations.TryReserve(999, [ReservationKey.Role(302)])); // released anyway
            _owners.Reservations.Release(999);
            admitted.Dispose();
        });
        await blocker;
        Assert.Equal(0, _owners.Reservations.Active);
    }
}
