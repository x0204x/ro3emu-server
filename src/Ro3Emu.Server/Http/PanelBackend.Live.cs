using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The event calendar and the MVP timers, answered by the server's own clocks on the game loop (where the testing
/// switches and the MVP states change) and kept for a short while: the calendar for a minute, the timers for
/// <see cref="OnlineTtl"/>, whatever the traffic.
/// </summary>
public sealed partial class PanelBackend
{
    private const int CalendarDays = 7, MaxOccurrences = 64;
    /// <summary>The game day starts at 05:00 local time (DailyActivityService.GameDay, TestingSwitches.DayBreakHour).</summary>
    private const int DayStartHour = 5;
    private static readonly TimeSpan CalendarTtl = TimeSpan.FromMinutes(1);

    private Task<PanelCalendar>? _calendar;
    private DateTimeOffset _calendarAt;
    private Task<IReadOnlyList<MvpTimer>>? _mvpTimers;
    private DateTimeOffset _mvpTimersAt;

    public Task<PanelCalendar> CalendarAsync() =>
        Fresh(ref _calendar, ref _calendarAt, CalendarTtl, () => loop.InvokeAsync(() => Task.FromResult(BuildCalendar())));

    /// <summary>
    /// Each timed To-Do row (DailyActivityConfig _iIsHide 0) with its openings from now to a week ahead, as
    /// TimedTodoSchedule.CurrentOrNext gives them to the client (packet 5004), and the season's long windows
    /// (NewActivityConfig, ActivityClock.ResolveActivity) that are open now or later.
    /// </summary>
    private PanelCalendar BuildCalendar()
    {
        var now = clock.GetUtcNow();
        long from = now.ToUnixTimeSeconds(), horizon = from + CalendarDays * 86400L;
        var events = new List<PanelEvent>();
        foreach (var row in DailyActivityConfig.All(data).Where(r => r.IsHide == 0).OrderBy(r => r.Id))
        {
            if (Plain(row.Name) is not { Length: > 0 } name) continue;
            var times = new List<PanelEventTime>();
            for (long at = from; times.Count < MaxOccurrences && todo.CurrentOrNext((int)row.Id, at) is { } next && next.Start < horizon && next.End > at; at = next.End)
                times.Add(new PanelEventTime(DateTimeOffset.FromUnixTimeSeconds(next.Start), DateTimeOffset.FromUnixTimeSeconds(next.End)));
            events.Add(new PanelEvent(row.Id, name, row.Icon is [var icon, ..] ? Image("events", icon) : "", (int)row.ILv, Plain(row.TimeDescribe), times));
        }
        var season = NewActivityConfig.All(data).Select(row => (row, window: activities.ResolveActivity(row.Row, from, 0, seasons.PlayBook)))
            .Where(r => r.window.Start > 0 && r.window.End > from && Plain(r.row.Name).Length > 0)
            .Select(r => new PanelSeasonEvent(r.row.Id, Plain(r.row.Name), DateTimeOffset.FromUnixTimeSeconds(r.window.Start),
                DateTimeOffset.FromUnixTimeSeconds(r.window.End)))
            .OrderBy(s => s.Start).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new PanelCalendar(now, activities.LocalTimeZone.Id, DayStartHour, testing.AlwaysOpen, seasons.OpenContent.Enabled,
            [.. events.OrderBy(e => e.Times.Count == 0).ThenBy(e => e.Times.Count > 0 ? e.Times[0].Start : DateTimeOffset.MaxValue).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)], season);
    }

    public async Task<IReadOnlyList<PanelMvpTimer>> MvpTimersAsync()
    {
        var timers = await Fresh(ref _mvpTimers, ref _mvpTimersAt, OnlineTtl, () => loop.InvokeAsync(() => Task.FromResult(mvp.Timers())));
        var catalog = await CatalogAsync();
        return [.. timers.Select(t =>
        {
            var monster = catalog.MonstersById.TryGetValue(t.NpcId, out var group) ? group.Rep.Summary with { Id = t.NpcId }
                : new PanelMonsterSummary(t.NpcId, "Monster " + t.NpcId, t.Level, 0, 0, "", "", "", "mvp");
            string state = t.Alive ? "alive" : t.NextSpawnMs > 0 ? "respawning" : "spawning";
            return new PanelMvpTimer(t.ConfigId, monster, t.Level, t.MapId, MapName(t.MapId), MapImage(t.MapId), state,
                t.LastDeathMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t.LastDeathMs) : null,
                t.NextSpawnMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t.NextSpawnMs) : null, (int)(t.RefreshMs / 1000));
        }).OrderBy(t => t.State == "alive").ThenBy(t => t.NextSpawn).ThenBy(t => t.Monster.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The cached answer while it is younger than <paramref name="ttl"/>, else a new one; a failure is asked again.</summary>
    private Task<T> Fresh<T>(ref Task<T>? cached, ref DateTimeOffset at, TimeSpan ttl, Func<Task<T>> read)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (cached == null || cached.IsFaulted || cached.IsCanceled || cached.IsCompleted && now - at >= ttl)
            {
                cached = read();
                at = now;
            }
            return cached;
        }
    }
}
