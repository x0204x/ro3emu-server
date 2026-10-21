using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

public class WorldTests
{
    private sealed class RecordingSink : IAoiSink
    {
        public List<string> Events { get; } = new();
        public Func<GameSession, IReadOnlyList<WorldUnit>, Task>? BeforeUnitReply { get; set; }

        public Task EnterAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}+{subject.Role!.Name}");

        public Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk) => Add($"{viewer.Role!.Name}~{subject.Role!.Name}");

        public Task TurnAsync(GameSession viewer, GameSession subject, float face) => Add($"{viewer.Role!.Name}^{subject.Role!.Name}");

        public Task LeaveAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}-{subject.Role!.Name}");

        public async Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left)
        {
            if (BeforeUnitReply != null) await BeforeUnitReply(viewer, entered);
            await Add($"{viewer.Role!.Name} units +[{string.Join(",", entered.Select(u => u.Uid))}] -[{string.Join(",", left)}]");
        }

        public Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement) => Add($"{viewer.Role!.Name} unit~{movement.Uid}");

        private Task Add(string e)
        {
            lock (Events) Events.Add(e);
            return Task.CompletedTask;
        }
    }

    private readonly RecordingSink _sink = new();
    private readonly World _world;

    public WorldTests() => _world = new World(_sink, new GameLoop(NullLogger<GameLoop>.Instance), NullLogger<World>.Instance);

    private sealed class TestUnit : WorldUnit
    {
        public override EntityBase Entity() => new() { Uuid = (ulong)Uid, EntityType = 1 };
    }

    private static TestUnit Unit(long uid, float x, float z, long scene = 10111) => new() { Uid = uid, SceneEntry = scene, X = x, Z = z };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendedUnitEnteredCallbackDoesNotBlockPlayerLeave(bool moving)
    {
        var player = Player("A", moving ? 300 : 100, 100);
        var unit = Unit(7, 100, 100);
        var laterUnit = Unit(8, 100, 100);
        if (moving) await _world.JoinAsync(player);
        await _world.AddUnitAsync(unit);
        await _world.AddUnitAsync(laterUnit);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool laterCallback = false;
        _world.UnitEntered.Add(async (viewer, observed) =>
        {
            Assert.Same(player, viewer);
            Assert.Same(unit, observed);
            Assert.Contains("A units +[7,8] -[]", _sink.Events);
            entered.SetResult();
            await release.Task;
        });
        _world.UnitEntered.Add((_, _) => { laterCallback = true; return Task.CompletedTask; });
        Task publication = moving ? _world.MoveAsync(player, To(player, 100, 100)) : _world.JoinAsync(player);
        Task? leaving = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Exact Dungeon hang edge: the enter callback waits for room closure, which calls Leave.
            // The recording sink completes synchronously, so a pending Leave identifies the AOI gate;
            // this assertion does not depend on timers or thread scheduling.
            leaving = _world.LeaveAsync(player);
            Assert.True(leaving.IsCompletedSuccessfully, "UnitEntered retained the player AOI gate needed by Leave.");
            Assert.False(publication.IsCompleted);
            Assert.DoesNotContain(player, _world.PlayersIn(player.WorldScene));
            Assert.DoesNotContain(unit, player.VisibleUnits);
            Assert.DoesNotContain(laterUnit, player.VisibleUnits);
            Assert.DoesNotContain(player, unit.Viewers);
        }
        finally
        {
            release.TrySetResult();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            if (leaving != null) await leaving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(laterCallback, "A later callback replayed unit state after the viewer left.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedUnitCannotPublishLeaveBeforeSuspendedJoinOrMoveEnter(bool moving)
    {
        var player = Player("A", moving ? 300 : 100, 100);
        var unit = Unit(7, 100, 100);
        if (moving) await _world.JoinAsync(player);
        await _world.AddUnitAsync(unit);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sink.BeforeUnitReply = async (_, bases) =>
        {
            if (!bases.Contains(unit)) return;
            entered.TrySetResult();
            await release.Task;
        };
        try
        {
            Task publication = moving ? _world.MoveAsync(player, To(player, 100, 100)) : _world.JoinAsync(player);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task removal = _world.RemoveUnitAsync(unit);
            Assert.False(removal.IsCompleted);
            release.TrySetResult();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "A units +[7] -[]", "A units +[] -[7]" }, _sink.Events);
            Assert.DoesNotContain(unit, player.VisibleUnits);
            Assert.DoesNotContain(player, unit.Viewers);
            Assert.DoesNotContain(unit, _world.AllUnits());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RemovedCartLikeUnitCanBeReusedInAnotherSceneWithoutStaleMoveRecreatingOldMap()
    {
        var oldViewer = Player("Old", 100, 100);
        var newViewer = Player("New", 100, 100, scene: 10101);
        await _world.JoinAsync(oldViewer);
        await _world.JoinAsync(newViewer);
        var unit = Unit(7, 100, 100);
        await _world.AddUnitAsync(unit);
        await _world.RemoveUnitAsync(unit);
        await _world.MoveUnitAsync(unit, new MovementChangeNtf { Uid = 7 });
        Assert.DoesNotContain(unit, _world.AllUnits());
        unit.SceneEntry = 10101;
        await _world.AddUnitAsync(unit);
        Assert.DoesNotContain(unit, oldViewer.VisibleUnits);
        Assert.Contains(unit, newViewer.VisibleUnits);
        Assert.Equal(new[]
        {
            "Old units +[7] -[]", "Old units +[] -[7]", "New units +[7] -[]",
        }, _sink.Events);
    }

    [Fact]
    public async Task UnitsAppearForPlayersInRangeAndFollowThem()
    {
        var a = Player("A", 100, 100);
        await _world.JoinAsync(a);
        var near = Unit(7, 110, 100);
        var far = Unit(8, 300, 100);
        await _world.AddUnitAsync(near);
        await _world.AddUnitAsync(far);
        Assert.Equal(new[] { "A units +[7] -[]" }, _sink.Events);

        // The unit walks out of sight, then back.
        _sink.Events.Clear();
        near.X = 200;
        await _world.MoveUnitAsync(near, new MovementChangeNtf { Uid = 7 });
        near.X = 120;
        await _world.MoveUnitAsync(near, new MovementChangeNtf { Uid = 7 });
        await _world.MoveUnitAsync(near, new MovementChangeNtf { Uid = 7 });
        Assert.Equal(new[] { "A units +[] -[7]", "A units +[7] -[]", "A unit~7" }, _sink.Events);

        // The player walks to the far one: one message with both changes.
        _sink.Events.Clear();
        await _world.MoveAsync(a, To(a, 290, 100));
        Assert.Equal(new[] { "A units +[8] -[7]" }, _sink.Events);

        // Removing a unit tells its viewers; a player joining later gets all visible units at once.
        _sink.Events.Clear();
        await _world.RemoveUnitAsync(far);
        Assert.Equal(new[] { "A units +[] -[8]" }, _sink.Events);
        var b = Player("B", 115, 100);
        _sink.Events.Clear();
        await _world.JoinAsync(b);
        Assert.Contains("B units +[7] -[]", _sink.Events);
    }

    [Fact]
    public async Task GuardianVisibilityPolicyRemovesAndRestoresDirectedEdgesWithoutLeakingOnMove()
    {
        var a = Player("A", 100, 100);
        var b = Player("B", 101, 100);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);
        bool hidden = true;
        _world.CanObserve = (viewer, subject) => !hidden || viewer != b || subject != a;
        await _world.RefreshVisibilityAsync(a);
        Assert.DoesNotContain(a, b.Visible);
        Assert.Contains(b, a.Visible);
        await _world.MoveAsync(a, To(a, 102, 100));
        Assert.DoesNotContain(a, b.Visible);
        hidden = false;
        await _world.RefreshVisibilityAsync(b);
        Assert.Contains(a, b.Visible);
        Assert.Contains("B-A", _sink.Events);
        Assert.Contains("B+A", _sink.Events);
    }

    private static GameSession Player(string name, float x, float z, long scene = 10111)
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), NullLogger.Instance);
        return new GameSession(channel, null!)
        {
            Role = new Role(name.GetHashCode(), "acc", 1, name, 1, 1000, 1, 0, 0, 0, scene, x, 0, z, 0),
            SceneEntry = scene,
            Position = new Vec3 { X = x, Z = z },
        };
    }

    private static WalkReq To(GameSession s, float x, float z)
    {
        s.Position = new Vec3 { X = x, Z = z };
        return new WalkReq { Pos = s.Position };
    }

    [Fact]
    public async Task PlayersInRangeSeeEachOtherOnJoin()
    {
        var a = Player("A", 100, 100);
        var b = Player("B", 120, 100);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);
        Assert.Equal(new[] { "B+A", "A+B" }, _sink.Events);
    }

    [Fact]
    public async Task OtherScenesAndFarPlayersAreInvisible()
    {
        await _world.JoinAsync(Player("A", 100, 100));
        await _world.JoinAsync(Player("B", 100, 100, scene: 10112));
        await _world.JoinAsync(Player("C", 300, 300));
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public async Task WalkingInAndOutOfRangeEntersMovesAndLeaves()
    {
        var a = Player("A", 100, 100);
        var b = Player("B", 250, 100);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);

        await _world.MoveAsync(b, To(b, 140, 100)); // into range
        await _world.MoveAsync(b, To(b, 145, 100)); // visible move
        await _world.MoveAsync(b, To(b, 165, 100)); // past ViewRadius but inside the hysteresis band: still visible
        await _world.MoveAsync(b, To(b, 200, 100)); // out
        Assert.Equal(new[] { "A+B", "B+A", "A~B", "A~B", "A-B", "B-A" }, _sink.Events);
    }

    [Fact]
    public async Task LeavingTheSceneNotifiesWatchers()
    {
        var a = Player("A", 100, 100);
        var b = Player("B", 110, 100);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);
        _sink.Events.Clear();

        await _world.LeaveAsync(b);
        await _world.LeaveAsync(b); // second leave is a no-op
        Assert.Equal(new[] { "A-B" }, _sink.Events);
        Assert.Empty(a.Visible);
    }
}
