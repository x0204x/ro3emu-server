using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ro3Emu.LoadTest;

public sealed record MetricHistogram(long Count, double Sum, double Min, double Max, double P50, double P99,
    double[] Bounds, long[] Buckets)
{
    public static MetricHistogram? Delta(JsonNode? before, JsonNode? after)
    {
        if (after is null) return null;
        var b = Values(after);
        var a = before is null ? new MetricHistogram(0, 0, 0, 0, 0, 0, b.Bounds, new long[b.Buckets.Length]) : Values(before);
        if (!a.Bounds.SequenceEqual(b.Bounds) || a.Buckets.Length != b.Buckets.Length) return null;
        var buckets = b.Buckets.Zip(a.Buckets, (y, x) => y - x).ToArray();
        if (buckets.Any(x => x < 0) || b.Count < a.Count) return null;
        long count = b.Count - a.Count;
        double Quantile(double p)
        {
            if (count == 0) return 0;
            long target = (long)Math.Ceiling(count * p), current = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                current += buckets[i];
                if (current >= target) return i < b.Bounds.Length ? Math.Min(b.Bounds[i], b.Max) : b.Max;
            }
            return b.Max;
        }
        double freshMax = count == 0 ? 0 : a.Count == 0 || b.Max > a.Max ? b.Max : double.NaN;
        return new(count, b.Sum - a.Sum, count == 0 ? 0 : b.Min, freshMax,
            Quantile(.5), Quantile(.99), b.Bounds, buckets);
    }

    private static MetricHistogram Values(JsonNode node) => new(
        node["count"]?.GetValue<long>() ?? 0, node["sum"]?.GetValue<double>() ?? 0,
        node["min"]?.GetValue<double>() ?? 0, node["max"]?.GetValue<double>() ?? 0,
        node["p50"]?.GetValue<double>() ?? 0, node["p99"]?.GetValue<double>() ?? 0,
        node["bounds"]?.AsArray().Select(x => x!.GetValue<double>()).ToArray() ?? [],
        node["buckets"]?.AsArray().Select(x => x!.GetValue<long>()).ToArray() ?? []);
}

public sealed class MetricsPair(JsonNode before, JsonNode after)
{
    public JsonNode Before { get; } = before; public JsonNode After { get; } = after; public bool SameEpoch => Before["epoch"]?.ToString() == After["epoch"]?.ToString();
    public double Seconds => (After["elapsedSeconds"]?.GetValue<double>() ?? 0) -
                             (Before["elapsedSeconds"]?.GetValue<double>() ?? 0);
    public double Delta(string path) => Number(After, path) - Number(Before, path);
    public MetricHistogram? Histogram(string path) => MetricHistogram.Delta(At(Before, path), At(After, path));
    public static JsonNode? At(JsonNode? root, string path)
    {
        foreach (var part in path.Split('.')) root = root?[part];
        return root;
    }
    private static double Number(JsonNode root, string path) => At(root, path)?.GetValue<double>() ?? 0;
    public Dictionary<string, long> ServerAdmissions() =>
        (After["inbound"] as JsonObject ?? new JsonObject()).ToDictionary(k => k.Key,
            k => Math.Max(0, (long)(k.Value?["count"]?.GetValue<long>() ?? 0) -
                             (long)(Before["inbound"]?[k.Key]?["count"]?.GetValue<long>() ?? 0)));
    public JsonNode StructuredDelta() => DiffNode(Before, After)!;

    private static JsonNode? DiffNode(JsonNode? before, JsonNode? after)
    {
        if (after is JsonObject obj)
        {
            if (obj.ContainsKey("buckets") && obj.ContainsKey("bounds"))
            {
                var hist = MetricHistogram.Delta(before, after);
                if (hist == null) return null;
                return new JsonObject
                {
                    ["count"] = hist.Count, ["sum"] = hist.Sum, ["min"] = null,
                    ["max"] = double.IsNaN(hist.Max) ? null : hist.Max,
                    ["p50"] = hist.P50, ["p99"] = hist.P99,
                    ["bounds"] = new JsonArray(hist.Bounds.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                    ["buckets"] = new JsonArray(hist.Buckets.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
                };
            }
            var result = new JsonObject();
            foreach (var (key, value) in obj)
                result[key] = key is "schemaVersion" or "epoch" or "capturedUtc" ? value?.DeepClone() :
                    DiffNode(before?[key], value);
            return result;
        }
        if (after is JsonArray) return after.DeepClone(); // session byMap is a current-state list.
        if (after is JsonValue && after.GetValueKind() == JsonValueKind.Number &&
            decimal.TryParse(after.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
        {
            decimal b = before is JsonValue && before.GetValueKind() == JsonValueKind.Number &&
                decimal.TryParse(before.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var old)
                ? old : 0;
            return JsonValue.Create(a - b);
        }
        return after?.DeepClone();
    }
}

public sealed class ActionCounters
{
    private readonly ConcurrentDictionary<string, ActionTally> _actions = new();
    private readonly ConcurrentDictionary<uint, ActionTally> _messages = new();
    public ActionTally Action(string name) => _actions.GetOrAdd(name, _ => new());
    public ActionTally Message(uint id) => _messages.GetOrAdd(id, _ => new());
    public bool TryMessage(uint id, out ActionTally? tally) => _messages.TryGetValue(id, out tally);
    public Dictionary<string, ActionResult> Actions() => _actions.ToDictionary(k => k.Key, v => v.Value.Snapshot());
    public Dictionary<uint, ActionResult> Messages() => _messages.ToDictionary(k => k.Key, v => v.Value.Snapshot());
}

public sealed class ActionTally
{
    private long _planned, _offered, _completed, _refused, _timedOut, _failed;
    private readonly FixedLatency _latency = new();
    public void Plan() => Interlocked.Increment(ref _planned);
    public void Offer() => Interlocked.Increment(ref _offered);
    public void Complete(double ms)
    {
        Interlocked.Increment(ref _completed);
        _latency.Add(ms);
    }
    public void Refuse() => Interlocked.Increment(ref _refused);
    public void Timeout() => Interlocked.Increment(ref _timedOut);
    public void Fail() => Interlocked.Increment(ref _failed);
    public void RecordLatency(double ms) => _latency.Add(ms);
    public ActionResult Snapshot() => new(Interlocked.Read(ref _planned), Interlocked.Read(ref _offered),
        Interlocked.Read(ref _completed), Interlocked.Read(ref _refused), Interlocked.Read(ref _timedOut),
        Interlocked.Read(ref _failed), _latency.Snapshot());
}

/// <summary>An offered operation must have exactly one terminal outcome, including cancellation.</summary>
public sealed class ActionAttempt(ActionTally tally) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _finished;
    public void Complete() { if (Interlocked.Exchange(ref _finished, 1) == 0) tally.Complete(Stopwatch.GetElapsedTime(_started).TotalMilliseconds); }
    public void Refuse() { if (Interlocked.Exchange(ref _finished, 1) == 0) tally.Refuse(); }
    public void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        if (error is Ro3Emu.Bot.BotRefusedException) tally.Refuse();
        else if (error is TimeoutException or OperationCanceledException) tally.Timeout(); else tally.Fail();
    }
    // Abandoning an admitted operation at the run deadline is an observed timeout, never a success.
    public void Dispose() { if (Interlocked.Exchange(ref _finished, 1) == 0) tally.Timeout(); }
}

/// <summary>Fixed 1 ms buckets to 10 s, 50 ms to 60 s, 500 ms to 10 min, then overflow.</summary>
public sealed class FixedLatency
{
    private readonly long[] _buckets = new long[12082];
    private long _maxMicroseconds;
    public void Add(double ms)
    {
        int bucket = ms <= 10000 ? Math.Max(0, (int)Math.Ceiling(ms)) : ms <= 60000
            ? 10000 + (int)Math.Ceiling((ms - 10000) / 50) : ms <= 600000
            ? 11000 + (int)Math.Ceiling((ms - 60000) / 500) : 12081;
        Interlocked.Increment(ref _buckets[bucket]);
        long value = (long)Math.Ceiling(ms * 1000);
        long old;
        do { old = Interlocked.Read(ref _maxMicroseconds); if (value <= old) break; }
        while (Interlocked.CompareExchange(ref _maxMicroseconds, value, old) != old);
    }
    public LatencySummary Snapshot() => LatencySummary.FromBuckets(_buckets, Interlocked.Read(ref _maxMicroseconds) / 1000d);
    internal static double UpperBound(int bucket, double maximum) => Math.Min(maximum,
        bucket <= 10000 ? bucket : bucket <= 11000 ? 10000 + (bucket - 10000) * 50 :
        bucket <= 12080 ? 60000 + (bucket - 11000) * 500 : maximum);
}
public sealed record ActionResult(long Planned, long Offered, long Completed, long Refused, long TimedOut,
    long Failed, LatencySummary Latency, long? Admitted = null);

public sealed record LatencySummary(long Count, double P50, double P99, double Max)
{
    public static LatencySummary FromBuckets(long[] source, double? exactMax = null)
    {
        var buckets = new long[source.Length];
        for (int i = 0; i < source.Length; i++) buckets[i] = Interlocked.Read(ref source[i]);
        long total = buckets.Sum();
        if (total == 0) return new(0, 0, 0, 0);
        double At(double fraction)
        {
            long position = (long)Math.Ceiling(total * fraction), seen = 0;
            for (int i = 0; i < buckets.Length; i++) { seen += buckets[i]; if (seen >= position) return FixedLatency.UpperBound(i, exactMax ?? double.MaxValue); }
            return buckets.Length - 1;
        }
        int max = Array.FindLastIndex(buckets, x => x != 0);
        return new(total, At(.5), At(.99), exactMax ?? max);
    }
}

public sealed record TargetRow(string Area, string Metric, string Target, string Status, string Detail);

public sealed partial class RunReport
{
    public required string Scenario { get; init; }
    public required int TargetBots { get; init; }
    public required int RequestedBots { get; init; }
    public required double RequestedSeconds { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset EndedUtc { get; set; }
    public string Verdict { get; set; } = "NOT QUALIFIED";
    public string? AbortReason { get; set; }
    public string? ServerUrl { get; set; }
    public string? ServerVersion { get; set; }
    public string? ServerDataDir { get; set; }
    public string MeasurementPhase { get; set; } = "setup";
    public JsonNode? ServerBefore { get; set; }
    public JsonNode? ServerAfter { get; set; }
    public JsonNode? ServerDelta { get; set; }
    public Dictionary<string, ActionResult> Actions { get; set; } = [];
    public Dictionary<int, string> BotFailures { get; set; } = [];
    public Dictionary<uint, ActionResult> Messages { get; set; } = [];
    public Dictionary<string, long> Admissions { get; set; } = [];
    public Dictionary<string, ReducerSnapshot> Inbound { get; set; } = [];
    public Dictionary<string, double> InboundBytesPerSecond { get; set; } = [];
    public Dictionary<string, double> DecodedPayloadBytesPerSecond { get; set; } = [];
    public double[] QueueLengthSamples { get; set; } = [];
    public List<TargetRow> Targets { get; set; } = [];
    public string[] UnsupportedBehaviours { get; set; } = [];
    public string[] HeavyProcesses { get; set; } = [];
    public double? SystemCpuPercent { get; set; }
    public double SchedulingLagP99Ms { get; set; }
    public double MovementPeerP99Ms { get; set; }
    public LatencySummary MovementPeerLatency { get; set; } = new(0, 0, 0, 0);
    public long MovementUnmatched { get; set; }
    public long MovementNeverSeen { get; set; }
    public long MovementOffered { get; set; }
    public long MovementObservedOffers { get; set; }
    public long MovementAmbiguous { get; set; }
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public string ToMarkdown()
    {
        var lines = new List<string> { $"# {Scenario} — {Verdict}", "", $"Requested {RequestedBots}/{TargetBots} bots for {RequestedSeconds:F0}s; {StartedUtc:u}–{EndedUtc:u}.",
            $"Server: {ServerUrl}; measurement phase: {MeasurementPhase}; scheduling lag p99 {SchedulingLagP99Ms:F1} ms; system CPU {SystemCpuPercent?.ToString("F1") ?? "UNKNOWN"}%.", "" };
        if (AbortReason != null) lines.Add($"Abort: {AbortReason}");
        foreach (var failure in BotFailures.OrderBy(x => x.Key)) lines.Add($"Bot {failure.Key}: {failure.Value}");
        lines.Add($"Build: {ServerVersion ?? "UNKNOWN"}; data: {ServerDataDir ?? "UNKNOWN"}.");
        if (ServerDataDir != null) lines.Add($"Server log: [{Path.GetFileName(ServerDataDir)}.stdout.log]({Path.GetFileName(ServerDataDir)}.stdout.log).");
        if (UnsupportedBehaviours.Length > 0) lines.Add($"NOT RUN: {string.Join(", ", UnsupportedBehaviours)}");
        lines.AddRange(LineMarkdownLines());
        lines.AddRange(S3MarkdownLines());
        lines.AddRange(["", "| Area | Metric | Target | Status | Evidence |", "|---|---|---|---|---|"]);
        lines.AddRange(Targets.Select(x => $"| {x.Area} | {x.Metric} | {x.Target} | {x.Status} | {x.Detail.Replace('|', '/')} |"));
        lines.AddRange(["", "## Actions", "", "| Action | Planned | Offered | Admitted | Completed | Refused | Timed out | Failed | Client latency p50 / p99 / max ms |", "|---|---:|---:|---:|---:|---:|---:|---:|---|"]);
        lines.AddRange(Actions.OrderBy(x => x.Key).Select(x => $"| {x.Key} | {x.Value.Planned} | {x.Value.Offered} | {x.Value.Admitted?.ToString() ?? "UNKNOWN"} | {x.Value.Completed} | {x.Value.Refused} | {x.Value.TimedOut} | {x.Value.Failed} | {LatencyText(x.Value.Latency)} |"));
        lines.AddRange(["", "## Server admissions by message id", "", "| ID | Offered | Admitted | Server completed | Client expected-reply p50 / p99 / max ms |", "|---|---:|---:|---:|---|"]);
        lines.AddRange(Messages.OrderBy(x => x.Key).Select(x => $"| {x.Key} | {x.Value.Offered} | {x.Value.Admitted?.ToString() ?? "UNKNOWN"} | {x.Value.Completed} | {LatencyText(x.Value.Latency)} |"));
        if (InboundBytesPerSecond.Count > 0)
        {
            lines.AddRange(["", "## Inbound by bot", "", "| Bot | Bytes/s |", "|---|---:|"]);
            lines.AddRange(InboundBytesPerSecond.OrderBy(x => x.Key).Select(x => $"| {x.Key} | {x.Value:F0} |"));
        }
        if (HeavyProcesses.Length > 0) lines.AddRange(["", "Competing processes: " + string.Join(", ", HeavyProcesses)]);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
    private static string LatencyText(LatencySummary latency) => latency.Count == 0 ? "NOT MEASURED" :
        $"{latency.P50:F2} / {latency.P99:F2} / {latency.Max:F2} (n={latency.Count})";
}

public static class TargetEvaluation
{
    private static readonly (string Area, string Metric, string Target)[] Rows =
    [
        ("Game loop", "tick p50 / p99 / max", "≤5 / ≤20 / ≤50 ms"),
        ("Game loop", "overrun fraction", "<0.5%"),
        ("Game loop", "busy ratio", "≤60%"),
        ("Game loop", "queue p99 and growth", "<200 and no growth"),
        ("Latency", "request to wire p50 / p99", "≤10 / ≤50 ms"),
        ("Latency", "movement to viewer p99", "≤80 ms"),
        ("Login", "S1 completion and scene p99", "100% and ≤3 s"),
        ("Map change", "S4 scene p99", "≤2 s"),
        ("Visibility", "players per viewer", "≤149"),
        ("Network", "S2 outbound per player", "≤20 KB/s"),
        ("CPU", "S3 process average", "≤2 cores"),
        ("Memory", "S3 working set and drift", "≤2 GB and <5% over 30 min"),
        ("GC", "gen2 and pause p99", "≤1/min and ≤10 ms"),
        ("Database", "synchronous loop I/O", "zero"),
        ("Database", "write-behind p99", "≤500 ms"),
        ("Durability", "crash loss and economy ack", "≤1 s non-economy; economy committed"),
        ("Correctness", "exceptions / disconnects / desync", "0 / 0 / 0"),
        ("Correctness", "S3 ledger exactness", "exact"),
        ("Visibility", "players per public line", "≤configured cap")
    ];

    public static List<TargetRow> Evaluate(RunReport report)
    {
        var rows = Rows.Select(x => new TargetRow(x.Area, x.Metric, x.Target, "NOT MEASURED", "No qualifying evidence")).ToArray();
        if (report.ServerBefore == null || report.ServerAfter == null) return rows.ToList();
        var m = new MetricsPair(report.ServerBefore, report.ServerAfter);
        if (!m.SameEpoch || m.Seconds <= 0) return rows.ToList();
        int minimumSeconds = report.Scenario switch { "S1" or "S4" => 60, "S2" => 600, "S3" => 1800, _ => int.MaxValue };
        bool full = report.MeasurementPhase == "scenario" && report.RequestedBots == report.TargetBots && report.RequestedSeconds >= minimumSeconds && m.Seconds >= minimumSeconds * .98 &&
            report.AbortReason == null && report.SchedulingLagP99Ms <= 100 && report.UnsupportedBehaviours.Length == 0 &&
            report.Actions.Values.All(x => x.Planned <= x.Offered && x.Refused == 0 && x.TimedOut == 0 && x.Failed == 0) &&
            report.Messages.Values.All(x => x.Admitted >= x.Offered && x.Completed >= x.Offered);
        void Set(int i, bool ok, string detail) => rows[i] = rows[i] with { Status = ok ? full ? "PASS" : "NOT MEASURED" : "FAIL", Detail = detail };
        var tick = m.Histogram("loop.tickDurationMs");
        if (tick is { Count: > 0 })
        {
            double hz = m.Delta("loop.ticks") / m.Seconds;
            bool quantilesOk = tick.P50 <= 5 && tick.P99 <= 20 && hz >= 19 && hz <= 21;
            if (!quantilesOk || !double.IsNaN(tick.Max))
                Set(0, quantilesOk && tick.Max <= 50, $"n={tick.Count}, p50≤{tick.P50:F1}, p99≤{tick.P99:F1}, max={(double.IsNaN(tick.Max) ? "UNKNOWN" : tick.Max.ToString("F1"))} ms; cadence={hz:F2} ticks/s (required 19–21)");
        }
        double ticks = m.Delta("loop.ticks");
        if (ticks > 0) Set(1, m.Delta("loop.overruns") / ticks < .005, $"{m.Delta("loop.overruns")}/{ticks}");
        if (m.Seconds > 0) Set(2, m.Delta("loop.busyMs") / (m.Seconds * 1000) <= .6, $"{m.Delta("loop.busyMs") / (m.Seconds * 1000):P1}");
        var queue = m.Histogram("loop.queueLength");
        if (queue is { Count: > 0 })
        {
            if (queue.P99 >= 200) Set(3, false, $"p99≤{queue.P99:F0}");
            else if (report.QueueLengthSamples.Length >= 6)
            {
                int n = report.QueueLengthSamples.Length / 3;
                double early = report.QueueLengthSamples.Take(n).Average();
                double late = report.QueueLengthSamples.TakeLast(n).Average();
                Set(3, late <= early + 20, $"p99≤{queue.P99:F0}, early mean={early:F0}, late mean={late:F0}");
            }
        }
        // Server wire reply histograms are per-id. A complete aggregate would require bucket merge; show every supported id.
        var wire = (report.ServerAfter["inbound"] as JsonObject)?.Select(x => m.Histogram($"inbound.{x.Key}.wireReplyMs")).Where(x => x is { Count: > 0 }).ToArray();
        if (wire is { Length: > 0 })
        {
            bool violation = wire.Any(x => x!.P50 > 10 || x.P99 > 50);
            rows[4] = rows[4] with
            {
                Status = violation ? "FAIL" : "NOT MEASURED",
                Detail = $"{wire.Length} same-ID game reply IDs measured; worst p99≤{wire.Max(x => x!.P99):F1} ms; SDK/micro/deferred IDs unavailable"
            };
        }
        if (report.Scenario == "S2" && report.MovementPeerP99Ms > 0) Set(5,
            report.MovementPeerP99Ms <= 80 && report.MovementUnmatched == 0 && report.MovementNeverSeen == 0 && report.MovementAmbiguous == 0,
            $"same-runtime AOI pairs p99={report.MovementPeerP99Ms:F1} ms, unmatched seen={report.MovementUnmatched}, never seen paired offers={report.MovementNeverSeen}, ambiguous={report.MovementAmbiguous}; unpaired walks={report.MovementWithoutViewer}");
        if (rows[5].Status == "PASS" && report.MovementWithoutViewer > 0)
            rows[5] = rows[5] with { Status = "NOT MEASURED", Detail = rows[5].Detail + "; incomplete viewer-proof coverage" };
        if (report.Scenario == "S1" && report.Actions.TryGetValue("login", out var login) && login.Latency.Count > 0)
            Set(6, login.Completed == report.RequestedBots && login.Latency.P99 <= 3000, $"{login.Completed}/{report.RequestedBots}; client scene p99≤{login.Latency.P99:F1} ms");
        if (report.Scenario == "S4" && report.Actions.TryGetValue("map-hop", out var hop) && hop.Latency.Count > 0)
            Set(7, hop.Completed == Math.Min(100, report.RequestedBots / 2) && hop.Latency.P99 <= 2000,
                $"{hop.Completed}/{Math.Min(100, report.RequestedBots / 2)}; p99≤{hop.Latency.P99:F1} ms");
        bool visibilityCoverage = report.MeasurementPhase == "scenario" && report.RequestedBots == report.TargetBots &&
            report.Inbound.Count == report.RequestedBots && report.Inbound.Values.All(x => x.Location.Ready);
        if (report.Inbound.Count > 0)
        {
            int peak = report.Inbound.Values.Max(x => x.MaxVisiblePlayers);
            rows[8] = rows[8] with
            {
                Status = peak > 149 ? "FAIL" : visibilityCoverage ? "PASS" : "NOT MEASURED",
                Detail = $"continuous reducer peak={peak} other players; {report.Inbound.Count}/{report.RequestedBots} client reducers"
            };
        }
        var publicLines = report.LineSamples.Where(x => x.Kind == "PublicLine").ToArray();
        if (publicLines.Length > 0)
        {
            bool breach = publicLines.Any(x => x.HardLimit != null && x.Players + x.Loading > x.HardLimit);
            bool known = publicLines.All(x => x.HardLimit != null);
            rows[18] = rows[18] with
            {
                Status = breach ? "FAIL" : known && visibilityCoverage ? "PASS" : "NOT MEASURED",
                Detail = $"{publicLines.Length} admission-manager HTTP map/line samples; peak players+reservations={publicLines.Max(x => x.Players + x.Loading)}; " +
                    string.Join("; ", publicLines.GroupBy(x => x.Map).Select(g => $"map {g.Key}: soft {g.First().SoftLimit}, hard {g.First().HardLimit}")) +
                    "; sampled bounds only (see Runtime lines)"
            };
        }
        if (report.Scenario == "S2" && report.RequestedSeconds > 0 && report.RequestedBots > 0)
            Set(9, m.Delta("wire.bytesOut") / m.Seconds / report.RequestedBots <= 20 * 1024,
                $"{m.Delta("wire.bytesOut") / m.Seconds / report.RequestedBots:F0} bytes/player/s");
        if (report.Scenario == "S3") Set(10, m.Delta("runtime.processCpuSeconds") / m.Seconds <= 2,
            $"{m.Delta("runtime.processCpuSeconds") / m.Seconds:F2} cores");
        if (report.Scenario == "S3" && m.Seconds >= 1800)
        {
            double first = MetricsPair.At(report.ServerBefore, "runtime.workingSetBytes")?.GetValue<double>() ?? 0;
            double last = MetricsPair.At(report.ServerAfter, "runtime.workingSetBytes")?.GetValue<double>() ?? 0;
            if (first > 0) Set(11, last <= 2L * 1024 * 1024 * 1024 && (last - first) / first < .05, $"start={first:F0}, end={last:F0} bytes");
        }
        var pause = m.Histogram("runtime.pauseDurationMs");
        if (pause is { Count: > 0 }) Set(12, m.Delta("runtime.gen2") / m.Seconds * 60 <= 1 && pause.P99 <= 10,
            $"gen2/min={m.Delta("runtime.gen2") / m.Seconds * 60:F2}; pause p99≤{pause.P99:F1} ms");
        if (m.Delta("database.profileErrors") == 0) Set(13, m.Delta("database.loopStatements") == 0 && m.Delta("database.loopOpens") == 0,
            $"{m.Delta("database.loopStatements")} loop statements; {m.Delta("database.loopOpens")} loop opens");
        // The ledger can be evaluated independently of 30-minute performance qualification.
        if (report.Scenario == "S3") rows[17] = report.EvaluateLedgerRow();
        var faults = report.Inbound.Values.Sum(x => x.Faults.Length);
        Set(16, m.Delta("errors.exceptions") == 0 && faults == 0 && report.Actions.Values.All(x => x.Failed == 0),
            $"exceptions={m.Delta("errors.exceptions")}; reducer faults={faults}; action failures={report.Actions.Values.Sum(x => x.Failed)}; disconnect proof unavailable");
        // Claims requiring unavailable proof cannot pass even if the observable subset is green.
        if (rows[16].Status == "PASS") rows[16] = rows[16] with { Status = "NOT MEASURED" };
        return rows.ToList();
    }
    public static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
