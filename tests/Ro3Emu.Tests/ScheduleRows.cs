using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>
/// Weekly DailyActivityConfig windows (_kDay/_kTime) read from the exported tables, so tests pin their clocks to the
/// client's own schedule instead of one export's literals (the 7 and 16 Oct 2026 clients moved several events). This is
/// the plain weekly schedule: rows with _iIsCustome follow DailyActivityCustomConfig during the opening weeks instead.
/// </summary>
public static class ScheduleRows
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    /// <summary>One occurrence as civil times and unix seconds, [Start, End).</summary>
    public sealed record Window(DateTime LocalStart, DateTime LocalEnd, long Start, long End);

    /// <summary>The row's first window starting on or after the civil date <paramref name="from"/> in <paramref name="zone"/>.</summary>
    public static Window Weekly(int id, DateTime from, TimeZoneInfo zone, GameData? data = null)
    {
        var row = (data ?? Exported.Value).Row("DailyActivityConfig", id)
            ?? throw new InvalidOperationException($"DailyActivityConfig {id} is not exported");
        // GetActivityWeeklyTimeList folds days modulo 7 (13 is Saturday).
        var days = row.GetProperty("_kDay").EnumerateArray().Select(d => ((int)GameData.Number(d)!.Value - 1) % 7 + 1).ToHashSet();
        if (days.Count == 0) throw new InvalidOperationException($"DailyActivityConfig {id} has no _kDay");
        var times = row.GetProperty("_kTime");
        int start = (int)GameData.Number(times[0])!.Value, end = (int)GameData.Number(times[1])!.Value;
        var day = from.Date;
        while (!days.Contains(((int)day.DayOfWeek + 6) % 7 + 1)) day = day.AddDays(1);
        return At(day, start, end, zone);
    }

    /// <summary>An activity window in one of the opening custom weeks, selected by the realm's opening weekday.</summary>
    public static Window Custom(int id, DateTime opening, int openedWeek, TimeZoneInfo zone, GameData? data = null)
    {
        var selected = DailyActivityCustomConfig.All(data ?? Exported.Value).Single(row =>
            row.OpenedWeek == openedWeek && OpeningDay(row, opening.DayOfWeek) == 1);
        var activity = selected.WeeklyOpenActivity.Single(row => row.Length >= 4 && row[1] == id);
        int weekday = ((int)activity[0] - 1) % 7 + 1;
        var day = Monday(opening).AddDays((openedWeek - 1) * 7 + weekday - 1);
        return At(day, (int)activity[2], (int)activity[3], zone);
    }

    private static long OpeningDay(DailyActivityCustomConfig row, DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => row.IsMonday,
        DayOfWeek.Tuesday => row.IsTuesday,
        DayOfWeek.Wednesday => row.IsWednesday,
        DayOfWeek.Thursday => row.IsThursday,
        DayOfWeek.Friday => row.IsFriday,
        DayOfWeek.Saturday => row.IsSaturday,
        _ => row.IsSunday,
    };

    private static Window At(DateTime day, int start, int end, TimeZoneInfo zone)
    {
        var localStart = day.AddMinutes(start / 100 * 60 + start % 100);
        var localEnd = day.AddMinutes(end / 100 * 60 + end % 100);
        if (localEnd <= localStart) localEnd = localEnd.AddDays(1);
        return new(localStart, localEnd, Unix(localStart, zone), Unix(localEnd, zone));
    }

    private static DateTime Monday(DateTime day) => day.Date.AddDays(1 - (((int)day.DayOfWeek + 6) % 7 + 1));

    public static long Unix(DateTime local, TimeZoneInfo zone) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUnixTimeSeconds();
}
