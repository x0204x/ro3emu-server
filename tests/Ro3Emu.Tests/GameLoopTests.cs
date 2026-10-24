using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Net;
using System.Net;
using System.Net.Sockets;

namespace Ro3Emu.Tests;

/// <summary>The single game-logic thread: ordering, await continuations and ticks.</summary>
public sealed class GameLoopTests : IAsyncLifetime
{
    private readonly GameLoop _loop = new(NullLogger<GameLoop>.Instance);

    public Task InitializeAsync() => _loop.StartAsync(default);

    public async Task DisposeAsync()
    {
        await _loop.StopAsync(default);
        _loop.Dispose();
    }

    [Fact]
    public async Task WorkRunsInOrderOnTheLoopThread()
    {
        var seen = new List<int>();
        var tasks = Enumerable.Range(0, 100).Select(i => _loop.InvokeAsync(() =>
        {
            Assert.True(_loop.IsOnLoop);
            seen.Add(i);
            return Task.CompletedTask;
        })).ToList();
        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 100), seen);
    }

    [Fact]
    public async Task LateUntrackedContinuationRunsOnTheLoopInsteadOfThrowing()
    {
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Late()
        {
            await release.Task;
            resumed.TrySetResult(_loop.IsOnLoop);
        }
        // The dropped task outlives the invocation's permit; its continuation used to throw on a timer thread.
        // It resumes only after the invocation has finished (a fixed delay could fire before that under load).
        await _loop.InvokeAsync(() => { _ = Late(); return Task.CompletedTask; });
        // The result reaches this thread before the loop closes the invocation's permit; release after it closed.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_loop.DrainStatus.Outstanding != 0 && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.Equal(0, _loop.DrainStatus.Outstanding);
        release.SetResult();
        Assert.True(await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _loop.OrphanContinuations);
        Assert.Equal(0, _loop.DrainStatus.Outstanding);
    }

    [Fact]
    public async Task AwaitContinuationsComeBackToTheLoop()
    {
        bool onLoopAfterAwait = await _loop.InvokeAsync(async () =>
        {
            await Task.Delay(20);
            await Task.Run(() => { });
            return _loop.IsOnLoop;
        });
        Assert.True(onLoopAfterAwait);
    }

    [Fact]
    public async Task SuspendedInvokeCompletesAfterStopBeginsAndRejectsNewOrdinaryWork()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        await loop.StartAsync(default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = loop.InvokeAsync(async () =>
        {
            entered.SetResult();
            await resume.Task;
            return loop.IsOnLoop;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = loop.StopAsync(default);
        Assert.Equal(GameLoopPhase.Draining, loop.Phase);
        Assert.False(stop.IsCompleted);
        Assert.True(loop.InvokeAsync(() => Task.CompletedTask).IsCanceled);
        resume.SetResult();
        Assert.True(await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GameLoopPhase.Stopped, loop.Phase);
        Assert.True(loop.DrainStatus.Clean);
        Assert.Empty(loop.DrainStatus.Debt);
    }

    [Fact]
    public async Task OffLoopCompletionCanResumeAndPostRequiredFollowupDuringDrain()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        await loop.StartAsync(default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = loop.InvokeAsync(async () =>
        {
            entered.SetResult();
            await receipt.Task;
            Assert.True(loop.IsOnLoop);
            loop.Post(() => { followup.SetResult(); return Task.CompletedTask; });
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = loop.StopAsync(default);
        await Task.Run(receipt.SetResult);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await followup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task StopWaitsForTickerStartedDetachedCompletion()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        using var releaseTick = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Add(new CallbackTicker(() =>
        {
            entered.TrySetResult();
            releaseTick.Wait();
            loop.TrackDetached("ticker writer receipt", async () =>
            {
                detached.TrySetResult();
                await receipt.Task;
                Assert.True(loop.IsOnLoop);
            });
        }));
        await loop.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = loop.StopAsync(default);
        releaseTick.Set();
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        receipt.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task ReservedConnectionCleanupRunsDuringDrain()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        await loop.StartAsync(default);
        using var reservation = loop.ReserveCompletion("accepted connection")!;
        loop.Quiesce();
        Assert.Null(loop.ReserveCompletion("late connection"));
        var cleanup = loop.InvokeCleanupAsync(reservation, () => Task.CompletedTask);
        var stop = loop.StopAsync(default);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        reservation.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task DeadlinesDisposeAfterWorkerExitNeedsNoOrdinaryAdmission()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        var deadlines = new Deadlines(loop);
        loop.Add(deadlines);
        await loop.StartAsync(default);
        await loop.StopAsync(default);
        long rejected = loop.DrainStatus.Rejected;
        deadlines.Dispose();
        deadlines.Dispose();
        await deadlines.DisposalComplete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(rejected, loop.DrainStatus.Rejected);
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task DeadlinesDisposeDuringHeldDrainDefersCollectionTeardown()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        var deadlines = new Deadlines(loop);
        loop.Add(deadlines);
        await loop.StartAsync(default);
        await loop.InvokeAsync(() =>
        {
            deadlines.SetGlobal("held", DateTimeOffset.UtcNow.AddHours(1), (_, _, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = loop.InvokeAsync(async () => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = loop.StopAsync(default);
        try
        {
            Assert.Equal(GameLoopPhase.Draining, loop.Phase);
            await Task.WhenAll(Task.Run(deadlines.Dispose), Task.Run(deadlines.Dispose));
            Assert.Equal(1, deadlines.Count); // live worker still owns the collection
            Assert.Equal(0, loop.DrainStatus.Rejected);
        }
        finally { release.TrySetResult(); }
        await admitted.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.WorkerExited.WaitAsync(TimeSpan.FromSeconds(5));
        await deadlines.DisposalComplete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, deadlines.Count);
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task OrdinaryPostCapacityRefusesTheNextJobExplicitly()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await loop.StartAsync(default);
        loop.Post(() => { entered.TrySetResult(); release.Wait(); return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 1; i < 4096; i++) loop.Post(() => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => loop.Post(() => Task.CompletedTask));
        Assert.Equal(1, loop.DrainStatus.Rejected);
        release.Set();
        await loop.StopAsync(default);
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task SaturatedRootsStillAdmitAlreadyCountedSpawnCompletion()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        using var releaseBlocker = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pending = 0;
        await loop.StartAsync(default);
        var root = loop.InvokeAsync(async () =>
        {
            entered.SetResult();
            await receipt.Task;
            pending++;
            loop.TrackDetached("admitted spawn", () =>
            {
                pending--;
                spawnDone.SetResult();
                return Task.CompletedTask;
            });
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loop.Post(() => { blocked.SetResult(); releaseBlocker.Wait(); return Task.CompletedTask; });
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 4094; i++) loop.Post(() => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => loop.Post(() => Task.CompletedTask));
        receipt.SetResult();
        var stop = loop.StopAsync(default);
        releaseBlocker.Set();
        await root.WaitAsync(TimeSpan.FromSeconds(5));
        await spawnDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, pending);
        Assert.True(loop.DrainStatus.Clean);
    }

    [Fact]
    public async Task GateStopPreservesQueuedOutputFromAdmittedFrame()
    {
        int port = TestPorts.FreePort();
        using var gate = new SuspendedGate(port, respond: true);
        await gate.StartAsync(default);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.SendAsync(new byte[] { 2, 0, 0, 0, 1, 0 }, SocketFlags.None);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = gate.StopAsync(default);
        Assert.False(stop.IsCompleted);
        gate.Release();
        var response = new byte[7];
        int received = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received < response.Length)
        {
            int n = await client.ReceiveAsync(response.AsMemory(received), SocketFlags.None, timeout.Token);
            Assert.True(n > 0);
            received += n;
        }
        Assert.Equal(new byte[] { 3, 0, 0, 0, 2, 0, 42 }, response);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.DrainSucceeded);
    }

    [Fact]
    public async Task QuiescingGateRefusesSecondFrameAndFlushesFirstResponse()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        await loop.StartAsync(default);
        int port = TestPorts.FreePort();
        using var gate = new SuspendedGate(port, respond: true, admission: loop);
        await gate.StartAsync(default);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.SendAsync(new byte[] { 2, 0, 0, 0, 1, 0, 2, 0, 0, 0, 1, 0 }, SocketFlags.None);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        loop.Quiesce();
        var stop = gate.StopAsync(default);
        gate.Release();
        var response = new byte[7];
        int received = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received < response.Length)
        {
            int n = await client.ReceiveAsync(response.AsMemory(received), SocketFlags.None, timeout.Token);
            Assert.True(n > 0);
            received += n;
        }
        Assert.Equal(new byte[] { 3, 0, 0, 0, 2, 0, 42 }, response);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.StopAsync(default);
        Assert.Equal(2, gate.Seen);
        Assert.True(gate.DrainSucceeded);
    }

    [Fact]
    public async Task UnstartedAdmittedWorkIsReportedAsDebt()
    {
        using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        loop.Post(() => Task.CompletedTask);
        await loop.StopAsync(default);
        Assert.Equal(GameLoopPhase.Stopped, loop.Phase);
        Assert.False(loop.DrainStatus.Clean);
        Assert.Single(loop.DrainStatus.Debt);
        Assert.Contains(loop.DrainStatus.FailedObligations, x => x.Contains("unstarted"));
    }

    [Fact]
    public async Task GateStopReportsOutstandingServeTaskUntilFrameHandlerCompletes()
    {
        int port = TestPorts.FreePort();
        using var gate = new SuspendedGate(port);
        await gate.StartAsync(default);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.SendAsync(new byte[] { 2, 0, 0, 0, 1, 0 }, SocketFlags.None);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await gate.StopAsync(cancellation.Token);
        Assert.False(gate.DrainSucceeded);
        Assert.Equal(1, gate.OutstandingServeTasks);
        gate.Release();
        await gate.Disconnected.WaitAsync(TimeSpan.FromSeconds(5));
        await gate.StopAsync(default);
        Assert.Equal(0, gate.OutstandingServeTasks);
    }

    [Fact]
    public async Task TickersAdvanceWithTheClock()
    {
        var ticker = new CountingTicker(_loop);
        _loop.Add(ticker);
        // 50 ms ticks; wait for a few (the test host may be busy with other tests).
        for (int i = 0; i < 100 && ticker.Ticks < 4; i++) await Task.Delay(50);
        Assert.True(ticker.Ticks >= 4);
        // Off-loop Remove only queues removal; wait for it before enumerating the ticker's list.
        await _loop.InvokeAsync(() => { _loop.Remove(ticker); return Task.CompletedTask; });
        Assert.True(ticker.AllOnLoop);
        Assert.All(ticker.Deltas, d => Assert.True(d.TotalMilliseconds >= 1));
    }

    [Fact]
    public async Task CanceledShutdownDefersQueueDisposalUntilBlockedWorkerDrains()
    {
        var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await loop.StartAsync(default);
        loop.Post(() =>
        {
            entered.TrySetResult();
            release.Wait();
            return Task.CompletedTask;
        });
        loop.Post(() => { drained.TrySetResult(); return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task stop = Task.Run(() => loop.StopAsync(cancellation.Token));
        Task? dispose = null;
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(loop.DrainStatus.Clean);
            Assert.NotEmpty(loop.DrainStatus.Debt);
            Assert.True(loop.InvokeAsync(() => Task.CompletedTask).IsCanceled);
            dispose = Task.Run(loop.Dispose);
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(drained.Task.IsCompleted);
            release.Set();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await loop.StopAsync(default); // worker has now used the queue safely after Dispose returned
        }
        finally
        {
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose != null) await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            loop.Dispose();
        }

        Assert.True(loop.InvokeAsync(() => Task.CompletedTask).IsCanceled);
    }

    [Fact]
    public async Task UnstartedLoopCanStopAndDisposeRepeatedly()
    {
        var loop = new GameLoop(NullLogger<GameLoop>.Instance);
        await loop.StopAsync(default);
        loop.Dispose();
        loop.Dispose();
        await loop.StopAsync(default);
        Assert.True(loop.InvokeAsync(() => Task.CompletedTask).IsCanceled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => loop.StartAsync(default));
    }

    private sealed class CountingTicker(GameLoop loop) : IGameTicker
    {
        public int Ticks;
        public bool AllOnLoop = true;
        public readonly List<TimeSpan> Deltas = new();

        public void Tick(GameTime time)
        {
            Ticks++;
            AllOnLoop &= loop.IsOnLoop;
            Deltas.Add(time.Delta);
        }
    }

    private sealed class CallbackTicker(Action callback) : IGameTicker
    {
        public void Tick(GameTime time) => callback();
    }

    private sealed class SuspendedGate(int port, bool respond = false, GameLoop? admission = null) : GateListener(IPAddress.Loopback, port, NullLogger.Instance)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public Task Disconnected => _disconnected.Task;
        public int Seen { get; private set; }
        public void Release() => _release.TrySetResult();
        protected override string Name => "SuspendedTestGate";
        protected override async ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body)
        {
            Seen++;
            if (admission != null && admission.Phase != GameLoopPhase.Accepting)
                throw new OperationCanceledException("Test gate is quiescing.");
            _entered.TrySetResult();
            await _release.Task;
            if (respond) await channel.SendRawAsync(2, new byte[] { 42 });
        }
        protected override ValueTask OnDisconnectedAsync(PhonestChannel channel)
        {
            _disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
