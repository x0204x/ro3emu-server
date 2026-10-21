using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>Core activity wire, schedule, replay and transaction checks.</summary>
public sealed partial class NewActivityTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-activities-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly TestClock _clock = new();
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Ro3Emu.slnx")))
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "data", "config"))) return directory.FullName;
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("RO3 repository root");
    }

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void AdvanceDays(int count) => Interlocked.Add(ref _seconds, count * 86400L);
        public void SetLocal(int year, int month, int day, int hour, int minute, int second = 0)
        {
            var local = new DateTime(year, month, day, hour, minute, second);
            Interlocked.Exchange(ref _seconds, new DateTimeOffset(local, LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds());
        }
        public void AdvanceSeconds(int count) => Interlocked.Add(ref _seconds, count);
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task Reconcile(BotClient bot) => Service<NewActivityService>().ReconcileAsync(Session(bot));

    private static long _serial;
    private static async Task<CommonActivityRes> Common(BotClient bot, CommonActivityReq req)
    {
        req.RequestId = Interlocked.Increment(ref _serial);
        await bot.SendGameAsync(NewActivityService.Common, req);
        return await Next(bot, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == req.RequestId);
    }

    [ClientDataFact]
    public async Task SevenDayCountsDistinctLoginsAndClaimsPersist()
    {
        var bot = await InWorld("LoginDays");
        var initial = await Next(bot, NewActivityService.ActivitySync, DBNewActivityInfoIncrement.Parser);
        Assert.True(initial.ActivityInfo.ContainsKey(113));
        await bot.SendGameAsync(NewActivityService.GetReward, new NewActivityGetRewardCommonReq { ActivityID = 113, Index = 1 });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(bot, NewActivityService.GetReward, NewActivityGetRewardCommonResp.Parser)).Result);
        await Gm(bot, "!lvl 30");
        await Reconcile(bot);
        Assert.Equal(1, Session(bot).NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.LoginDays);
        await bot.SendGameAsync(NewActivityService.GetReward, new NewActivityGetRewardCommonReq { ActivityID = 113, Index = 1 });
        Assert.Equal(0, (await Next(bot, NewActivityService.GetReward, NewActivityGetRewardCommonResp.Parser)).Result);
        await bot.SendGameAsync(NewActivityService.GetReward, new NewActivityGetRewardCommonReq { ActivityID = 113, Index = 1 });
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(bot, NewActivityService.GetReward, NewActivityGetRewardCommonResp.Parser)).Result);
        await bot.DisposeAsync();
        await using var again = await InWorld("LoginDays", false);
        Assert.True(Session(again).NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.RewardStatus[1]);
        Assert.Equal(1, Session(again).NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.LoginDays);
        _clock.AdvanceDays(3);
        await Reconcile(again);
        Assert.Equal(2, Session(again).NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.LoginDays);
        await again.SendGameAsync(NewActivityService.GetReward, new NewActivityGetRewardCommonReq { ActivityID = 113, Index = 3 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(again, NewActivityService.GetReward, NewActivityGetRewardCommonResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task LevelClaimsValidateTypeThresholdAndSourceTransaction()
    {
        await using var bot = await InWorld("LevelClaims");
        await Gm(bot, "!lvl 30"); await Reconcile(bot);
        var req = new CommonActivityReq { ActivityId = 103, LevelAwardGetReward = new GetThreeDayLevelAwardReq { LevelAwardCfgId = 1 } };
        var premature = new CommonActivityReq { ActivityId = 103, LevelAwardGetReward = new GetThreeDayLevelAwardReq { LevelAwardCfgId = 2 } };
        Assert.Equal(ErrorCodes.TaskRequirements, (await Common(bot, premature)).Result);
        req.ActivityId = 113;
        Assert.Equal(ErrorCodes.Parameter, (await Common(bot, req)).Result);
        req.ActivityId = 103;
        Assert.Equal(0, (await Common(bot, req)).Result);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Common(bot, req)).Result);
        await Gm(bot, "!lvl 40"); await Reconcile(bot);
        var before = Session(bot).NewActivities!.ToByteArray();
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER activity_test_rollback BEFORE UPDATE OF new_activities ON role BEGIN SELECT RAISE(ABORT,'activity-source-test'); END");
        // Model the dispatcher's admission even though this call observes the SQL exception directly.
        // Taking ActivityGate first can deadlock with an already-owned calendar reconciliation.
        var session = Session(bot);
        var loop = Service<GameLoop>();
        var owners = Service<OwnerRegistry>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = loop.InvokeAsync(() => owners.RunAsync("activity test blocker", [ReservationKey.Realm], async () =>
        {
            owners.DeferredForTest = origin => { if (origin == "activity test request") deferred.TrySetResult(); };
            entered.TrySetResult();
            await release.Task;
        }));
        await entered.Task.WaitAsync(Wait);
        var claim = loop.InvokeAsync(() => owners.RunSessionAsync("activity test request", [ReservationKey.Realm], session,
            () => Service<NewActivityService>().CommonAsync(session, premature)));
        try
        {
            await deferred.Task.WaitAsync(Wait);
            await loop.InvokeAsync(() =>
            {
                Assert.Equal(1, session.ActivityGate.CurrentCount);
                Assert.False(claim.IsCompleted);
                return Task.CompletedTask;
            });
        }
        finally
        {
            release.TrySetResult();
            await loop.InvokeAsync(() => { owners.DeferredForTest = null; return Task.CompletedTask; });
        }
        await blocker.WaitAsync(Wait);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => claim.WaitAsync(Wait));
        Assert.Equal(before, Session(bot).NewActivities!.ToByteArray());
        Assert.False(Session(bot).NewActivities!.ActivityInfo[103].ActivityParam.LevelAward.AwardData.ContainsKey(2));
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER activity_test_rollback");
        Assert.Equal(0, (await Common(bot, premature)).Result);
    }

    [ClientDataFact]
    public async Task FunctionPreviewRequiresItsUnlockAndPersistsSingleClaim()
    {
        await using var bot = await InWorld("Preview");
        await Gm(bot, "!lvl 30"); await Reconcile(bot);
        var req = new CommonActivityReq
        {
            ActivityId = 104,
            FunctionPreviewGetReward = new GetThreeDayFunctionPreviewAwardReq { FunctionPreviewCfgId = 4 }
        };
        Assert.Equal(0, (await Common(bot, req)).Result);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Common(bot, req)).Result);
        req.FunctionPreviewGetReward.FunctionPreviewCfgId = 9; // the configured level40 preview
        Assert.Equal(ErrorCodes.TaskRequirements, (await Common(bot, req)).Result);
        var saved = Service<RoleStore>().LoadNewActivities(Session(bot).Role!.Id);
        Assert.NotNull(saved);
        Assert.True(DBNewActivityInfo.Parser.ParseFrom(saved).ActivityInfo[104].ActivityParam.FunctionPreview.AwardData.ContainsKey(4));
    }

    [ClientDataFact]
    public async Task LoggedOutConnectionCannotClaimBeforeReenteringWorld()
    {
        await using var bot = await InWorld("LogoutClaim");
        await Gm(bot, "!lvl 30"); await Reconcile(bot);
        var session = Session(bot);
        long roleId = session.Role!.Id;
        var request = new CommonActivityReq
        {
            ActivityId = 103,
            LevelAwardGetReward = new GetThreeDayLevelAwardReq { LevelAwardCfgId = 1 }
        };
        Assert.False(session.NewActivities!.ActivityInfo[103].ActivityParam.LevelAward.AwardData.ContainsKey(1));
        await bot.SendGameAsync(MsgId.Logout, new LogoutReq());
        await Next(bot, MsgId.Logout, LogoutResp.Parser);
        Assert.False(session.ActivitiesReady);
        await StageGate.AssertUnansweredAsync(bot, NewActivityService.Common, request);
        await StageGate.AssertUnansweredAsync(bot, NewActivityService.GetReward, new NewActivityGetRewardCommonReq { ActivityID = 113, Index = 1 });
        Assert.False(session.NewActivities.ActivityInfo[103].ActivityParam.LevelAward.AwardData.ContainsKey(1));
        Assert.Empty(session.NewActivities.ActivityInfo[113].ActivityParam.SevenDay.RewardStatus);
        await bot.EnterWorldAsync(roleId);
        Assert.True(Session(bot).ActivitiesReady);
        Assert.Equal(0, (await Common(bot, request)).Result);
    }

    [ClientDataFact]
    public async Task CalendarUsesSeasonAnchorAndAllApplicableRows()
    {
        await using var bot = await InWorld("Calendar");
        await bot.SendGameAsync(NewActivityService.GetList, new GetActivityListReq());
        var list = await Next(bot, NewActivityService.GetList, GetActivityListResp.Parser);
        Assert.Equal(0, list.Result);
        var data = Service<Ro3Emu.Server.Data.GameData>();
        var expected = data.Table("NewActivityConfig").Values.Where(r => r.GetProperty("_kMultiverseArray") is var books
            && books.ValueKind == JsonValueKind.Array && books.EnumerateArray().Any(v => v.GetInt32() is 0 or 101))
            .Select(r => r.GetProperty("_iID").GetInt64()).Order().ToArray();
        Assert.Equal(expected, list.ActivityList.Select(a => a.Id).Order().ToArray());
        var calendar = Service<ActivityClock>();
        var month = list.ActivityList.Single(a => a.Id == 199);
        Assert.Equal(calendar.ServerOpen, month.BeginTime);
        var pass = list.ActivityList.Single(a => a.Id == 200);
        Assert.Equal(calendar.ServerOpen, pass.BeginTime);
        Assert.Equal(15 * 86400, pass.EndTime - pass.BeginTime);
        var friday = new DateTimeOffset(2026, 10, 9, 18, 32, 0, _clock.LocalTimeZone.GetUtcOffset(new DateTime(2026, 10, 9))).ToUnixTimeSeconds();
        Assert.Equal("Ready", calendar.Segments(1083, friday).Single(w => w.Contains(friday)).State);
    }

    private sealed class ZonedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [ClientDataTheory]
    [InlineData(2026, 10, 23)]
    [InlineData(2026, 10, 30)]
    [InlineData(2027, 4, 2)]
    public void WeeklyFestivalReanchorsToClientCivilWeekAcrossDst(int year, int month, int day)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var civil = new DateTime(year, month, day, 18, 32, 0);
        var now = new DateTimeOffset(civil, zone.GetUtcOffset(civil));
        var calendar = new ActivityClock(Service<Ro3Emu.Server.Data.GameData>(), Service<SeasonService>(), new ZonedClock(now, zone));
        var ready = Assert.Single(calendar.Segments(1083, now.ToUnixTimeSeconds()), w => w.Contains(now.ToUnixTimeSeconds()));
        Assert.Equal("Ready", ready.State);
        Assert.Equal(civil.Date.AddHours(18).AddMinutes(31), TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ready.Start), zone).DateTime);
        Assert.Equal(civil.Date.AddHours(18).AddMinutes(35), TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ready.End), zone).DateTime);
        Assert.False(ready.Contains(ready.End));
    }
}
