using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>Directed player visibility with the client's own cap (QS_LodLevelCtrl 39/49/79/99/149).</summary>
public sealed class AoiCapTests
{
    private sealed class RecordingSink : IAoiSink
    {
        public List<string> Events { get; } = new();
        public Task EnterAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}+{subject.Role!.Name}");
        public Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk) => Add($"{viewer.Role!.Name}~{subject.Role!.Name}");
        public Task TurnAsync(GameSession viewer, GameSession subject, float face) => Add($"{viewer.Role!.Name}^{subject.Role!.Name}");
        public Task LeaveAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}-{subject.Role!.Name}");
        public Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement) => Task.CompletedTask;
        private Task Add(string e) { lock (Events) Events.Add(e); return Task.CompletedTask; }
    }

    private readonly RecordingSink _sink = new();
    private readonly World _world;
    private int _nextId = 1;

    public AoiCapTests() => _world = new World(_sink, new GameLoop(NullLogger<GameLoop>.Instance), NullLogger<World>.Instance);

    private GameSession Player(string name, float x, float z, int limit = World.MaxViewLimit)
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), NullLogger.Instance);
        return new GameSession(channel, null!)
        {
            Role = new Role(_nextId++, "acc", 1, name, 1, 1000, 1, 0, 0, 0, 10111, x, 0, z, 0),
            SceneEntry = 10111,
            Position = new Vec3 { X = x, Z = z },
            ViewLimit = limit,
        };
    }

    private static string Names(IEnumerable<GameSession> players) => string.Join(",", players.Select(p => p.Role!.Name).Order());

    [Fact]
    public async Task LowCapViewerShowsTheNearestWhileEveryoneStillSeesIt()
    {
        var others = new[] { Player("B", 105, 100), Player("C", 110, 100), Player("D", 120, 100), Player("E", 130, 100) };
        foreach (var o in others) await _world.JoinAsync(o);
        var low = Player("A", 100, 100, limit: 2);
        await _world.JoinAsync(low);

        Assert.Equal("B,C", Names(low.Visible));                 // the two nearest
        Assert.Equal("B,C,D,E", Names(low.Observers));           // directed: all of them still show A
        Assert.Equal("B,C,D,E", Names(_world.Viewers(low)));      // broadcasts about A reach its observers
        Assert.All(others, o => Assert.Contains(low, o.Visible));
        Assert.DoesNotContain("A+D", _sink.Events);
        Assert.Contains("D+A", _sink.Events);
    }

    [Fact]
    public async Task FullViewSwapsItsFarthestForANearerPlayerBeyondTheMargin()
    {
        var b = Player("B", 125, 100);
        var c = Player("C", 130, 100);
        foreach (var o in new[] { b, c }) await _world.JoinAsync(o);
        var low = Player("A", 100, 100, limit: 2);
        await _world.JoinAsync(low);
        var near = Player("D", 101, 100);   // arrives while A is full: A only gets its name board
        var edge = Player("E", 126, 100);   // nearer than C, but within the margin of B once C is gone
        await _world.JoinAsync(near);
        await _world.JoinAsync(edge);
        Assert.Equal("B,C", Names(low.Visible));
        _sink.Events.Clear();

        Assert.Equal(1, await _world.RebalanceAsync(_ => true));
        Assert.Equal("B,D", Names(low.Visible));
        Assert.Equal(new[] { "A-C", "A+D" }, _sink.Events.ToArray());
        Assert.Contains(low, near.Observers);
        Assert.DoesNotContain(low, c.Observers);

        // Stable afterwards: E (26) is not 5 units nearer than B (25), and nobody else qualifies.
        _sink.Events.Clear();
        Assert.Equal(0, await _world.RebalanceAsync(_ => true));
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public async Task RebalanceLeavesViewsWithRoomAlone()
    {
        var a = Player("A", 100, 100, limit: 3);
        var b = Player("B", 140, 100);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);
        Assert.Equal(0, await _world.RebalanceAsync(_ => true));
        Assert.Equal("B", Names(a.Visible));
    }

    [Fact]
    public async Task ViewerRefillsWhenAShownPlayerLeaves()
    {
        var b = Player("B", 105, 100);
        var c = Player("C", 110, 100);
        var d = Player("D", 120, 100);
        foreach (var o in new[] { b, c, d }) await _world.JoinAsync(o);
        var low = Player("A", 100, 100, limit: 2);
        await _world.JoinAsync(low);
        _sink.Events.Clear();

        await _world.LeaveAsync(b);
        Assert.Equal("C,D", Names(low.Visible));
        // The leaver's observers are told first, then the viewer that had a free slot takes the next nearest.
        Assert.Equal(4, _sink.Events.Count);
        Assert.Equal(new[] { "A-B", "C-B", "D-B" }, _sink.Events.Take(3).Order().ToArray());
        Assert.Equal("A+D", _sink.Events[^1]);

        // Walking away from C: C leaves both ways, A refills with nobody new (D is still shown).
        _sink.Events.Clear();
        c.Position = new Vec3 { X = 300, Z = 100 };
        await _world.MoveAsync(c, new WalkReq { Pos = c.Position });
        Assert.Equal("D", Names(low.Visible));
        Assert.DoesNotContain(low, c.Observers);
    }

    [Fact]
    public async Task ChangingTheCapDropsTheFarthestAndRaisingItRefills()
    {
        var players = new[] { Player("B", 105, 100), Player("C", 110, 100), Player("D", 120, 100) };
        foreach (var p in players) await _world.JoinAsync(p);
        var a = Player("A", 100, 100);
        await _world.JoinAsync(a);
        Assert.Equal("B,C,D", Names(a.Visible));

        _sink.Events.Clear();
        await _world.SetViewLimitAsync(a, 1);
        Assert.Equal("B", Names(a.Visible));
        Assert.Equal(new[] { "A-D", "A-C" }, _sink.Events);
        Assert.Equal("B,C,D", Names(a.Observers)); // others' views are not the viewer's business

        _sink.Events.Clear();
        await _world.SetViewLimitAsync(a, 149);
        Assert.Equal("B,C,D", Names(a.Visible));
        Assert.Equal(new[] { "A+C", "A+D" }, _sink.Events);

        await _world.SetViewLimitAsync(a, 0); // absent: keep the current cap
        Assert.Equal(149, a.ViewLimit);
    }

    [Fact]
    public async Task GridFindsPlayersAcrossCellBordersAndNotBeyondTheLeaveRadius()
    {
        // 68-unit cells: 67/69 straddle a border.
        var a = Player("A", 67, 67);
        var b = Player("B", 69, 69);
        var far = Player("F", 400, 400);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);
        await _world.JoinAsync(far);
        Assert.Equal("B", Names(a.Visible));
        Assert.Empty(far.Visible);

        // F walks next to A over several cells: it appears both ways once in range.
        _sink.Events.Clear();
        foreach (var step in new[] { 300f, 200f, 120f, 80f })
        {
            far.Position = new Vec3 { X = step, Z = step };
            await _world.MoveAsync(far, new WalkReq { Pos = far.Position });
        }
        Assert.Contains(far, a.Visible);
        Assert.Contains(a, far.Visible);
        Assert.Contains("A+F", _sink.Events);
        Assert.Contains("F+A", _sink.Events);

        // A moving player's observers get its movement, nobody else.
        _sink.Events.Clear();
        a.Position = new Vec3 { X = 70, Z = 70 };
        await _world.MoveAsync(a, new WalkReq { Pos = a.Position });
        Assert.Equal(new[] { "B~A", "F~A" }, _sink.Events.Order().ToArray());
    }
}
