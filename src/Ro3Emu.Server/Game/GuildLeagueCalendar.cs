using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Guild League tournament calendar the server owns (LeagueActivityInfo, LC_DAL_GuildLeague.lua:114-324). The client
/// (LC_DailyActivityHelper.lua:1014-1035) and TimedTodoSchedule fold DailyActivityConfig _kDay 13 into "every Saturday"
/// and ignore _iRefreshWeek, so the real dates come from here.
/// GuildSettingConfig GuildLeagueActivityID0..3 name the four rows: 129 registration (Monday 05:03, UITips 90006), 109
/// preliminaries, 110 semifinals, 111 finals. The opening weeks follow DailyActivityCustomConfig (selected by the opening
/// weekday, as TimedTodoSchedule.CustomWeek): every complete 129/109/110/111 run in those weeks is one tournament. After the
/// custom weeks the rows' _kDay 1/3/6/13 and _iRefreshWeek 2 read as a two-week cycle (inferred: the opening rows already
/// run a tournament across weeks 3-4, so the cycle restarts in week 5). Times are unix seconds, windows [start, end).
/// </summary>
public sealed class GuildLeagueCalendar(GameData data, ActivityClock clock, TimeProvider time)
{
    public sealed record Window(long Start, long End)
    {
        public bool Contains(long seconds) => Start <= seconds && seconds < End;
    }

    /// <summary>One tournament: registration (Init) and its three match windows.</summary>
    public sealed record Tournament(int Index, long Init, Window Preliminary, Window Semifinal, Window Final)
    {
        /// <summary>Round 1 preliminaries, 2 semifinals, 3 finals.</summary>
        public Window Round(int round) => round switch { 1 => Preliminary, 2 => Semifinal, _ => Final };
    }

    public int RegistrationActivity => Activity(0, 129);
    public int[] RoundActivities => [Activity(1, 109), Activity(2, 110), Activity(3, 111)];

    private int Activity(int slot, int fallback) => data.Table("GuildSettingConfig").TryGetValue($"GuildLeagueActivityID{slot}", out var row)
        && row.TryGetProperty("_kValue", out var value) && GameData.Number(value) is { } n ? (int)n : fallback;

    private (long Opened, int Count, Tournament[] Prefix, DateTime Anchor)? _memo;

    /// <summary>The latest tournament registered at or before <paramref name="now"/>, or null before the first one.</summary>
    public Tournament? Current(long now)
    {
        Tournament? found = null;
        foreach (var t in From(now - 15 * 86400L))
        {
            if (t.Init > now) break;
            found = t;
        }
        return found;
    }

    /// <summary>The first tournament registered after <paramref name="now"/>.</summary>
    public Tournament Next(long now) => From(now).First(t => t.Init > now);

    /// <summary>Tournaments in order, starting with the first one whose final ends after <paramref name="from"/>.</summary>
    public IEnumerable<Tournament> From(long from)
    {
        var (prefix, anchor) = Layout();
        foreach (var t in prefix)
            if (t.Final.End > from) yield return t;
        var steady = Steady();
        if (steady == null) yield break;
        long anchorUnix = ToUnix(anchor);
        long cycle = Math.Max(0, (from - anchorUnix) / (14 * 86400L) - 1);
        for (long k = cycle; ; k++)
        {
            var t = Build(prefix.Length + (int)k + 1, anchor.AddDays(14 * k), steady.Value);
            if (t.Final.End > from) yield return t;
        }
    }

    /// <summary>
    /// The 30410 timeline at <paramref name="now"/>. Statuses (MG_GuildLeague.proto GuildLeagueStatus) follow the panel's
    /// reading of them (UI_Sys_GuildLeague_LeagueScheduleWidgetCtrl.lua:424-470: a stage page counts down to its timeList
    /// entry while eStatus has not passed it; GetResultByScore shows one result in FightTwo, two in FightThree, three in
    /// Reward): Init until the preliminaries start, FightOne through them, FightTwo until the semifinals end, FightThree
    /// until the finals end, then Reward until the next registration. open/startTime/endTime describe the current or next
    /// match window (UI_Sys_GuildLeague_BattleWidgetCtrl.lua:415-560 counts down to endTime while open, else to
    /// startTime); in Reward they are the next tournament's preliminaries, as nextPreliminaryTime.
    /// </summary>
    public LeagueActivityInfo Info(long now)
    {
        var current = Current(now);
        var next = Next(now);
        var info = new LeagueActivityInfo { NextPreliminaryTime = next.Preliminary.Start };
        if (current == null)
        {
            info.EStatus = GuildLeagueStatus.NotOpen;
            info.StartTime = next.Preliminary.Start; info.EndTime = next.Preliminary.End;
            info.PreliminaryTime = next.Preliminary.Start; info.SemiFinalTime = next.Semifinal.Start; info.FinalTime = next.Final.Start;
            info.PreliminaryEndTime = next.Preliminary.End; info.SemiFinalEndTime = next.Semifinal.End; info.FinalEndTime = next.Final.End;
            return info;
        }
        info.Index = current.Index;
        info.InitTime = current.Init;
        info.PreliminaryTime = current.Preliminary.Start; info.PreliminaryEndTime = current.Preliminary.End;
        info.SemiFinalTime = current.Semifinal.Start; info.SemiFinalEndTime = current.Semifinal.End;
        info.FinalTime = current.Final.Start; info.FinalEndTime = current.Final.End;
        info.EStatus = Status(current, now);
        var window = info.EStatus switch
        {
            GuildLeagueStatus.Init or GuildLeagueStatus.FightOne => current.Preliminary,
            GuildLeagueStatus.FightTwo => current.Semifinal,
            GuildLeagueStatus.FightThree => current.Final,
            _ => next.Preliminary,
        };
        info.StartTime = window.Start; info.EndTime = window.End;
        info.Open = window.Contains(now);
        return info;
    }

    public static GuildLeagueStatus Status(Tournament t, long now) =>
        now < t.Preliminary.Start ? GuildLeagueStatus.Init
        : now < t.Preliminary.End ? GuildLeagueStatus.FightOne
        : now < t.Semifinal.End ? GuildLeagueStatus.FightTwo
        : now < t.Final.End ? GuildLeagueStatus.FightThree
        : GuildLeagueStatus.Reward;

    /// <summary>The round whose match window contains <paramref name="now"/>, or 0.</summary>
    public static int OpenRound(Tournament t, long now) =>
        t.Preliminary.Contains(now) ? 1 : t.Semifinal.Contains(now) ? 2 : t.Final.Contains(now) ? 3 : 0;

    private (Tournament[] Prefix, DateTime Anchor) Layout()
    {
        long opened = clock.ServerOpen;
        int count = data.Table("DailyActivityCustomConfig").Count;
        if (_memo is { } m && m.Opened == opened && m.Count == count) return (m.Prefix, m.Anchor);
        var opening = Local(opened).Date;
        var firstMonday = Monday(opening);
        var rows = data.Table("DailyActivityCustomConfig").Values.ToArray();
        int weeks = (int)rows.Select(r => GameData.Int(r, "_iOpenedWeek")).DefaultIfEmpty(0).Max();
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
        int[] ids = [RegistrationActivity, .. RoundActivities];
        var entries = new List<(int Activity, Window Window)>();
        for (int week = 1; week <= weeks; week++)
        {
            var row = rows.FirstOrDefault(r => GameData.Int(r, "_iOpenedWeek") == week && GameData.Int(r, flag) == 1);
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_kWeeklyOpenActivity", out var list)
                || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 4) continue;
                int activity = (int)(GameData.Number(item[1]) ?? 0);
                if (!ids.Contains(activity)) continue;
                var day = firstMonday.AddDays((week - 1) * 7 + (int)(GameData.Number(item[0]) ?? 1) - 1);
                entries.Add((activity, At(day, (int)(GameData.Number(item[2]) ?? 0), (int)(GameData.Number(item[3]) ?? 0))));
            }
        }
        var prefix = new List<Tournament>();
        long? init = null; Window? prelim = null, semi = null;
        foreach (var (activity, window) in entries.OrderBy(e => e.Window.Start))
        {
            if (activity == ids[0]) { init = window.Start; prelim = semi = null; }
            else if (activity == ids[1]) { prelim = window; semi = null; }
            else if (activity == ids[2] && prelim != null) semi = window;
            else if (activity == ids[3] && prelim != null && semi != null)
            {
                // (inferred) A run without its own 129 row registers at 05:03 on the preliminaries' day.
                long registered = init ?? ToUnix(Local(prelim.Start).Date.AddMinutes(5 * 60 + 3));
                prefix.Add(new Tournament(prefix.Count + 1, Math.Min(registered, prelim.Start), prelim, semi, window));
                init = null; prelim = semi = null;
            }
        }
        var anchor = firstMonday.AddDays(weeks * 7);
        _memo = (opened, count, prefix.ToArray(), anchor);
        return (_memo.Value.Prefix, anchor);
    }

    private ((int Day, int Start, int End) Init, (int Day, int Start, int End)[] Rounds)? Steady()
    {
        (int, int, int)? Row(int id)
        {
            if (data.Row("DailyActivityConfig", id) is not { } row || !row.TryGetProperty("_kDay", out var days)
                || days.ValueKind != JsonValueKind.Array || days.GetArrayLength() == 0
                || !row.TryGetProperty("_kTime", out var times) || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() < 2)
                return null;
            return ((int)(GameData.Number(days[0]) ?? 1), (int)(GameData.Number(times[0]) ?? 0), (int)(GameData.Number(times[1]) ?? 0));
        }
        if (Row(RegistrationActivity) is not { } init) return null;
        var rounds = RoundActivities.Select(Row).ToArray();
        if (rounds.Any(r => r == null)) return null;
        return (init, rounds.Select(r => r!.Value).ToArray());
    }

    private Tournament Build(int index, DateTime cycleStart, ((int Day, int Start, int End) Init, (int Day, int Start, int End)[] Rounds) s)
    {
        Window Of((int Day, int Start, int End) r) => At(cycleStart.AddDays(r.Day - 1), r.Start, r.End);
        return new Tournament(index, Of(s.Init).Start, Of(s.Rounds[0]), Of(s.Rounds[1]), Of(s.Rounds[2]));
    }

    private Window At(DateTime day, int start, int end)
    {
        var begin = day.AddMinutes(start / 100 * 60 + start % 100);
        var finish = day.AddMinutes(end / 100 * 60 + end % 100);
        if (finish <= begin) finish = finish.AddDays(1);
        return new(ToUnix(begin), ToUnix(finish));
    }

    private DateTimeOffset Local(long seconds) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), time.LocalTimeZone);
    private long ToUnix(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
        time.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    private static int Weekday(DateTime day) => ((int)day.DayOfWeek + 6) % 7 + 1;
    private static DateTime Monday(DateTime day) => day.Date.AddDays(1 - Weekday(day));
}
