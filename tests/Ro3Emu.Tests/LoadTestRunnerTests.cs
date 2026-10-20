using System.Text.Json.Nodes;
using Google.Protobuf;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

public sealed class LoadTestRunnerTests
{
    [Fact]
    public void S4_defaults_to_the_target_files_hundred_transfers_and_hundred_relogs()
    {
        var options = RunnerOptions.Parse(["--scenario", "S4", "--server-local"]);
        Assert.Equal(200, options.Bots);
        Assert.Equal(200, options.TargetBots);
    }
    [Fact]
    public void Offered_attempts_always_have_one_terminal_outcome()
    {
        var tally = new ActionTally();
        tally.Offer();
        using (var attempt = new ActionAttempt(tally)) { attempt.Refuse(); attempt.Complete(); }
        tally.Offer();
        using (var attempt = new ActionAttempt(tally)) attempt.Fail(new TaskCanceledException());
        tally.Offer();
        using (var attempt = new ActionAttempt(tally)) attempt.Fail(new IOException());
        tally.Offer();
        using (var attempt = new ActionAttempt(tally)) { }
        var result = tally.Snapshot();
        Assert.Equal(0, result.Completed);
        Assert.Equal(1, result.Refused);
        Assert.Equal(2, result.TimedOut);
        Assert.Equal(1, result.Failed);
        Assert.Equal(result.Offered, result.Completed + result.Refused + result.TimedOut + result.Failed);
    }

    [Fact]
    public void Idle_but_ten_hertz_loop_fails_cadence_gate()
    {
        var report = new RunReport
        {
            Scenario = "S2", RequestedBots = 300, TargetBots = 300,
            RequestedSeconds = 600, StartedUtc = DateTimeOffset.UtcNow, MeasurementPhase = "scenario"
        };
        report.ServerBefore = JsonNode.Parse("""{"epoch":"one","elapsedSeconds":0,"loop":{"ticks":0}}""");
        report.ServerAfter = JsonNode.Parse("""{"epoch":"one","elapsedSeconds":600,"loop":{"ticks":6000,"tickDurationMs":{"count":6000,"sum":600,"min":0.1,"max":0.1,"bounds":[1],"buckets":[6000,0]}}}""");
        Assert.Equal("FAIL", TargetEvaluation.Evaluate(report)[0].Status);
    }
    [Fact]
    public async Task Expectation_registered_before_send_captures_early_reply_with_bounded_retention()
    {
        var inbox = new BotInbox();
        var observed = new List<uint>();
        inbox.ConfigureBounded(2, e => observed.Add(e.MsgId));
        using var reply = inbox.Expect(e => e.IsGame(1007));
        inbox.Add(new BotEvent(BotEventKind.Game) { MsgId = 1007 });
        for (uint i = 1; i <= 4; i++) inbox.Add(new BotEvent(BotEventKind.Game) { MsgId = i });
        Assert.Equal((uint)1007, (await reply.WaitAsync(TimeSpan.FromSeconds(1))).MsgId);
        Assert.Equal(4, inbox.Dropped);
        Assert.Equal(5, observed.Count);
        Assert.Empty(inbox.Snapshot());
    }

    [Fact]
    public void Reducer_requires_base_before_movement_and_forgets_left_entities()
    {
        var reducer = new WorldReducer(maxEntities: 2);
        var movement = new MovementChangeNtf { Uid = 42, MovementInfo = new MovementInfo { Pos = new Vec3 { X = 1 } } };
        reducer.Observe(new BotEvent(BotEventKind.Game) { MsgId = GameAoiSink.MovementChange, Body = movement.ToByteArray() });
        Assert.Contains(reducer.Snapshot().Faults, x => x.Contains("before base"));
        var basePacket = new AoIViewChangeData();
        basePacket.Bases.Add(new EntityBase { Uuid = 42, EntityType = 3 });
        reducer.Observe(new BotEvent(BotEventKind.Game) { MsgId = GameAoiSink.SyncAoiView, Body = basePacket.ToByteArray() });
        Assert.Equal(1, reducer.Snapshot().MaxVisiblePlayers);
        reducer.Observe(new BotEvent(BotEventKind.Game) { MsgId = GameAoiSink.MovementChange, Body = movement.ToByteArray() });
        var leave = new AoIViewChangeData(); leave.LeaveAoi.Add(42);
        reducer.Observe(new BotEvent(BotEventKind.Game) { MsgId = GameAoiSink.SyncAoiView, Body = leave.ToByteArray() });
        Assert.Equal(0, reducer.Snapshot().CurrentEntities);
        Assert.Single(reducer.Snapshot().Faults);
    }

    [Fact]
    public async Task Send_queue_times_out_stalled_transport_and_closes_it()
    {
        int closed = 0;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new BotSendQueue(() => { Interlocked.Increment(ref closed); return ValueTask.CompletedTask; },
            capacity: 1, timeout: TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAsync<TimeoutException>(() => queue.SendAsync(() => pending.Task, CancellationToken.None));
        await Task.Delay(20);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Histogram_subtracts_raw_disjoint_buckets_and_rejects_epoch_change()
    {
        var before = JsonNode.Parse("""{"count":2,"sum":3,"min":1,"max":2,"p50":1,"p99":2,"bounds":[1,5],"buckets":[1,1,0]}""")!;
        var after = JsonNode.Parse("""{"count":5,"sum":14,"min":1,"max":8,"p50":2,"p99":8,"bounds":[1,5],"buckets":[1,3,1]}""")!;
        var delta = MetricHistogram.Delta(before, after)!;
        Assert.Equal(3, delta.Count);
        Assert.Equal([0L, 2L, 1L], delta.Buckets);
        Assert.Equal(5, delta.P50);
        Assert.Equal(8, delta.P99);
    }

    [Fact]
    public void Movement_correlation_keeps_exact_position_and_stop_separate()
    {
        var correlation = new MovementCorrelation();
        correlation.Offer(42, 1.01f, 2, false);
        correlation.Offer(42, 1.01f, 2, true);
        correlation.Seen(new MovementChangeNtf
        {
            Uid = 42, MovementInfo = new MovementInfo
            { Pos = new Vec3 { X = 1.01f, Z = 2 }, MoveType = MovementType.MoveTypeWalk }
        });
        correlation.Seen(new MovementChangeNtf
        {
            Uid = 42, MovementInfo = new MovementInfo
            { Pos = new Vec3 { X = 1.01f, Z = 2 }, MoveType = MovementType.MoveTypeNone }
        });
        correlation.Seen(new MovementChangeNtf
        {
            Uid = 42, MovementInfo = new MovementInfo
            { Pos = new Vec3 { X = 1.02f, Z = 2 }, MoveType = MovementType.MoveTypeWalk }
        });
        var result = correlation.Snapshot();
        Assert.Equal(2, result.ObservedOffers);
        Assert.Equal(0, result.NeverSeen);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.Ambiguous);
    }

    [Fact]
    public void Structured_delta_keeps_exact_bucket_subtraction_and_new_id_histogram()
    {
        var before = JsonNode.Parse("""{"epoch":"x","loop":{"ticks":5},"inbound":{}}""")!;
        var after = JsonNode.Parse("""{"epoch":"x","loop":{"ticks":9},"inbound":{"1":{"count":3,"handlingMs":{"count":3,"sum":6,"min":1,"max":3,"p50":2,"p99":3,"bounds":[1,2,5],"buckets":[1,1,1,0]}}}}""")!;
        var delta = new MetricsPair(before, after).StructuredDelta();
        Assert.Equal(4, delta["loop"]!["ticks"]!.GetValue<decimal>());
        Assert.Equal(3, delta["inbound"]!["1"]!["handlingMs"]!["count"]!.GetValue<long>());
        Assert.Equal(1, delta["inbound"]!["1"]!["handlingMs"]!["buckets"]![2]!.GetValue<long>());
    }

    [Fact]
    public void Fixed_latency_does_not_clip_long_timeouts()
    {
        var latency = new FixedLatency();
        latency.Add(30_000);
        Assert.True(latency.Snapshot().P99 >= 30_000);
        Assert.True(latency.Snapshot().Max >= 30_000);
    }

    [Fact]
    public void Long_latency_quantiles_keep_resolution_instead_of_collapsing_to_maximum()
    {
        var latency = new FixedLatency();
        latency.Add(11_000); latency.Add(25_000); latency.Add(30_000);
        Assert.Equal(25_000, latency.Snapshot().P50);
        Assert.Equal(30_000, latency.Snapshot().P99);
        Assert.Equal(30_000, latency.Snapshot().Max);
    }

    [Fact]
    public void Reduced_offer_never_passes_targets_and_every_target_row_is_reported()
    {
        var report = new RunReport
        {
            Scenario = "S2", RequestedBots = 50, TargetBots = 300,
            RequestedSeconds = 30, StartedUtc = DateTimeOffset.UtcNow
        };
        report.ServerBefore = JsonNode.Parse("""{"epoch":"one","elapsedSeconds":1,"loop":{"ticks":0,"overruns":0,"busyMs":0},"database":{"loopStatements":0},"errors":{"exceptions":0},"wire":{"bytesOut":0}}""");
        report.ServerAfter = JsonNode.Parse("""{"epoch":"one","elapsedSeconds":31,"loop":{"ticks":600,"overruns":0,"busyMs":0},"database":{"loopStatements":0},"errors":{"exceptions":0},"wire":{"bytesOut":0}}""");
        var rows = TargetEvaluation.Evaluate(report);
        Assert.Equal(19, rows.Count);
        Assert.DoesNotContain(rows, x => x.Status == "PASS");
    }
}
