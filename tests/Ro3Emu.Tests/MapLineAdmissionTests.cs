using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class MapLineAdmissionTests
{
    private sealed class Sink : IAoiSink
    {
        public Task EnterAsync(GameSession a, GameSession b) => Task.CompletedTask;
        public Task LeaveAsync(GameSession a, GameSession b) => Task.CompletedTask;
        public Task MoveAsync(GameSession a, GameSession b, WalkReq w) => Task.CompletedTask;
        public Task TurnAsync(GameSession a, GameSession b, float f) => Task.CompletedTask;
        public Task UnitsChangedAsync(GameSession a, IReadOnlyList<WorldUnit> b, IReadOnlyList<long> c) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession a, MovementChangeNtf b) => Task.CompletedTask;
    }
    private readonly GameData _data;
    private readonly PublicLineManager _lines;
    public MapLineAdmissionTests()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "Ro3Emu.slnx"))) root = Path.GetDirectoryName(root);
        _data = new GameData(Options.Create(new ServerOptions { GameDataDir = Path.Combine(root!, "data") }), NullLogger<GameData>.Instance);
        var world = new World(new Sink(), new GameLoop(NullLogger<GameLoop>.Instance), NullLogger<World>.Instance);
        _lines = new PublicLineManager(_data, world);
    }
    private void Configure(int soft, int hard, bool create)
    {
        var rows = (IDictionary<string, JsonElement>)_data.Table("MapConfig");
        rows["10100"] = JsonSerializer.SerializeToElement(new { _iRoleAlert = soft, _iRoleCntLimit = hard, _iAllowCreateLine = create ? 1 : 0 });
    }
    private static GameSession Session() => new(null!, null!);

    [ClientDataFact]
    public async Task SixHundredConcurrentPronteraAdmissionsRespectAuthoredLimits()
    {
        Assert.Equal((200, 300, true), _lines.Limits(10100));
        var sessions = Enumerable.Range(0, 600).Select(_ => Session()).ToArray();
        var admitted = await Task.WhenAll(sessions.Select(s => Task.Run(() => _lines.TryReserve(s, 10100))));
        Assert.All(admitted, r => Assert.NotNull(r));
        var state = _lines.Snapshot(10100);
        Assert.True(state.Count >= 2);
        Assert.Equal(600, state.Sum(l => l.Load));
        Assert.All(state, l => Assert.InRange(l.Load, 1, l.HardLimit));
        Assert.All(state, l => Assert.Equal(0, l.Players));
        Assert.Equal(3, state.Count);
        Assert.All(state, l => Assert.Equal(200, l.Load));
    }

    [Fact]
    public async Task RequestedLineNeverOverbooksAndReleaseIsTokenBound()
    {
        Configure(2, 3, false);
        var first = Session(); var reservation = _lines.TryReserve(first, 10100)!;
        var applicants = Enumerable.Range(0, 50).Select(_ => Session()).ToArray();
        var result = await Task.WhenAll(applicants.Select(s => Task.Run(() => _lines.TryReserve(s, 10100, requested: 1))));
        Assert.Equal(2, result.Count(r => r != null));
        Assert.Equal(3, Assert.Single(_lines.Snapshot(10100)).Load);
        _lines.Release(first, reservation);
        var second = _lines.TryReserve(first, 10100)!;
        _lines.Release(first, reservation);
        Assert.Equal(second, _lines.Reserved(first));
        Assert.Null(_lines.TryReserve(Session(), 10100, createNew: true));
        Assert.Null(_lines.TryReserve(Session(), 10100, requested: 2));
    }

    [Fact]
    public async Task RetirementWaitsForReservationsAndOwnerWorkAndDoesNotReuseNumbers()
    {
        Configure(1, 2, true);
        var a = Session(); var b = Session();
        _lines.TryReserve(a, 10100);
        var extra = _lines.TryReserve(b, 10100)!;
        Assert.Equal(2, extra.Map.LineNumber);
        await _lines.RetireEmptyAsync(); Assert.Equal(2, _lines.Snapshot(10100).Count);
        bool pending = true; bool retired = false;
        _lines.HasPendingWork.Add(k => pending);
        _lines.Retiring.Add(k => { Assert.Equal(extra.Map, k); retired = true; return Task.CompletedTask; });
        _lines.Release(b, extra);
        await _lines.RetireEmptyAsync(); Assert.False(retired);
        pending = false;
        await _lines.RetireEmptyAsync(); Assert.True(retired);
        Assert.Single(_lines.Snapshot(10100));
        Assert.Equal(3, _lines.TryReserve(b, 10100)!.Map.LineNumber);
    }

    [Fact]
    public void FollowCanUseYellowLineButNeverBypassesHardCap()
    {
        Configure(1, 2, true);
        _lines.TryReserve(Session(), 10100);
        Assert.Equal(1, _lines.TryReserve(Session(), 10100, follow: 1)!.Map.LineNumber);
        Assert.Equal(2, _lines.TryReserve(Session(), 10100, follow: 1)!.Map.LineNumber);
        Assert.Null(_lines.TryReserve(Session(), 10100, requested: 1));
    }

    [Fact]
    public void RuntimeKindsAndLinesSeparateWorldIdentityWithoutPrivateFlagOnPublicPlayers()
    {
        var a = Session(); a.SceneEntry = 10100;
        var b = Session(); b.SceneEntry = 10100; b.LineNumber = 2;
        Assert.Equal(0, b.InstanceId);
        Assert.NotEqual(a.RuntimeMap, b.RuntimeMap); Assert.NotEqual(a.WorldScene, b.WorldScene);
        Assert.NotEqual(RuntimeMapKey.Private(10100, 1L << 40).WorldScene, RuntimeMapKey.Event(10100, 1L << 40).WorldScene);
    }
}
