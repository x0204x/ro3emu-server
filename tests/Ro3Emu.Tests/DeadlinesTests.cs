using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

public sealed class DeadlinesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly GameLoop _loop = new(NullLogger<GameLoop>.Instance);
    private readonly ManualClock _clock = new(Epoch);
    private readonly List<PhonestChannel> _channels = [];
    private Deadlines _deadlines = null!;

    public Task InitializeAsync()
    {
        _deadlines = new Deadlines(_loop, _clock, perTickLimit: 2);
        _loop.Add(_deadlines);
        return _loop.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        await _loop.InvokeAsync(() => { _deadlines.Dispose(); return Task.CompletedTask; });
        await _loop.StopAsync(default);
        _loop.Dispose();
        foreach (var channel in _channels) await channel.DisposeAsync();
    }

    [Fact]
    public async Task ExactFractionalBoundaryAndLateDeliveryPassTheLogicalDueInstant()
    {
        var session = NewSession();
        var seen = new List<DateTimeOffset>();
        var firstDue = Epoch.AddMilliseconds(1250);
        await OnLoop(() =>
        {
            _deadlines.Set(session, "first", firstDue,
            (s, due, lease, ct) => { Assert.Same(session, s); seen.Add(due); return Task.CompletedTask; });
        });

        await OnLoop(() => { _clock.Set(firstDue.AddTicks(-1)); _deadlines.Tick(default); });
        await Barrier();
        Assert.Empty(seen);

        await OnLoop(() => { _clock.Set(firstDue); _deadlines.Tick(default); });
        await Barrier();
        Assert.Equal(new[] { firstDue }, seen);

        var secondDue = firstDue.AddMilliseconds(175);
        await OnLoop(() =>
        {
            _deadlines.Set(session, "second", secondDue,
            (s, due, lease, ct) => { seen.Add(due); return Task.CompletedTask; });
        });
        await OnLoop(() => { _clock.Set(secondDue.AddSeconds(11)); _deadlines.Tick(default); });
        await Barrier();
        Assert.Equal(new[] { firstDue, secondDue }, seen);
        Assert.Equal(0, await OnLoop(() => _deadlines.Count));
    }

    [Fact]
    public async Task ReplacementAndCancellationFenceAlreadyPostedWork()
    {
        var session = NewSession();
        var seen = new List<string>();
        await OnLoop(() =>
        {
            var old = _deadlines.Set(session, "service", Epoch,
                (s, due, lease, ct) => { seen.Add("old"); return Task.CompletedTask; });
            _deadlines.Tick(default); // old is posted, but not executed yet
            var latest = _deadlines.Set(session, "service", Epoch,
                (s, due, lease, ct) => { seen.Add("latest"); return Task.CompletedTask; });
            Assert.False(_deadlines.IsCurrent(session, old));
            Assert.True(_deadlines.IsCurrent(session, latest));
            _deadlines.Tick(default);
            _deadlines.Cancel(session, "service"); // latest is posted too
            Assert.False(_deadlines.IsCurrent(session, latest));
        });
        await Barrier();
        Assert.Empty(seen);
        Assert.Equal(0, await OnLoop(() => _deadlines.Count));
    }

    [Fact]
    public async Task RoleRecordRefreshKeepsDeadlineButRoleSwitchAndNewAttachmentFenceIt()
    {
        var session = NewSession();
        var seen = 0;
        await OnLoop(() =>
        {
            session.Role = NewRole(101, 1000);
            _deadlines.Attach(session);
            var lease = _deadlines.Set(session, "job", Epoch,
                (s, due, token, ct) => { seen++; return Task.CompletedTask; });
            session.Role = NewRole(101, 2000); // career update replaces the same role record
            Assert.True(_deadlines.IsCurrent(session, lease));
            _deadlines.Tick(default);
        });
        await Barrier();
        Assert.Equal(1, seen);

        await OnLoop(() =>
        {
            var old = _deadlines.Set(session, "job", Epoch,
                (s, due, lease, ct) => { seen++; return Task.CompletedTask; });
            _deadlines.Tick(default);
            session.Role = NewRole(102, 1000);
            _deadlines.Attach(session); // role selection begins a new attachment
            Assert.False(_deadlines.IsCurrent(session, old));
        });
        await Barrier();
        Assert.Equal(1, seen);

        await OnLoop(() =>
        {
            var lease = _deadlines.Set(session, "job", Epoch.AddHours(1),
                (s, due, token, ct) => Task.CompletedTask);
            _deadlines.Detach(session);
            Assert.False(_deadlines.IsCurrent(session, lease));
            Assert.Equal(default, _deadlines.Set(session, "job", Epoch,
                (s, due, token, ct) => Task.CompletedTask));
            _deadlines.Attach(session);
            Assert.NotEqual(default, _deadlines.Capture(session));
        });

        var replacementConnection = NewSession();
        await OnLoop(() =>
        {
            session.Role = NewRole(101, 1000);
            replacementConnection.Role = NewRole(101, 1000);
            _deadlines.Set(session, "reconnect", Epoch,
                (s, due, lease, ct) => { seen++; return Task.CompletedTask; });
            _deadlines.Tick(default);
            _deadlines.Detach(session);
            _deadlines.Attach(replacementConnection);
            _deadlines.Set(replacementConnection, "reconnect", Epoch,
                (s, due, lease, ct) => { seen++; return Task.CompletedTask; });
            _deadlines.Tick(default);
        });
        await Barrier();
        Assert.Equal(2, seen); // only the replacement connection ran
    }

    [Fact]
    public async Task AwaitingCallbackGetsCancellationAndCanFencePostAwaitEffect()
    {
        var session = NewSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        await OnLoop(() =>
        {
            _deadlines.Set(session, "await", Epoch, async (s, due, lease, ct) =>
            {
                entered.TrySetResult();
                await release.Task;
                if (_deadlines.IsCurrent(s, lease) && !ct.IsCancellationRequested) applied = true;
                finished.TrySetResult();
            });
            _deadlines.Tick(default);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await OnLoop(() => _deadlines.Cancel(session, "await"));
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(applied);
    }

    [Fact]
    public async Task SimultaneousSessionAndGlobalDeadlinesSharePerTickQuota()
    {
        var a = NewSession();
        var b = NewSession();
        var seen = new List<int>();
        await OnLoop(() =>
        {
            _deadlines.Set(a, "a1", Epoch, (s, d, lease, ct) => { seen.Add(1); return Task.CompletedTask; });
            _deadlines.Set(b, "b1", Epoch, (s, d, lease, ct) => { seen.Add(2); return Task.CompletedTask; });
            _deadlines.SetGlobal("g1", Epoch, (d, lease, ct) => { seen.Add(3); return Task.CompletedTask; });
            _deadlines.Set(a, "a2", Epoch, (s, d, lease, ct) => { seen.Add(4); return Task.CompletedTask; });
            _deadlines.SetGlobal("g2", Epoch, (d, lease, ct) => { seen.Add(5); return Task.CompletedTask; });
            Assert.Equal(5, _deadlines.WaitingCount);
            _deadlines.Tick(default);
            Assert.Equal(3, _deadlines.WaitingCount);
            _deadlines.Tick(default);
            Assert.Equal(1, _deadlines.WaitingCount);
            _deadlines.Tick(default);
            Assert.Equal(0, _deadlines.WaitingCount);
        });
        await Barrier();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, seen);
    }

    [Fact]
    public async Task DetachRemovesLongDeadlineAndReleasesSessionAndCallbackCapture()
    {
        var references = await OnLoop(() => CreateAndDetach());
        await Barrier();
        for (var i = 0; i < 3 && (references.Session.IsAlive || references.Capture.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(references.Session.IsAlive);
        Assert.False(references.Capture.IsAlive);
        Assert.Equal(0, await OnLoop(() => _deadlines.Count));
    }

    [Fact]
    public async Task RepeatedReplacementHasOneQueuedEntryAndCancellationRemovesIt()
    {
        var session = NewSession();
        await OnLoop(() =>
        {
            for (var i = 0; i < 1000; i++)
                _deadlines.Set(session, "repeat", Epoch.AddYears(10),
                    (s, due, lease, ct) => Task.CompletedTask);
            Assert.Equal(1, _deadlines.WaitingCount);
            Assert.Equal(1, _deadlines.Count);
            _deadlines.Cancel(session, "repeat");
            Assert.Equal(0, _deadlines.WaitingCount);
            Assert.Equal(0, _deadlines.Count);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference Session, WeakReference Capture) CreateAndDetach()
    {
        var session = NewSession();
        var capture = new object();
        _deadlines.Set(session, "long", Epoch.AddYears(10), (s, due, lease, ct) =>
        {
            GC.KeepAlive(capture);
            return Task.CompletedTask;
        });
        var references = (new WeakReference(session), new WeakReference(capture));
        _deadlines.Detach(session);
        return references;
    }

    private GameSession NewSession()
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
            NullLogger.Instance);
        _channels.Add(channel);
        return new GameSession(channel, null!);
    }

    private static Role NewRole(long id, int career) =>
        new(id, "account", 0, "Hero", 1, career, 1, 0, 0, 0, 1, 0, 0, 0, 0);

    private Task OnLoop(Action action) => _loop.InvokeAsync(() => { action(); return Task.CompletedTask; });

    private Task<T> OnLoop<T>(Func<T> read) => _loop.InvokeAsync(() => Task.FromResult(read()));

    private Task Barrier() => _loop.InvokeAsync(() => Task.CompletedTask);

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public void Set(DateTimeOffset now) => Interlocked.Exchange(ref _ticks, now.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }
}
