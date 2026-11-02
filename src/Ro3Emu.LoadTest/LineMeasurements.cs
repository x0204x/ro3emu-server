using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Ro3Emu.LoadTest;

/// <summary>Deterministic per-bot workload; random phase and every interval are independent of other bots.</summary>
public static class WalkSchedule
{
    public static double[] DueSeconds(int seconds, int index, string phase)
    {
        var due = new List<double>();
        var random = new Random(1729 + index);
        double at = phase == "sync" ? index * .0005 : random.NextDouble();
        while (at < seconds)
        {
            due.Add(at);
            at += phase == "sync" ? .75 : .5 + random.NextDouble() * .5;
        }
        return [.. due];
    }
}

public sealed record LinePopulation(long Map, long SceneId, int Line, string Kind, int Players, int Loading,
    int? SoftLimit, int? HardLimit, DateTimeOffset CapturedUtc);

public sealed class LineMeasurements
{
    private readonly ConcurrentQueue<LinePopulation> _samples = new();
    public LinePopulation[] Snapshot()
    {
        // ToArray, not a spread: the samplers may still add, and a spread sizes its array from Count first.
        var snapshot = _samples.ToArray();
        return snapshot;
    }

    public void Observe(JsonNode metrics)
    {
        if (metrics["sessions"]?["publicLines"] is JsonArray publicLines)
            foreach (var line in publicLines)
            {
                if (line == null) continue;
                _samples.Enqueue(new(line["map"]!.GetValue<long>(), line["sceneId"]!.GetValue<long>(),
                    line["line"]!.GetValue<int>(), "PublicLine", line["players"]!.GetValue<int>(),
                    line["reservations"]!.GetValue<int>(), line["softLimit"]!.GetValue<int>(),
                    line["hardLimit"]!.GetValue<int>(),
                    metrics["capturedUtc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow));
            }
        if (metrics["sessions"]?["byMap"] is not JsonArray maps) return;
        foreach (var map in maps)
        {
            if (map == null || map["kind"] == null || map["sceneId"] == null) continue;
            long entry = map["map"]!.GetValue<long>();
            string kind = map["kind"]!.GetValue<string>();
            if (kind == "PublicLine") continue; // Provisional role-select labels are not admission claims.
            _samples.Enqueue(new(entry, map["sceneId"]!.GetValue<long>(), map["line"]!.GetValue<int>(), kind,
                map["count"]!.GetValue<int>(), map["loading"]?.GetValue<int>() ?? 0, null, null,
                metrics["capturedUtc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow));
        }
    }
}

public sealed partial class RunReport
{
    public string WalkPhase { get; set; } = "realistic";
    public long MovementWithoutViewer { get; set; }
    public long ChatWithoutPeer { get; set; }
    public LinePopulation[] LineSamples { get; set; } = [];

    public IEnumerable<string> LineMarkdownLines()
    {
        yield return "";
        yield return $"Walk phase: **{WalkPhase}**; realistic = independent initial phase [0,1)s and intervals [0.5,1)s; sync = legacy 750 ms waves with 0.5 ms/bot offset.";
        yield return $"Movement proofs: {MovementObservedOffers}/{MovementOffered} paired offers observed; {MovementWithoutViewer} additional walks had no same-runtime AOI viewer (proof NOT RUN, not a timeout). Local chat without same-runtime peer: {ChatWithoutPeer}.";
        yield return "";
        yield return "## Runtime lines";
        yield return "";
        yield return "Public counts come from the admission manager: occupied players and outstanding reservations. Private counts include loading sessions. Peaks are sampled HTTP gauges (opening, every 5 s while traffic runs, closing), not a continuous capacity audit. Soft = auto-split threshold; hard = configured admission cap.";
        yield return "";
        yield return "| Map | Kind | Line | Runtime scene | Latest server players | Peak players + reservations | Latest client players | Soft / hard | Samples |";
        yield return "|---|---|---:|---:|---:|---:|---:|---|---:|";
        foreach (var group in LineSamples.GroupBy(x => (x.Map, x.Kind, x.Line, x.SceneId)).OrderBy(x => x.Key.Map).ThenBy(x => x.Key.SceneId))
        {
            var last = group.Last();
            int client = Inbound.Values.Count(x => x.Location.Ready && x.Location.SceneEntry == last.Map && x.Location.SceneId == last.SceneId && x.Location.LineNumber == last.Line);
            yield return $"| {last.Map} | {last.Kind} | {last.Line} | {last.SceneId} | {last.Players} | {group.Max(x => x.Players + (x.Kind == "PublicLine" ? x.Loading : 0))} | {client} | {last.SoftLimit?.ToString() ?? "n/a"} / {last.HardLimit?.ToString() ?? "n/a"} | {group.Count()} |";
        }
    }
}
