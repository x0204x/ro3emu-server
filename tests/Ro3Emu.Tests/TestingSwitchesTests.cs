using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Ro3Emu:Testing:AlwaysOpen (TestingSwitches, a test aid): off, a weekday-only To-Do activity stays on its schedule; on
/// ("!testing alwaysopen on"), the client's To-Do row is open now (5004) and the server admits the player.
/// </summary>
public sealed class TestingSwitchesTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    private const int DragonIsland = 126, DragonEntry = 6, Dodo = 3302;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-testing-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public async Task InitializeAsync()
    {
        // Tuesday 2026-11-10 12:00: Dragon Coffin Island (_kDay 1, 3, 6 at 18:30-19:55) is not on, and the first four
        // custom weeks after the 2026-10-05 opening are over.
        _clock.Set(Local(new DateTime(2026, 11, 10, 12, 0, 0)));
        var (port, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05";
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

    private static long Local(DateTime local) => new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUnixTimeSeconds();

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<int> StartDragonMatch(BotClient bot)
    {
        await bot.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = DragonEntry });
        return (await Next(bot, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result;
    }

    [ClientDataFact]
    public async Task AWeekdayOnlyActivityOpensOnAnotherDayOnlyWhileAlwaysOpenIsOn()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");

        // Off (the default): the To-Do shows the next Wednesday's occurrence and the match refuses the player.
        var todo = Service<TimedTodoSchedule>();
        Assert.False(todo.IsOpen(DragonIsland));
        Assert.True(todo.CurrentOrNext(DragonIsland)!.Start > Now);
        Assert.Equal(ErrorCodes.FeatureUnavailable, await StartDragonMatch(alice));

        await Gm(alice, "!testing alwaysopen on");
        Assert.True(Service<TestingSwitches>().AlwaysOpen);
        // 5004 now carries an occurrence that holds the present, inside one civil date (GetActivityOpenByServer).
        var row = (await Next(alice, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser,
            update => update.ActivityData.TryGetValue(DragonIsland, out var field) && field.Inc?.HasStartTime == true))
            .ActivityData[DragonIsland].Inc;
        Assert.InRange(Now, row.StartTime, row.EndTime - 1);
        Assert.Equal(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(row.StartTime), Zone).Date,
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(row.EndTime), Zone).Date);
        Assert.True(todo.IsOpen(DragonIsland));
        Assert.Equal(0, await StartDragonMatch(alice));

        // The wars keep their own calendars: the Royal City War row stays on its schedule.
        Assert.False(todo.IsOpen(115));

        await Gm(alice, "!testing reset");
        Assert.False(Service<TestingSwitches>().AlwaysOpen);
        Assert.False(todo.IsOpen(DragonIsland));
    }

    [ClientDataFact]
    public async Task DodosRequestCanBeAcceptedAgainOnlyWhileUnlimitedRewardsIsOn()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 34");
        await Gm(alice, "!quest done 10105090"); // functions 25 and 5902 (DailyTests' gate)
        var session = Service<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            // Today's chain is done: the allowance is used up, as after its terminal node was delivered.
            var row = session.DailyRows!.ActivityData[Dodo];
            row.TaskCompleteTimes = 1;
            row.ActivityInfo.ActivityParam.RunRingTask.RemainRingCount = 0;
            session.Daily!.Rows = session.DailyRows.ToByteArray();
            Service<Ro3Emu.Server.Accounts.RoleStore>().SaveDaily(session.Role!.Id,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(session.Daily)); // the stored state the accept commits over
            return Task.CompletedTask;
        });
        Assert.Equal(ErrorCodes.AttemptsExhausted, await AcceptRing(alice));

        await Gm(alice, "!testing unlimited on");
        var synced = (DBDailyActivityInfo)session.Synced[DailyActivityService.ActivitySync];
        Assert.Equal(1, synced.ActivityData[Dodo].ActivityInfo.ActivityParam.RunRingTask.RemainRingCount);
        Assert.Equal(0, await AcceptRing(alice));
    }

    private static async Task<int> AcceptRing(BotClient bot)
    {
        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = Dodo, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        return (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser)).Result;
    }

    [ClientDataFact]
    public void PhasedActivitiesRepeatTheirOwnPhasesBackToBack()
    {
        const int Festival = 3301, FestivalPhases = 1083, MockBattlePhases = 1084;
        Service<TestingSwitches>().AlwaysOpen = true;
        // Tuesday 12:00; Festival Invitation's 1083 is Friday 18:30: Candidate 1 min, Ready 4 min, Answer 25 min.
        var clock = Service<ActivityClock>();
        var festival = clock.Segments(FestivalPhases, Now);
        var running = festival.Where(w => w.Cycle == festival.Single(w => w.Contains(Now)).Cycle).ToArray();
        Assert.Equal(["Candidate", "Ready", "Answer"], running.Select(w => w.State));
        Assert.Equal([60L, 240L, 1500L], running.Select(w => w.End - w.Start));
        Assert.Equal(running[0].End, running[1].Start);
        // The To-Do row's occurrence is that run, so the festival's own phase checks agree with 5004.
        var occurrence = Service<TimedTodoSchedule>().CurrentOrNext(Festival)!;
        Assert.Equal((running[0].Start, running[2].End), (occurrence.Start, occurrence.End));
        // Mock Battle (1084) repeats its first run: Matching 5 min, then Battling 10 min.
        var mock = clock.Segments(MockBattlePhases, Now);
        var run = mock.Where(w => w.Cycle == mock[0].Cycle).ToArray();
        Assert.Equal([("Matching", 300L), ("Battling", 600L)], run.Select(w => (w.State, w.End - w.Start)));
        // An id no To-Do row controls keeps its schedule (1012, the Battle for Prontera).
        Service<TestingSwitches>().AlwaysOpen = false;
        var defense = clock.Segments(1012, Now);
        Service<TestingSwitches>().AlwaysOpen = true;
        Assert.Equal(defense, clock.Segments(1012, Now));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => Zone;
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }
}
