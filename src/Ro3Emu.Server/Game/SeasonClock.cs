using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MultiverseStage times, anchored only to SeasonService.Start. Lua_GameDefine.lua:7788-7805 and
/// LC_SeasonHelper.ConvertConfigTimeStamp prove 3 = season start, 5 = break, 9 = referenced stage end.
/// Types 7/8 have no enum or converter branch in the supplied Lua: the weekday-aligned and duration
/// interpretations below are (inferred), not recovered original-server algorithms. <paramref name="start"/> is a civil
/// time in the realm <paramref name="zone"/> (SeasonService.Zone), which also aligns type 7 to its weekday.
/// </summary>
public sealed class SeasonClock(GameData data, DateTime start, int playBook, TimeZoneInfo zone)
{
    private readonly HashSet<(long, bool)> _resolving = new();
    private readonly Dictionary<(long, bool), long> _resolved = new();

    public long StartTime => SeasonService.Unix(start, zone);

    /// <summary>
    /// (inferred) This single-server first season uses MultiverseMainStage's season-1/day-1 length (77 days).
    /// There is no cross-server global phase or matchmaking scheduler; dated config anchors are not used.
    /// </summary>
    public long BreakTime => StartTime + 86400 * data.Table("MultiverseMainStage").Values
        .Where(r => GameData.Int(r, "_iSeasonNum") == 1 && GameData.Int(r, "_iStartTime") == 1)
        .Select(r => GameData.Int(r, "_iMainStageLength")).FirstOrDefault();

    public SeasonTimeInfoNotify Build()
    {
        var notice = new SeasonTimeInfoNotify
        {
            SeasonStartTime = StartTime, SeasonBreakStartTime = BreakTime,
            ShowCountDownTime = BreakTime - Constant("MainStageCountDownTime") * 3600,
            ServerMatchUpCircle = Constant("ServerMatchUpCircle"),
            // No next match/season has been scheduled. Zero means absent, not a promised rollover (inferred).
        };
        foreach (var row in data.Table("MultiverseStage").Values.Where(r => GameData.Int(r, "_iMultiverseID") == playBook)
                     .OrderBy(r => GameData.Int(r, "_iPlace")))
        {
            long id = GameData.Int(row, "_iID");
            var stage = new SeasonStageTimeInfo { StageId = id, StartTime = Resolve(id, false), EndTime = Resolve(id, true) };
            if (stage.EndTime <= stage.StartTime) throw new InvalidDataException($"Invalid season stage interval {id}");
            notice.StageTimeInfo[id] = stage;
        }
        return notice;
    }

    private long Constant(string name) => data.Table("MultiverseConstant").TryGetValue(name, out var value)
        ? (long)(GameData.Number(value) ?? 0) : 0;

    private long Resolve(long stage, bool end)
    {
        var key = (stage, end);
        if (_resolved.TryGetValue(key, out long value)) return value;
        if (!_resolving.Add(key)) throw new InvalidDataException($"Cyclic season stage time {stage}/{end}");
        var row = data.Row("MultiverseStage", stage) ?? throw new InvalidDataException($"Missing season stage {stage}");
        if (GameData.Int(row, "_iMultiverseID") != playBook) throw new InvalidDataException("Cross-playbook stage reference");
        var spec = row.GetProperty(end ? "_iEndTime" : "_iOpenTime").EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        value = spec[0] switch
        {
            3 when spec.Length == 2 => StartTime + spec[1],
            5 when spec.Length == 2 => BreakTime + spec[1],
            // (inferred) 7: referenced stage start + duration, rounded forward to weekday (1 = Monday).
            7 when spec.Length == 4 => WeekdayAtOrAfter(Resolve(spec[1], false) + spec[2], (int)spec[3]),
            // (inferred) 8: referenced stage start + duration; this client's row references itself.
            8 when spec.Length == 3 => Resolve(spec[1], false) + spec[2],
            9 when spec.Length == 3 => Resolve(spec[1], true) + spec[2],
            _ => throw new InvalidDataException($"Unsupported season time type {spec[0]}")
        };
        _resolving.Remove(key);
        _resolved[key] = value;
        return value;
    }

    private long WeekdayAtOrAfter(long timestamp, int weekday)
    {
        if (weekday is < 1 or > 7) throw new InvalidDataException("Invalid season weekday");
        var local = SeasonService.Civil(timestamp, zone);
        int current = (int)local.DayOfWeek == 0 ? 7 : (int)local.DayOfWeek;
        return SeasonService.Unix(local.AddDays((weekday - current + 7) % 7), zone);
    }
}
