using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class DailyActivityService
{
    /// <summary>Event owners project their authoritative state into the shared 5004 snapshot before every send.</summary>
    public List<Action<GameSession, DBDailyActivityInfo>> EventProjections { get; } = new();

    private Task SyncRowsAsync(GameSession session, DateTime? localNow = null)
    {
        var now = localNow ?? _clock.GetLocalNow().DateTime;
        long seconds = ToUnix(now);
        var rows = Rows(session).Clone();
        foreach (var project in EventProjections) project(session, rows);
        // Derived times are refreshed after owner projections, without resetting progress/quests or replacing
        // their nested ActivityInfo phase state. CharacterService emits only changed map fields in 5004.
        ProjectWindows(rows, seconds);
        session.DailyWindowSampledAt = seconds;
        session.DailyWindowRefreshAt = rows.ActivityData.Values.Select(r => r.EndTime).Where(end => end > seconds)
            .Append(ToUnix(now.Date.AddDays(1))) // custom weeks change at civil Monday, before the 05:00 reset
            .Min();
        return CharacterService.SyncAsync(session, ActivitySync, rows);
    }

    /// <summary>
    /// LC_DAL_DailyActivity maps these seconds to iStartTime/iEndTime. 0/0 is "always open" to time-state and
    /// calendar helpers but unusable by GetNearestGuildActivity. No future occurrence uses 1/2 (inferred
    /// expired sentinel); a positive duration also keeps the GVE auction's (end-now)/(end-start) finite.
    /// Missing schedules cannot masquerade as all-day events.
    /// </summary>
    private void ProjectWindows(DBDailyActivityInfo rows, long seconds)
    {
        foreach (var (id, row) in rows.ActivityData)
        {
            var window = _timedSchedule.CurrentOrNext(id, seconds);
            row.StartTime = window?.Start ?? 1;
            row.EndTime = window?.End ?? 2;
        }
    }

    public async Task SyncEventsAsync(GameSession session)
    {
        if (!session.DailyReady || session.DailyRows == null) return;
        using var gate = await TaskService.LockTasksAsync(session);
        // Another owner's projection (festival, activities) may run first after 05:00: it must not send the new
        // day's windows over the old day's progress, so a crossed boundary takes the full reset path.
        var now = _clock.GetLocalNow().DateTime;
        if (session.Daily is { } state && (state.DailyEpoch < Boundary(now) || state.WeeklyEpoch < WeekBoundary(now)))
            await ReconcileCoreAsync(session, now);
        else await SyncRowsAsync(session, now);
    }
}
