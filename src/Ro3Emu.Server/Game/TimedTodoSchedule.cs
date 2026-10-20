using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// DailyActivityConfig weekly HHMM windows, including the first four weeks' DailyActivityCustomConfig rotation
/// (LC_DailyActivityHelper.GetActivityWeeklyTimeList). The same occurrences drive
/// admission and the outer 5004 startTime/endTime; activity owners retain gameplay and named phase state.
/// Civil timezone follows ActivityClock (inferred realm timezone, matching the client's os.date('*t')).
/// </summary>
public sealed class TimedTodoSchedule(GameData data, ActivityClock clock, TimeProvider time)
{
    /// <summary>GM test aid only, process-local. Production scheduling never populates this map.</summary>
    internal Dictionary<int, Occurrence> TestWindows { get; } = new();
    public sealed record Occurrence(long Start, long End)
    {
        public bool Contains(long seconds) => Start <= seconds && seconds < End;
    }

    public bool IsOpen(int id) => CurrentOrNext(id) is { } window && window.Contains(clock.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// Current or next occurrence, including later weeks when a custom week omits the row. A missing/exhausted
    /// schedule is null, never the client's 0/0 "always open". Equal HHMM bounds mean a full civil day
    /// (inferred from 500/500 all-day rows, including guild card 101); ends are exclusive, also at 05:00.
    /// </summary>
    public Occurrence? CurrentOrNext(int id, long? timestamp = null)
    {
        if (TestWindows.TryGetValue(id, out var test)) return test;
        long at = timestamp ?? clock.UtcNow.ToUnixTimeSeconds(), opened = clock.ServerOpen;
        // Everything else the answer reads: the zone of the civil-day math and the schedule tables (fixed in production).
        var inputs = (clock.LocalTimeZone, data.Table("TimeLimitConfig").Count, data.Table("DailyActivityConfig").Count,
            data.Table("DailyActivityCustomConfig").Count);
        // The answer is the unfinished occurrence with the earliest start. Found at t0, it stays the answer for every
        // t in [t0, End): any rival would have had the earlier start and an end after t0 already. Login asked this for
        // every activity row (~7% of the loop during a login storm); a clock moved back or a new opening recomputes.
        if (_answers.TryGetValue(id, out var memo) && memo.Opened == opened && memo.Inputs == inputs && memo.At <= at && at < memo.Answer.End)
            return memo.Answer;
        var answer = Compute(id, at);
        if (answer != null) _answers[id] = (opened, inputs, at, answer);
        return answer;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (long Opened, (TimeZoneInfo, int, int, int) Inputs, long At, Occurrence Answer)> _answers = new();

    private Occurrence? Compute(int id, long now)
    {
        if (data.Row("DailyActivityConfig", id) is not { } row) return null;
        var today = Local(now).Date;
        var customRows = data.Table("DailyActivityCustomConfig").Values.ToArray();
        int customWeeks = (int)customRows.Select(r => GameData.Int(r, "_iOpenedWeek")).DefaultIfEmpty(0).Max();
        var opening = Local(clock.ServerOpen).Date;
        var firstMonday = Monday(opening);
        // All exported schedules repeat weekly after the custom prefix. Search through that prefix and
        // a complete fallback week; seed later absolute TimeLimit bounds too (inferred future lookup).
        var seeds = new HashSet<DateTime> { today };
        foreach (string field in new[] { "_iTimeLimitId", "_iTimeCtrlId" })
            if (GameData.Int(row, field) is > 0 and var limit)
                foreach (var segment in clock.Segments(limit, now).Where(w => w.End > now))
                    seeds.Add(Local(Math.Max(now, segment.Start)).Date);
        Occurrence? nearest = null;
        foreach (var seed in seeds)
        {
            var last = seed.AddDays(8);
            if (GameData.Int(row, "_iIsCustome") != 0 && firstMonday.AddDays((customWeeks + 1) * 7) > last)
                last = firstMonday.AddDays((customWeeks + 1) * 7);
            for (var day = seed.AddDays(-1); day <= last; day = day.AddDays(1))
            {
                var custom = CustomWeek(row, day, opening, firstMonday, customRows);
                IEnumerable<Occurrence> candidates;
                if (custom is { } selected)
                {
                    candidates = CustomWindows(selected, id, day);
                }
                else if (GameData.Int(row, "_iTimeCtrlId") is > 0 and var control)
                {
                    // (inferred) Without a custom override, adjacent named control phases form one outer
                    // occurrence. Keep gaps between cycles; do not expose Ready/Answer as separate events.
                    candidates = Merge(clock.Segments(control, ToUnix(day.AddHours(12)))
                        .Select(w => new Occurrence(w.Start, w.End))).Where(w => Local(w.Start).Date == day);
                }
                else candidates = WeeklyWindows(row, day);
                foreach (var candidate in candidates)
                {
                    var windows = new[] { candidate }.AsEnumerable();
                    if (GameData.Int(row, "_iTimeLimitId") is > 0 and var limit)
                        windows = clock.Segments(limit, candidate.Start).Select(w =>
                            new Occurrence(Math.Max(candidate.Start, w.Start), Math.Min(candidate.End, w.End)));
                    foreach (var window in windows)
                        if (window.Start < window.End && window.End > now &&
                            (nearest == null || window.Start < nearest.Start)) nearest = window;
                }
            }
        }
        return nearest;
    }

    private JsonElement? CustomWeek(JsonElement row, DateTime day, DateTime opening, DateTime firstMonday, JsonElement[] customRows)
    {
        if (GameData.Int(row, "_iIsCustome") != 0)
        {
            // LC_DailyActivityHelper.lua:819-888: Mondays since opening, selected by OPENING weekday.
            long week = Math.Max(1, (Monday(day) - firstMonday).Days / 7 + 1);
            string flag = Weekday(opening) switch
            {
                1 => "_iIsMonday",
                2 => "_iIsTuesday",
                3 => "_iIsWednesday",
                4 => "_iIsThursday",
                5 => "_iIsFriday",
                6 => "_iIsSaturday",
                _ => "_iIsSunday",
            };
            var selected = customRows.FirstOrDefault(r => GameData.Int(r, "_iOpenedWeek") == week && GameData.Int(r, flag) == 1);
            if (selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty("_kWeeklyOpenActivity", out var list)
                && list.ValueKind == JsonValueKind.Array) return selected;
        }
        return null;
    }

    private IEnumerable<Occurrence> CustomWindows(JsonElement selected, int id, DateTime day)
    {
        // A selected custom week with no entry is deliberately empty; never fall back to _kDay for it.
        foreach (var item in selected.GetProperty("_kWeeklyOpenActivity").EnumerateArray())
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 4 && GameData.Number(item[1]) == id
                && GameData.Number(item[0]) == Weekday(day))
                yield return At(day, (int)(GameData.Number(item[2]) ?? 0), (int)(GameData.Number(item[3]) ?? 0));
    }

    private IEnumerable<Occurrence> WeeklyWindows(JsonElement row, DateTime day)
    {
        if (!row.TryGetProperty("_kDay", out var days) || days.ValueKind != JsonValueKind.Array
            || !row.TryGetProperty("_kTime", out var times) || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() < 2) yield break;
        int startTime = (int)(GameData.Number(times[0]) ?? 0), endTime = (int)(GameData.Number(times[1]) ?? 0);
        // Preserve GetActivityWeeklyTimeList's modulo-7 normalization, including day 13.
        foreach (var entry in days.EnumerateArray())
            if (GameData.Number(entry) is { } n && (((int)n - 1) % 7 + 7) % 7 + 1 == Weekday(day))
                yield return At(day, startTime, endTime);
    }

    private Occurrence At(DateTime day, int start, int end)
    {
        var begin = day.AddMinutes(start / 100 * 60 + start % 100);
        var finish = day.AddMinutes(end / 100 * 60 + end % 100);
        if (finish <= begin) finish = finish.AddDays(1);
        return new(ToUnix(begin), ToUnix(finish));
    }

    private static IEnumerable<Occurrence> Merge(IEnumerable<Occurrence> windows)
    {
        Occurrence? previous = null;
        foreach (var window in windows.OrderBy(w => w.Start))
        {
            if (previous == null) previous = window;
            else if (window.Start <= previous.End) previous = previous with { End = Math.Max(previous.End, window.End) };
            else { yield return previous; previous = window; }
        }
        if (previous != null) yield return previous;
    }

    private DateTimeOffset Local(long seconds) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), time.LocalTimeZone);
    private long ToUnix(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), time.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    private static int Weekday(DateTime day) => ((int)day.DayOfWeek + 6) % 7 + 1;
    private static DateTime Monday(DateTime day) => day.Date.AddDays(1 - Weekday(day));
}
