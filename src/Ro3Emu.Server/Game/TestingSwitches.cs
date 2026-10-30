using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ro3Emu:Testing at run time. TEST AIDS, not the original's behaviour (the client data has no such switch): the
/// configured values, changed in memory by GM "!testing" until the next start (docs/guide/hosting.md).
/// <list type="bullet">
/// <item><see cref="AlwaysOpen"/>: TimedTodoSchedule gives every To-Do row (DailyActivityConfig) an occurrence that holds
/// the present, and ActivityClock repeats the phases of the rows' control ids (_iTimeCtrlId 1074, 1079, 1083, 1084)
/// back to back, so the events that run phases (Festival Invitation, Hunting Challenge, Wedding Crashers, Mock
/// Battle) keep them. 5004 carries those occurrences, so the client's To-Do shows the rows as open
/// (LC_DailyActivityHelper.GetActivityOpenByServer reads only the server's iStartTime/iEndTime).</item>
/// <item><see cref="UnlimitedRewards"/>: reward allowances and attempt counters are neither checked nor spent.</item>
/// </list>
/// </summary>
public sealed class TestingSwitches(IOptions<ServerOptions> options)
{
    private readonly TestingOptions _configured = options.Value.Testing;

    public bool AlwaysOpen { get; set; } = options.Value.Testing.AlwaysOpen;

    public bool UnlimitedRewards { get; set; } = options.Value.Testing.UnlimitedRewards;

    public bool Any => AlwaysOpen || UnlimitedRewards;

    /// <summary>"!testing reset": the configured values again.</summary>
    public void Reset()
    {
        AlwaysOpen = _configured.AlwaysOpen;
        UnlimitedRewards = _configured.UnlimitedRewards;
    }

    public string Describe() => $"AlwaysOpen {OnOff(AlwaysOpen)}, UnlimitedRewards {OnOff(UnlimitedRewards)}";

    private static string OnOff(bool on) => on ? "ON" : "off";

    /// <summary>
    /// To-Do rows whose events run on calendars of their own, which a free window cannot start: the Guild League
    /// (GuildLeagueCalendar: registration, brackets and rounds; GM "!league"), the Territory Wars stages (TerritoryWarsClock;
    /// GM "!twpoint") and the Royal City War (TerritoryWarsService.Siege; GM "!siege open"), with their auction checks.
    /// <see cref="AlwaysOpen"/> leaves their windows as they are, so the To-Do does not show a war that is not running.
    /// </summary>
    internal static readonly HashSet<int> OwnCalendar =
    [
        109, 110, 111, 129, 3005, 3006, 3007, 3100, 3101, 3102, // Guild League
        122, 123, 124, 3008, 3009, 3010, 3103, 3104, 3105,       // Territory Wars
        115, 3011, 3106,                                         // Royal City War
    ];

    /// <summary>The To-Do row <paramref name="activityId"/> is open at any hour now.</summary>
    public bool Opens(int activityId) => AlwaysOpen && !OwnCalendar.Contains(activityId);

    /// <summary>Lua_TimeHelper.GetDayRefreshTime: the game day turns at 05:00.</summary>
    private const int DayBreakHour = 5;

    /// <summary>
    /// The test day's two parts, [00:00, 05:00) and [05:00, 23:59:59): each lies inside one civil date and one game day,
    /// which GetActivityOpenByServer demands of a window (Lua_TimeHelper.IsSameGameDay on its start and end).
    /// </summary>
    private static IEnumerable<(long Start, long End)> Parts(DateTime date, TimeZoneInfo zone)
    {
        long midnight = ToUnix(date, zone), dayBreak = ToUnix(date.AddHours(DayBreakHour), zone);
        yield return (midnight, dayBreak);
        yield return (dayBreak, ToUnix(date.AddDays(1), zone) - 1);
    }

    /// <summary>
    /// The first two slots of <paramref name="length"/> seconds that end after <paramref name="at"/>: slots follow each other
    /// from the start of each part of the day, a length longer than the part takes the whole part. A part's last slot is
    /// cut at the part's end, or left out with <paramref name="fullOnly"/> (a run of phases is never cut short).
    /// </summary>
    internal static IEnumerable<(long Start, long End)> Slots(long at, long length, bool fullOnly, TimeZoneInfo zone)
    {
        if (length <= 0) yield break;
        int found = 0;
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(at), zone).Date;
        foreach (var (start, end) in Parts(today, zone).Concat(Parts(today.AddDays(1), zone)))
        {
            if (end <= at) continue;
            long step = Math.Min(length, end - start);
            for (long slot = start + (at > start ? (at - start) / step * step : 0); slot < end && found < 2; slot += step)
            {
                long finish = slot + step;
                if (finish > end)
                {
                    if (fullOnly) break;
                    finish = end;
                }
                if (finish <= at) continue;
                found++;
                yield return (slot, finish);
            }
            if (found == 2) yield break;
        }
    }

    private static long ToUnix(DateTime local, TimeZoneInfo zone) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUnixTimeSeconds();
}
