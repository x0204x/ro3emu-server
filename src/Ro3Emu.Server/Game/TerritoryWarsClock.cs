using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// TerritoryWarStageConfig -> TimeLimitConfig, using the precedence in LC_TimeLimitHelper.lua:299-412.
/// Absolute Greenwich dates remain absolute; server-opening and season-stage offsets use SeasonService.Start
/// and its resolved playbook stage clock. In particular, this export's May dates are not moved to today's date.
/// </summary>
public sealed class TerritoryWarsClock(GameData data, SeasonService seasons, TimeProvider time)
{
    public sealed record Stage(int Id, int Type, int Round, int ResultRule, long StartTime, long EndTime);
    public long Now => time.GetUtcNow().ToUnixTimeSeconds();
    public long SeasonStart => seasons.StartTime;
    public int PlayBook => seasons.PlayBook;
    private IReadOnlyList<Stage>? _exported;
    private IReadOnlyList<Stage>? _stages;
    private long _builtCycle = -1;

    /// <summary>
    /// The block of battles in force: the exported dates, or under OpenContent the repeat that is running or comes next
    /// (the block shifted by Cycle × TerritoryWarCycleDays).
    /// </summary>
    public IReadOnlyList<Stage> Stages
    {
        get
        {
            long cycle = Cycle;
            if (_stages == null || _builtCycle != cycle)
            {
                long shift = cycle * seasons.OpenContent.TerritoryWarPeriod;
                _stages = (_exported ??= Build()).Select(s => s with { StartTime = s.StartTime + shift, EndTime = s.EndTime + shift }).ToArray();
                _builtCycle = cycle;
            }
            return _stages;
        }
    }

    /// <summary>0 for the exported block; under OpenContent the repeat running now, or the next one once a block ended.</summary>
    public long Cycle
    {
        get
        {
            long period = seasons.OpenContent.TerritoryWarPeriod;
            var block = _exported ??= Build();
            if (period <= 0 || block.Count == 0) return 0;
            long first = block[0].StartTime, span = block.Max(s => s.EndTime) - first, now = Now;
            if (now < first + span) return 0;
            long cycle = (now - first) / period;
            return now < first + cycle * period + span ? cycle : cycle + 1;
        }
    }

    /// <summary>
    /// The territory season: honor and valor archive, award and settlement keys. Cycle 0 keeps the original key, so
    /// existing state and tests are unchanged; every repeat under OpenContent is a season of its own.
    /// </summary>
    public string Epoch => EpochOf(SeasonStart, PlayBook, Cycle);

    /// <summary>"season:playbook", plus ":cycle" for a repeat of the war block (TerritoryEscortService keys the same way).</summary>
    public static string EpochOf(long seasonStart, int playBook, long cycle) =>
        cycle > 0 ? $"{seasonStart}:{playBook}:{cycle}" : $"{seasonStart}:{playBook}";

    public Stage Current => At(Now);

    /// <summary>Start inclusive, end exclusive (inferred boundary convention, matching SeasonClock).</summary>
    public Stage At(long seconds)
    {
        if (PlayBook != 101 || Stages.Count == 0) return new(0, 99, 0, 0, 0, 0);
        var current = Stages.LastOrDefault(s => seconds >= s.StartTime && seconds < s.EndTime);
        if (current != null) return current;
        var prior = Stages.LastOrDefault(s => seconds >= s.StartTime);
        var next = Stages.FirstOrDefault(s => seconds < s.StartTime);
        return new(0, next == null ? 99 : 0, prior?.Round ?? next?.Round ?? 0, 0,
            prior?.EndTime ?? SeasonStart, next?.StartTime ?? Stages[^1].EndTime);
    }

    public Stage? RoundStage(int round, params int[] types) => Stages.FirstOrDefault(s => s.Round == round && types.Contains(s.Type));

    private IReadOnlyList<Stage> Build()
    {
        var result = new List<Stage>();
        foreach (var row in data.Table("TerritoryWarStageConfig").Values.OrderBy(r => GameData.Int(r, "_iStageID")))
        {
            int id = (int)GameData.Int(row, "_iStageID");
            long limit = GameData.Int(row, "_iTimeLimitID");
            foreach (var window in data.Table("TimeLimitConfig").Values.Where(r => GameData.Int(r, "_iTimeLimitId") == limit))
            {
                long start = Resolve(window, false), end = Resolve(window, true);
                if (start <= 0 || end <= start) throw new InvalidDataException($"Invalid territory stage {id} time limit {limit}");
                result.Add(new(id, (int)GameData.Int(row, "_iStageType"), (id - 1) / 8 + 1,
                    (int)GameData.Int(row, "_iResultRule"), start, end));
            }
        }
        return result.OrderBy(s => s.StartTime).ThenBy(s => s.Id).ToArray();
    }

    internal long Resolve(JsonElement row, bool end)
    {
        string side = end ? "End" : "Start";
        if (GameData.Int(row, "_iGreenWich" + side) is > 0 and var absolute) return absolute;
        if (GameData.Int(row, "_iServerOpen" + side) is > 0 and var open) return SeasonStart + open;
        if (row.TryGetProperty("_iSeason" + side + "StageInfo", out var field) && field.ValueKind == JsonValueKind.String
            && field.GetString()?.Split('|') is { Length: 2 } spec && int.TryParse(spec[0], out int edge)
            && long.TryParse(spec[1], out long stage))
        {
            var times = seasons.Times().StageTimeInfo.Values.OrderBy(s => s.StartTime).ToArray();
            var chosen = stage == 0 ? times.FirstOrDefault() : stage == -1 ? times.LastOrDefault()
                : times.FirstOrDefault(s => s.StageId == stage);
            if (chosen != null && edge is 1 or 2)
                return (edge == 1 ? chosen.StartTime : chosen.EndTime) + GameData.Int(row, "_iSeason" + side + "StateInfoTime");
        }
        return end ? long.MaxValue : SeasonStart;
    }
}
