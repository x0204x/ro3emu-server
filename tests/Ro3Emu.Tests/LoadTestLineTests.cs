using Google.Protobuf;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

public sealed class LoadTestLineTests
{
    private static void Observe(WorldReducer r, uint id, IMessage packet) =>
        r.Observe(new(BotEventKind.Game) { MsgId = id, Body = packet.ToByteArray() });

    private static void Enter(WorldReducer r, long scene, int line, long runtime, ulong self)
    {
        Observe(r, MsgId.SwitchSceneLine, new SwitchSceneLineResp { SceneEntry = scene, Linenum = line, SceneID = runtime });
        Observe(r, MsgId.EntitySelf, new EntitySelf { Base = new EntityBase { Uuid = self, EntityType = 3 } });
        r.Observe(new(BotEventKind.Game) { MsgId = MsgId.SelfInfoEndFrameNotify });
    }

    [Fact]
    public void Reducer_tracks_line_handoff_ready_and_refusal_without_losing_current_scene()
    {
        var r = new WorldReducer();
        Enter(r, 10100, 1, 10100, 1);
        var bases = new AoIViewChangeData(); bases.Bases.Add(new EntityBase { Uuid = 2, EntityType = 3 });
        Observe(r, GameAoiSink.SyncAoiView, bases);
        Assert.True(r.Sees(2));
        Observe(r, MsgId.SwitchSceneLine, new SwitchSceneLineResp { Result = 2, SceneEntry = 10111, Linenum = 2 });
        Assert.Equal(new SceneLocation(10100, 10100, 1, true), r.Location);
        Assert.True(r.Sees(2));
        long second = RuntimeMapKey.Public(10100, 2).WorldScene;
        Observe(r, MsgId.SwitchSceneLine, new SwitchSceneLineResp { SceneEntry = 10100, Linenum = 2, SceneID = second });
        Assert.False(r.Location.Ready);
        Assert.False(r.Sees(2));
        Enter(r, 10100, 2, second, 1);
        r.BeginMeasurement();
        Assert.Equal(new SceneLocation(10100, second, 2, true), r.Snapshot().Location);
    }

    [Fact]
    public async Task Pairing_excludes_other_lines_private_rooms_loading_and_unseen_subjects()
    {
        await using var a = Actor(1, 10100, 1, 10100);
        await using var otherLine = Actor(2, 10100, 2, RuntimeMapKey.Public(10100, 2).WorldScene);
        await using var privateRoom = Actor(3, 10100, 1, 90001);
        await using var peer = Actor(4, 10100, 1, 10100);
        var candidates = new[] { a, otherLine, privateRoom, peer };
        Assert.Same(peer, PeerPairing.Select(a, candidates, false));
        Assert.Null(PeerPairing.Select(a, candidates, true));
        var bases = new AoIViewChangeData(); bases.Bases.Add(new EntityBase { Uuid = 1, EntityType = 3 });
        Observe(peer.Reducer, GameAoiSink.SyncAoiView, bases);
        Assert.Same(peer, PeerPairing.Select(a, candidates, true));
        Observe(peer.Reducer, MsgId.SwitchSceneLine, new SwitchSceneLineResp { SceneEntry = 10100, Linenum = 1, SceneID = 10100 });
        Assert.Null(PeerPairing.Select(a, candidates, false));
    }

    private static BotActor Actor(long role, long scene, int line, long runtime)
    {
        var reducer = new WorldReducer(); Enter(reducer, scene, line, runtime, (ulong)role);
        return new(role.ToString(), new BotClient("http://127.0.0.1:12345", ""), reducer) { RoleId = role, Scene = scene };
    }

    [Fact]
    public async Task Chat_ring_distributes_one_receiver_per_sender_with_interleaved_lines()
    {
        var actors = Enumerable.Range(1, 80).Select(id => Actor(id, 10100, 1 + id % 2,
            RuntimeMapKey.Public(10100, 1 + id % 2).WorldScene)).ToArray();
        try
        {
            var pairs = actors.Select(a => PeerPairing.Select(a, actors, false)!).ToArray();
            Assert.Equal(80, pairs.Distinct().Count());
            Assert.All(actors.Zip(pairs), pair => Assert.True(pair.First.Location.SameRuntime(pair.Second.Location)));
        }
        finally { foreach (var actor in actors) await actor.DisposeAsync(); }
    }

    [Fact]
    public void Movement_proof_requires_selected_viewer_and_same_runtime()
    {
        var c = new MovementCorrelation();
        var location = new SceneLocation(10100, 10100, 1, true);
        c.Offer(42, 1, 2, false, 43, location);
        var movement = new MovementChangeNtf { Uid = 42, MovementInfo = new() { Pos = new() { X = 1, Z = 2 }, MoveType = MovementType.MoveTypeWalk } };
        c.Seen(movement, 44, location);
        c.Seen(movement, 43, location with { LineNumber = 2 });
        c.Seen(movement, 43, location with { SceneId = 99999 });
        c.Seen(movement, 43, location with { Ready = false });
        Assert.Equal(0, c.Snapshot().ObservedOffers);
        Assert.Equal(1, c.Snapshot().NeverSeen);
        c.Seen(movement, 43, location);
        Assert.Equal(1, c.Snapshot().ObservedOffers);
        Assert.Equal(0, c.Snapshot().NeverSeen);
    }

    [Fact]
    public void Realistic_schedule_has_independent_phase_and_half_to_one_second_intervals()
    {
        var a = WalkSchedule.DueSeconds(600, 0, "realistic");
        var b = WalkSchedule.DueSeconds(600, 1, "realistic");
        Assert.InRange(a[0], 0, 1);
        Assert.NotEqual(a[0], b[0]);
        Assert.Equal(a, WalkSchedule.DueSeconds(600, 0, "realistic"));
        Assert.All(a.Zip(a.Skip(1), (x, y) => y - x), gap => Assert.InRange(gap, .5, 1));
        Assert.True(a[^1] < 600);
        var sync = WalkSchedule.DueSeconds(3, 2, "sync");
        Assert.Equal(new[] { .001, .751, 1.501, 2.251 }, sync);
        Assert.Equal("realistic", RunnerOptions.Parse(["--scenario", "S2", "--server-local"]).WalkPhase);
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--scenario", "S2", "--server-local", "--walk-phase", "bad"]));
    }

    [Fact]
    public async Task Server_metrics_separate_public_lines_and_private_instances()
    {
        var registry = new SessionRegistry(NullLogger<SessionRegistry>.Instance);
        foreach (var (id, line, instance) in new[] { (1, 1, 0L), (2, 2, 0L), (3, 1, 90001L), (4, 1, 90002L) })
        {
            var s = new GameSession(null!, null!)
            {
                Account = id.ToString(), SceneEntry = 10100, LineNumber = line,
                InstanceId = instance, Role = new Role(id, id.ToString(), 1, $"role{id}", 1, 1000, 1, 1, 0, 0, 10100, 0, 0, 0, 0)
            };
            await registry.RegisterAsync(s);
        }
        using var metrics = new ServerMetrics(false);
        var snapshot = JsonSerializer.SerializeToElement(metrics.Snapshot(SessionCensus.Take(registry)));
        var maps = snapshot.GetProperty("sessions").GetProperty("byMap").EnumerateArray().ToArray();
        Assert.Equal(4, maps.Length);
        Assert.Contains(maps, x => x.GetProperty("kind").GetString() == "PublicLine" && x.GetProperty("line").GetInt32() == 2 &&
            x.GetProperty("sceneId").GetInt64() == RuntimeMapKey.Public(10100, 2).WorldScene);
        Assert.Equal(2, maps.Count(x => x.GetProperty("kind").GetString() == "PrivateInstance"));
    }

    [Fact]
    public void Capacity_samples_use_admission_claims_not_provisional_login_labels()
    {
        var samples = new LineMeasurements();
        samples.Observe(JsonNode.Parse("""
            {"sessions":{"byMap":[{"map":10100,"line":1,"kind":"PublicLine","sceneId":10100,"count":500,"loading":301}],
            "publicLines":[{"map":10100,"line":1,"sceneId":10100,"players":199,"reservations":1,"softLimit":200,"hardLimit":300}]}}
            """)!);
        var row = Assert.Single(samples.Snapshot());
        Assert.Equal(199, row.Players);
        Assert.Equal(1, row.Loading);
        var report = Report();
        report.LineSamples = samples.Snapshot();
        Assert.Equal("PASS", TargetEvaluation.Evaluate(report).Single(x => x.Metric == "players per public line").Status);
        report.LineSamples = [row with { Players = 299, Loading = 2 }];
        Assert.Equal("FAIL", TargetEvaluation.Evaluate(report).Single(x => x.Metric == "players per public line").Status);
        var legacy = new LineMeasurements();
        legacy.Observe(JsonNode.Parse("""{"sessions":{"byMap":[{"map":10100,"line":0,"count":300}]}}""")!);
        Assert.Empty(legacy.Snapshot());
    }

    [Fact]
    public void Successful_subset_does_not_qualify_unpaired_movement_workload()
    {
        var report = Report();
        report.MovementPeerP99Ms = 20;
        report.MovementWithoutViewer = 1000;
        Assert.Equal("NOT MEASURED", TargetEvaluation.Evaluate(report).Single(x => x.Metric == "movement to viewer p99").Status);
        report.MovementPeerP99Ms = 200;
        Assert.Equal("FAIL", TargetEvaluation.Evaluate(report).Single(x => x.Metric == "movement to viewer p99").Status);
    }

    private static RunReport Report() => new()
    {
        Scenario = "S2", TargetBots = 2, RequestedBots = 2, RequestedSeconds = 600,
        StartedUtc = DateTimeOffset.UtcNow, MeasurementPhase = "scenario",
        ServerBefore = JsonNode.Parse("""{"epoch":"a","elapsedSeconds":0}"""),
        ServerAfter = JsonNode.Parse("""{"epoch":"a","elapsedSeconds":600}"""),
        Inbound = new() { ["a"] = Ready(), ["b"] = Ready() }
    };

    private static ReducerSnapshot Ready() => new(0, 0, 149, 1, 1, [], [], [], [])
    { Location = new(10100, 10100, 1, true) };
}
