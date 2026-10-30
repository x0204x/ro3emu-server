using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// TimeLimitConfig as consumed by LC_TimeLimitHelper.lua: absolute seconds, server-open offsets, stage
/// references (1=start, 2=end; stage 0=first, -1=last), and calendar repeat segments. The realm opening
/// anchor is SeasonService.Start. Civil calendar boundaries use the injected clock's local timezone
/// (inferred realm timezone; the client uses os.date('*t')). Intervals are [start,end), as season stages.
/// </summary>
public sealed class ActivityClock(GameData data, SeasonService seasons, TimeProvider clock, TestingSwitches? testing = null)
{
    public sealed record Window(string State, long Start, long End, long Cycle = 0)
    {
        public bool Contains(long seconds) => Start <= seconds && seconds < End;
    }

    public DateTimeOffset UtcNow => clock.GetUtcNow();
    public DateTimeOffset LocalNow => clock.GetLocalNow();
    public TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;
    public long ServerOpen => seasons.StartTime;
    /// <summary>
    /// The open end of a window: 2099-01-22, the value the client data itself uses as "permanent" (TimeLimitConfig
    /// 1010/1041/1088). The year 9999 broke the client's date formatting (Lua os.date on Windows past the year 3000).
    /// </summary>
    public const long Forever = 4072678912;

    /// <summary>Current and adjacent occurrences. Rows sharing an id remain separate named state segments.</summary>
    /// <summary>TimeLimitConfig rows of one id, in table order (indexed once: every Segments call scanned the table).</summary>
    private JsonElement[] LimitRows(long timeLimitId)
    {
        var table = data.Table("TimeLimitConfig");
        if (_limitRows == null || _limitRowsCount != table.Count) // tests add rows at runtime; production data is fixed
        {
            _limitRows = table.Values.GroupBy(r => GameData.Int(r, "_iTimeLimitId")).ToDictionary(g => g.Key, g => g.ToArray());
            _limitRowsCount = table.Count;
        }
        return _limitRows.TryGetValue(timeLimitId, out var rows) ? rows : [];
    }

    private Dictionary<long, JsonElement[]>? _limitRows;
    private int _limitRowsCount;

    /// <summary>GM test aid windows that stand in for a TimeLimitConfig id's own rows (PronteraDefenseService's !defense).</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<long, Window> TestWindows { get; } = new();

    public IReadOnlyList<Window> Segments(long timeLimitId, long now, int playbook = 101)
    {
        if (!TestWindows.IsEmpty && TestWindows.TryGetValue(timeLimitId, out var test)) return [test];
        if (testing is { AlwaysOpen: true } && Repeated(timeLimitId, now) is { } repeated) return repeated;
        var windows = new List<Window>();
        foreach (var row in LimitRows(timeLimitId))
        {
            long begin = Bound(row, true, playbook), end = Bound(row, false, playbook);
            if (begin <= 0 || end <= begin) continue;
            string state = Text(row, "_strStateName");
            if (state.Length == 0) state = "Start";
            string? unit = new[] { "Month", "Week", "Day", "Hour" }
                .FirstOrDefault(u => GameData.Int(row, $"_i{u}Start") > 0 || GameData.Int(row, $"_i{u}End") > 0);
            if (unit == null)
            {
                windows.Add(new(state, begin, end));
                continue;
            }
            long repeat = unit == "Week" ? Math.Max(1, GameData.Int(row, "_iWeekRefreshCount"))
                : unit == "Month" ? Math.Max(1, GameData.Int(row, "_iMonthRefreshCount")) : 1;
            long period = unit switch { "Hour" => 3600, "Day" => 86400, "Week" => repeat * 604800, _ => 0 };
            long anchor = Ceiling(begin, unit, now);
            long cycle = unit == "Month" ? Math.Max(0, MonthsBetween(anchor, now) / repeat)
                : Math.Max(0, (now - anchor) / period);
            long count = GameData.Int(row, "_iCycleCount");
            // LC_TimeLimitHelper: count 0 is one occurrence, -1 repeats through the upper bound.
            long limit = count == 0 ? 1 : count;
            for (long index = Math.Max(0, cycle - 1); index <= cycle + 1; index++)
            {
                if (limit > 0 && index >= limit) continue;
                long startOfCycle = unit == "Month" ? ToUnix(Local(anchor).DateTime.AddMonths(checked((int)(index * repeat))))
                    : anchor + index * period;
                long offset = GameData.Int(row, $"_i{unit}Start"), finish = GameData.Int(row, $"_i{unit}End");
                if (finish <= 0) finish = unit == "Month" ? repeat * 30 * 86400 : period;
                long start = startOfCycle + offset, stop = startOfCycle + finish;
                if (start >= begin && stop <= end && stop > start) windows.Add(new(state, start, stop, index));
            }
        }
        return windows.OrderBy(w => w.Start).ThenBy(w => w.End).ToArray();
    }

    /// <summary>
    /// Testing AlwaysOpen (TEST AID, not original): the phases of a To-Do control id (DailyActivityConfig._iTimeCtrlId of a
    /// row TestingSwitches opens) repeat back to back through the day (TestingSwitches.Slots), each run with the rows'
    /// own state names and lengths and its start as the cycle. Null for any other id, which keeps its schedule.
    /// </summary>
    private Window[]? Repeated(long timeLimitId, long now)
    {
        if (!TodoControls().Contains(timeLimitId) || Chain(timeLimitId) is not { Length: > 0 } chain) return null;
        long first = chain[0].Start, length = chain[^1].End - first;
        return [.. TestingSwitches.Slots(now, length, fullOnly: true, clock.LocalTimeZone)
            .SelectMany(slot => chain.Select(phase => new Window(phase.State, slot.Start + phase.Start - first,
                slot.Start + phase.End - first, slot.Start)))];
    }

    private HashSet<long>? _todoControls;

    private HashSet<long> TodoControls() => _todoControls ??= [.. DailyActivityConfig.All(data)
        .Where(row => row.TimeCtrlId > 0 && !TestingSwitches.OwnCalendar.Contains((int)row.Id))
        .Select(row => row.TimeCtrlId)];

    /// <summary>The first run of a weekly id's rows that follow each other without a gap (1084 has three runs a day, each
    /// Matching then Battling; the others have one): week offsets in seconds.</summary>
    private (string State, long Start, long End)[] Chain(long timeLimitId)
    {
        TimeLimitConfig[] rows = [.. LimitRows(timeLimitId).Select(row => new TimeLimitConfig(row))];
        if (rows.Length == 0 || rows.Any(row => row.WeekEnd <= row.WeekStart)) return [];
        var chain = new List<(string State, long Start, long End)>();
        foreach (var row in rows.OrderBy(row => row.WeekStart))
        {
            if (chain.Count > 0 && row.WeekStart != chain[^1].End) break;
            chain.Add((row.TrStateName is { Length: > 0 } state ? state : "Start", row.WeekStart, row.WeekEnd));
        }
        return [.. chain];
    }

    /// <summary>
    /// NewActivityConfig's time-limit clock takes precedence over legacy start/end tuples. Tuple type 1 is
    /// absolute and type 2 is realm-open relative (inferred fallback, matching the matching TimeLimit rows
    /// 1037/1041); supplied time-limit rows provide the authoritative modern client schedule.
    /// </summary>
    public Window ResolveActivity(JsonElement row, long now, long roleCreateSeconds, int playbook)
    {
        long id = GameData.Int(row, "_iTimeLimitId");
        if (id > 0)
        {
            var windows = Segments(id, now, playbook);
            return windows.FirstOrDefault(w => w.Contains(now))
                ?? windows.FirstOrDefault(w => w.Start > now) ?? windows.LastOrDefault()
                ?? new Window("Close", 0, 0);
        }
        long begin = TupleTime(row, "_kStartTime", ServerOpen), end = TupleTime(row, "_kEndTime", Forever);
        // OpenContent: a dated event that already ended (Otherworldly Invasion, 2026-01-01..11) repeats with its own
        // length, so its phases (e.g. the 4-day donation at the start) come back every cycle.
        if (seasons.OpenContent.Enabled && seasons.OpenContent.PerpetualWindows && end <= now && end > begin)
        {
            long length = end - begin, cycle = (now - begin) / length;
            return new Window("Start", begin + cycle * length, begin + (cycle + 1) * length, cycle);
        }
        return new Window("Start", begin, end);
    }

    public bool IsOpen(int id, DateTimeOffset now) => Segments(id, now.ToUnixTimeSeconds(), seasons.PlayBook).Any(w => w.Contains(now.ToUnixTimeSeconds()));

    public bool TryWindow(int id, out DateTimeOffset start, out DateTimeOffset end)
    {
        long now = UtcNow.ToUnixTimeSeconds();
        var all = Segments(id, now, seasons.PlayBook);
        var window = all.FirstOrDefault(w => w.Contains(now)) ?? all.FirstOrDefault(w => w.Start > now) ?? all.LastOrDefault();
        start = DateTimeOffset.FromUnixTimeSeconds(window?.Start ?? 0);
        end = DateTimeOffset.FromUnixTimeSeconds(window?.End ?? 0);
        return window != null;
    }

    /// <summary>Realm-local day identifier; seven-day login counts distinct civil days, not elapsed 24h (inferred).</summary>
    public long Day(long seconds) => DateOnly.FromDateTime(Local(seconds).DateTime).DayNumber;

    private long TupleTime(JsonElement row, string field, long fallback)
    {
        if (!row.TryGetProperty(field, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() < 2) return fallback;
        long value = (long)(GameData.Number(values[1]) ?? 0);
        return GameData.Number(values[0]) == 2 ? ServerOpen + value : value;
    }

    private long Bound(JsonElement row, bool start, int playbook)
    {
        string side = start ? "Start" : "End";
        long absolute = GameData.Int(row, $"_iGreenWich{side}");
        if (absolute > 0 && seasons.OpenContent.PerpetualAbsoluteWindow(GameData.Int(row, "_iTimeLimitId")))
            return start ? ServerOpen : Forever;
        if (absolute > 0) return absolute;
        long offset = GameData.Int(row, $"_iServerOpen{side}");
        string reference = Text(row, $"_iSeason{side}StageInfo");
        // OpenContent: a window anchored on the server opening or on a season stage is open from the opening, forever.
        if ((offset > 0 || reference.Length > 0 || GameData.Int(row, $"_iServerOpen{(start ? "End" : "Start")}") > 0)
            && seasons.OpenContent.Perpetual(GameData.Int(row, "_iTimeLimitId")))
            return start ? ServerOpen : Forever;
        if (offset > 0) return ServerOpen + offset;
        if (reference.Length > 0)
        {
            var parts = reference.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int boundary) || !long.TryParse(parts[1], out long stageId)) return 0;
            var stages = seasons.Clock(playbook).StageTimeInfo.Values.OrderBy(s => s.StartTime).ToArray();
            var stage = stageId == 0 ? stages.FirstOrDefault() : stageId == -1 ? stages.LastOrDefault() : stages.FirstOrDefault(s => s.StageId == stageId);
            if (stage == null) return 0;
            return (boundary == 1 ? stage.StartTime : stage.EndTime) + GameData.Int(row, $"_iSeason{side}StateInfoTime");
        }
        return start ? ServerOpen : Forever;
    }

    private DateTimeOffset Local(long timestamp) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestamp), clock.LocalTimeZone);
    private long ToUnix(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), clock.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    private long Ceiling(long timestamp, string unit, long now)
    {
        if (unit == "Week")
        {
            // LC_TimeLimitHelper.L16_1 uniquely re-anchors Week to GetThisWeekTimestamp(1),
            // which uses the current local date (Lua_TimeHelper), not the opening date. Keep
            // that distinction: a season-long fixed UTC anchor drifts after a DST change.
            // The helper itself adds/subtracts seconds within a week; Day/Hour keep their
            // original timestamp anchor and fixed-second periods in L17_1/L18_1.
            var today = Local(now).DateTime;
            long monday = ToUnix(today.Date) - ((int)today.DayOfWeek + 6) % 7 * 86400L;
            return timestamp > monday ? monday + 604800 : monday;
        }
        var local = Local(timestamp).DateTime;
        var basis = unit switch
        {
            "Month" => new DateTime(local.Year, local.Month, 1),
            "Day" => local.Date,
            _ => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
        };
        if (local > basis) basis = unit switch { "Month" => basis.AddMonths(1), "Day" => basis.AddDays(1), _ => basis.AddHours(1) };
        return ToUnix(basis);
    }
    private long MonthsBetween(long first, long second)
    {
        var a = Local(first); var b = Local(second);
        return (b.Year - a.Year) * 12L + b.Month - a.Month;
    }
    private static string Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
