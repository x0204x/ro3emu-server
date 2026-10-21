using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Fixed civil clock and real 5004 increments; client consumers are the Lua DALs.</summary>
public sealed class DailyWindowsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-daily-windows-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    public async Task InitializeAsync()
    {
        _clock.Set("2026-10-09 12:00:00");
        var (port, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("windows", "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync("Windows")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    private static async Task<DBDailyActivityInfoIncrement> Next(BotClient bot, Func<DBDailyActivityInfoIncrement, bool>? match = null)
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(5004) &&
            (match == null || match(DBDailyActivityInfoIncrement.Parser.ParseFrom(e.Body))), TimeSpan.FromSeconds(8));
        return DBDailyActivityInfoIncrement.Parser.ParseFrom(packet.Body);
    }

    private void Window(int id, string start, string end)
    {
        var window = Assert.IsType<TimedTodoSchedule.Occurrence>(Service<TimedTodoSchedule>().CurrentOrNext(id));
        Assert.Equal(_clock.Unix(start), window.Start);
        Assert.Equal(_clock.Unix(end), window.End);
    }

    private void Window(int id, long start, long end)
    {
        var window = Assert.IsType<TimedTodoSchedule.Occurrence>(Service<TimedTodoSchedule>().CurrentOrNext(id));
        Assert.Equal((start, end), (window.Start, window.End));
    }

    [ClientDataFact]
    public void WeeklyLeagueUsesOpeningWeekdayAndSkipsEmptyCustomWeeks()
    {
        // Monday-open week 1: Wednesday/Friday/Saturday, not the Friday-open row selected by the old code.
        Window(109, "2026-10-21 20:00:00", "2026-10-21 20:30:00");
        Window(110, "2026-10-09 20:30:00", "2026-10-09 21:00:00");
        Window(111, "2026-10-10 20:30:00", "2026-10-10 21:00:00");
        _clock.Set("2026-10-09 20:30:00");
        Assert.True(Service<TimedTodoSchedule>().IsOpen(110));
        _clock.Set("2026-10-09 21:00:00");
        Assert.False(Service<TimedTodoSchedule>().IsOpen(110));
        Window(110, "2026-10-24 20:00:00", "2026-10-24 20:30:00");
        _clock.Set("2026-10-12 12:00:00");
        Assert.False(Service<TimedTodoSchedule>().IsOpen(109));
        Window(109, "2026-10-21 20:00:00", "2026-10-21 20:30:00");
    }

    [ClientDataFact]
    public void ConfiguredTimedRowsUseCurrentThenNextOccurrence()
    {
        // Despite the task's example label, 115 is one weekday only and 125 is custom/weekly, not daily.
        var weekly = ScheduleRows.Weekly(115, new DateTime(2026, 10, 9), _clock.Zone, Service<GameData>());
        Window(115, weekly.Start, weekly.End);
        Window(125, "2026-10-25 20:00:00", "2026-10-25 21:00:00");
        Window(200, "2026-10-09 10:00:00", "2026-10-09 22:00:00");
        _clock.Set("2026-10-09 22:00:00");
        Assert.False(Service<TimedTodoSchedule>().IsOpen(200));
        Window(200, "2026-10-10 10:00:00", "2026-10-10 22:00:00");
    }

    [ClientDataFact]
    public void AllDayAndOvernightWindowsUseFiveAmAndCivilTimezone()
    {
        _clock.Set("2026-10-09 04:59:59");
        Window(101, "2026-10-08 05:00:00", "2026-10-09 05:00:00");
        Assert.True(Service<TimedTodoSchedule>().IsOpen(101));
        Window(119, "2026-10-09 04:00:00", "2026-10-09 06:00:00");
        _clock.Set("2026-10-09 05:00:00");
        Window(101, "2026-10-09 05:00:00", "2026-10-10 05:00:00");
        _clock.Set("2026-10-10 23:59:59");
        Window(210, "2026-10-10 10:00:00", "2026-10-11 00:00:00");
        _clock.Set("2026-10-11 00:00:00");
        Window(210, "2026-10-11 10:00:00", "2026-10-12 00:00:00");
        _clock.Set("2026-10-24 23:00:00");
        Window(101, "2026-10-24 05:00:00", "2026-10-25 05:00:00");
        Assert.Equal(25 * 3600, Service<TimedTodoSchedule>().CurrentOrNext(101)!.End - Service<TimedTodoSchedule>().CurrentOrNext(101)!.Start);
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("DailyWindows+08", TimeSpan.FromHours(8), "DailyWindows+08", "DailyWindows+08");
        _clock.Set("2026-10-09 12:00:00");
        Window(101, "2026-10-09 05:00:00", "2026-10-10 05:00:00");
    }

    [ClientDataFact]
    public void TimeLimitsAndControlPhasesKeepClosedRowsClosedAndPreserveGaps()
    {
        Window(122, "2026-10-09 20:00:00", "2026-10-09 21:30:00");
        Assert.Null(Service<TimedTodoSchedule>().CurrentOrNext(3004)); // TimeLimit 1007 is absent in the export.
        Assert.False(Service<TimedTodoSchedule>().IsOpen(3004));
        Window(3301, "2026-10-09 18:30:00", "2026-10-09 19:00:00");
        Window(3303, "2026-10-15 20:30:00", "2026-10-15 21:00:00");
        // 3304's control phases are week offsets; each cycle group (Matching + Battling) is one occurrence.
        var data = Service<GameData>();
        long control = GameData.Int(data.Row("DailyActivityConfig", 3304)!.Value, "_iTimeCtrlId");
        var cycles = data.Table("TimeLimitConfig").Values.Where(r => GameData.Int(r, "_iTimeLimitId") == control)
            .GroupBy(r => GameData.Int(r, "_iCycleGroup")).OrderBy(g => g.Key)
            .Select(g => (Start: g.Min(r => GameData.Int(r, "_iWeekStart")), End: g.Max(r => GameData.Int(r, "_iWeekEnd"))))
            .ToArray();
        long Week(long offset) => ScheduleRows.Unix(new DateTime(2026, 10, 12).AddSeconds(offset), _clock.Zone);
        Assert.True(cycles[1].Start > cycles[0].End);
        _clock.Set(Week(cycles[0].End));
        Window(3304, Week(cycles[1].Start), Week(cycles[1].End));
        Assert.False(Service<TimedTodoSchedule>().IsOpen(3304));
        _clock.Set(Week(cycles[1].Start));
        Assert.True(Service<TimedTodoSchedule>().IsOpen(3304));
        _clock.Set("2026-10-06 20:40:00");
        Window(3400, "2026-10-06 20:30:00", "2026-10-06 21:00:00"); // custom overrides conflicting 1079
        Assert.True(Service<TimedTodoSchedule>().IsOpen(3400));
    }

    [ClientDataFact]
    public async Task AbsoluteLimitClipsWindowAndCanFindAnOccurrenceBeyondNextWeek()
    {
        await OnLoop(() =>
        {
            var table = (Dictionary<string, JsonElement>)Service<GameData>().Table("TimeLimitConfig");
            table["daily-window-test"] = JsonSerializer.SerializeToElement(new
            {
                _iTimeLimitId = 1007, _iGreenWichStart = _clock.Unix("2026-12-10 20:05:00"),
                _iGreenWichEnd = _clock.Unix("2026-12-10 20:15:00"),
            });
            return Task.CompletedTask;
        });
        Window(3004, "2026-12-10 20:05:00", "2026-12-10 20:15:00");
        _clock.Set("2026-12-10 20:15:00");
        Assert.Null(Service<TimedTodoSchedule>().CurrentOrNext(3004));
    }

    [ClientDataTheory]
    [InlineData(8)]
    [InlineData(-12)]
    public async Task LoginCarriesUnixWindowsForEveryRowAndGuildGoHasCandidates(int utcOffset)
    {
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("DailyWindowsZone", TimeSpan.FromHours(utcOffset), "DailyWindowsZone", "DailyWindowsZone");
        _clock.Set("2026-10-09 12:00:00");
        await using var bot = await InWorld();
        var sync = await Next(bot, s => s.SyncAllMark);
        Assert.Equal(60, sync.ActivityData.Count);
        foreach (var (id, field) in sync.ActivityData)
        {
            Assert.NotNull(field.All);
            Assert.True(field.All.StartTime > 0, $"row {id} has no startTime");
            Assert.True(field.All.EndTime > field.All.StartTime, $"row {id} cannot drive the GVE progress ratio");
        }
        foreach (int id in new[] { 125, 115, 116, 101, 109, 110, 111, 3303, 3400 })
            Assert.True(sync.ActivityData[id].All.EndTime > _clock.GetUtcNow().ToUnixTimeSeconds());
        Assert.Equal(_clock.Unix("2026-10-09 05:00:00"), sync.ActivityData[101].All.StartTime);
        Assert.Equal(_clock.Unix("2026-10-10 05:00:00"), sync.ActivityData[101].All.EndTime);
        Assert.Equal(_clock.Unix("2026-10-09 05:00:00"), Session(bot).Daily!.DailyEpoch);
        // The client selects by the wire's opening timestamp in its zone, not the SeasonStart text.
        var opening = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(Service<ActivityClock>().ServerOpen), _clock.Zone);
        if (opening.DayOfWeek == DayOfWeek.Sunday)
            Assert.Equal(_clock.Unix("2026-10-29 20:00:00"), sync.ActivityData[3400].All.StartTime);
        else if (opening.DayOfWeek == DayOfWeek.Monday)
            Assert.Equal(_clock.Unix("2026-11-05 20:00:00"), sync.ActivityData[3400].All.StartTime);
        Assert.Equal(1, sync.ActivityData[3004].All.StartTime);
        Assert.Equal(2, sync.ActivityData[3004].All.EndTime);
        Assert.Equal(1, sync.ActivityData[3302].All.ActivityInfo.ActivityParam.RunRingTask.RemainRingCount);
    }

    [ClientDataFact]
    public async Task OccurrenceEndPushesOnlyTimeIncrementAndKeepsProgress()
    {
        _clock.Set("2026-10-09 20:59:59");
        await using var bot = await InWorld();
        var first = await Next(bot, s => s.SyncAllMark);
        await OnLoop(async () =>
        {
            var session = Session(bot);
            session.DailyRows!.ActivityData[110].TaskCompleteTimes = 2;
            session.DailyRows.ActivityData[110].Tasks[123] = 2;
            session.Daily!.Rows = session.DailyRows.ToByteArray();
            await Service<DailyActivityService>().SyncEventsAsync(session);
        });
        await Next(bot, s => s.ActivityData.TryGetValue(110, out var f) && f.Inc?.TaskCompleteTimes == 2);
        _clock.Set("2026-10-09 21:00:00"); // automatic ticker, no manual Reconcile call
        var update = await Next(bot, s => s.ActivityData.TryGetValue(110, out var f) && f.Inc?.HasStartTime == true);
        Assert.False(update.SyncAllMark);
        Assert.True(update.Version > first.Version);
        Assert.Equal(_clock.Unix("2026-10-24 20:00:00"), update.ActivityData[110].Inc.StartTime);
        Assert.Equal(_clock.Unix("2026-10-24 20:30:00"), update.ActivityData[110].Inc.EndTime);
        Assert.False(update.ActivityData[110].Inc.HasTaskCompleteTimes);
        Assert.Empty(update.ActivityData[110].Inc.Tasks);
        Assert.False(update.ActivityData.ContainsKey(101));
        Assert.Equal(2, Session(bot).DailyRows!.ActivityData[110].TaskCompleteTimes);
    }

    [ClientDataFact]
    public async Task FiveAmRolloverAndReconnectPushFreshAllDayWindow()
    {
        _clock.Set("2026-10-09 04:59:59");
        await using (var bot = await InWorld())
        {
            var first = await Next(bot, s => s.SyncAllMark);
            Assert.Equal(_clock.Unix("2026-10-08 05:00:00"), first.ActivityData[101].All.StartTime);
            _clock.Set("2026-10-09 05:00:00");
            var update = await Next(bot, s => s.ActivityData.TryGetValue(101, out var f) && f.Inc?.HasStartTime == true);
            Assert.False(update.SyncAllMark);
            Assert.Equal(_clock.Unix("2026-10-09 05:00:00"), update.ActivityData[101].Inc.StartTime);
            Assert.Equal(_clock.Unix("2026-10-10 05:00:00"), update.ActivityData[101].Inc.EndTime);
            Assert.Equal(_clock.Unix("2026-10-09 05:00:00"), Session(bot).Daily!.DailyEpoch);
        }
        for (int i = 0; i < 100 && Service<SessionRegistry>().All().Any(s => s.Account == "windows"); i++) await Task.Delay(20);
        _clock.Set("2026-10-10 06:00:00");
        await using var reconnected = await InWorld(create: false);
        var fresh = await Next(reconnected, s => s.SyncAllMark);
        Assert.Equal(_clock.Unix("2026-10-10 05:00:00"), fresh.ActivityData[101].All.StartTime);
        Assert.Equal(_clock.Unix("2026-10-11 05:00:00"), fresh.ActivityData[101].All.EndTime);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        public override TimeZoneInfo LocalTimeZone => Zone;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public long Unix(string value)
        {
            var date = DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", global::System.Globalization.CultureInfo.InvariantCulture);
            return new DateTimeOffset(date, Zone.GetUtcOffset(date)).ToUnixTimeSeconds();
        }
        public void Set(string value) => Interlocked.Exchange(ref _seconds, Unix(value));
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("data/config");
    }
}
