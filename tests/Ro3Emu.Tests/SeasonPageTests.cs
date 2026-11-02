using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>Real gate tests of the season page and server clock; the game client is deliberately not started.</summary>
public sealed class SeasonPageTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-season-page-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly TestClock _clock = new();
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }

    public async Task InitializeAsync()
    {
        var (port, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{port}";
        // The 20 Oct 2026 client closes the season page (function 69, condition 2000062 now [1002,9999]); these tests
        // pin the page's own rules, so they keep it open at the level the 16 Oct client asked for.
        var functions = Service<FunctionService>();
        var original = functions.ConditionOverride;
        functions.ConditionOverride = (s, id) => id == SeasonPageCondition ? s.Progress is { Level: >= 50 } : original?.Invoke(s, id);
    }

    private const long SeasonPageCondition = 2000062;

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true, DBSeasonPage? page = null)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        if (page != null) Service<RoleStore>().SaveSeasonPage(id, page.ToByteArray());
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
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    private static async Task<SeasonPageGetDataResp> Page(BotClient bot)
    {
        await bot.SendGameAsync(SeasonPageService.GetData, new SeasonPageGetDataReq());
        return await Next(bot, SeasonPageService.GetData, SeasonPageGetDataResp.Parser);
    }

    private static async Task<SeasonPageReceiveRankRewardResp> Claim(BotClient bot, params (int Level, int Function)[] claims)
    {
        var request = new SeasonPageReceiveRankRewardReq();
        foreach (var (level, function) in claims) request.RankReward[level] = function;
        await bot.SendGameAsync(SeasonPageService.ReceiveRankReward, request);
        return await Next(bot, SeasonPageService.ReceiveRankReward, SeasonPageReceiveRankRewardResp.Parser);
    }

    [ClientDataFact]
    public async Task AuthoritativeDungeonScoreEventsAreDeduplicatedAndReachTheWire()
    {
        await using var bot = await InWorld("ScoreAlice");
        await Gm(bot, "!lvl 50");
        var page = Service<SeasonPageService>();
        Assert.False(await Service<GameLoop>().InvokeAsync(() => page.RecordDungeonScoreAsync(Session(bot), 0, "room-1")));
        Assert.True(await Service<GameLoop>().InvokeAsync(() => page.RecordDungeonScoreAsync(Session(bot), 100, "room-1")));
        Assert.False(await Service<GameLoop>().InvokeAsync(() => page.RecordDungeonScoreAsync(Session(bot), 100, "room-1")));
        Assert.Equal(100, (await Page(bot)).RankScore[23]);
        Assert.True(await Service<GameLoop>().InvokeAsync(() => page.RecordDungeonScoreAsync(Session(bot), 25, "room-2")));
        Assert.Equal(125, (await Page(bot)).RankScore[23]);
        Assert.Equal(125, Service<RoleStore>().LoadSeasonPageMeta(Session(bot).Role!.Id) is { } saved
            ? System.Text.Json.JsonSerializer.Deserialize<SeasonPageService.PageMeta>(saved)!.DungeonScore : -1);
    }

    [ClientDataFact]
    public async Task OwnedCommemorativeMedalEntersHonorWallAndCanBeWorn()
    {
        await using var bot = await InWorld("HonorAlice");
        await Gm(bot, "!lvl 50");
        await Gm(bot, "!item 1300046 1");
        await Page(bot);
        Assert.True(Session(bot).SeasonPage!.HonorBadgeData.ContainsKey(1300046));
        await bot.SendGameAsync(SeasonPageService.PutOnHonorBadge,
            new SeasonPagePutOnHonorBadgeReq { Id = 1300046, IsPutOn = true });
        Assert.Equal(0, (await Next(bot, SeasonPageService.PutOnHonorBadge, SeasonPagePutOnHonorBadgeResp.Parser)).Result);
        Assert.True(Session(bot).SeasonPage!.HonorBadgeData[1300046].IsPutOn);
        Assert.Equal(Service<SeasonService>().SeasonId, Session(bot).SeasonPage!.HonorBadgeData[1300046].GetSeasonId);
    }

    [ClientDataFact]
    public async Task BadgeAndBreakSnapshotWritesDoNotChangeMemoryBeforeCommit()
    {
        await using var bot = await InWorld("CommitAlice");
        await Gm(bot, "!lvl 50");
        await Gm(bot, "!item 1300046 1");
        var session = Session(bot);
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER season_badge_abort BEFORE UPDATE OF season_page ON role BEGIN SELECT RAISE(ABORT, 'badge test'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Service<GameLoop>().InvokeAsync(
            () => Service<SeasonPageService>().GetDataAsync(session)));
        Assert.Empty(session.SeasonPage!.HonorBadgeData);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER season_badge_abort");
        await Page(bot);
        Assert.True(session.SeasonPage!.HonorBadgeData.ContainsKey(1300046));

        await Gm(bot, "!item 11 200");
        _clock.Set(Service<SeasonService>().Times().SeasonBreakStartTime);
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER season_snapshot_abort BEFORE UPDATE OF season_page_meta ON role BEGIN SELECT RAISE(ABORT, 'snapshot test'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Service<GameLoop>().InvokeAsync(
            () => Service<SeasonPageService>().GetDataAsync(session)));
        Assert.True(session.SeasonPage!.HistorySnapshot == null || session.SeasonPage.HistorySnapshot.FunctionScore.Count == 0);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER season_snapshot_abort");
        await Page(bot);
        Assert.Equal(200, session.SeasonPage!.HistorySnapshot!.FunctionScore[17]);
    }

    [ClientDataFact]
    public async Task ExplicitCycleArchivesScoresAndReissuesUnclaimedRankOnce()
    {
        var bot = await InWorld("CycleAlice");
        await Gm(bot, "!lvl 50");
        await Gm(bot, "!item 11 200");
        Assert.Equal(200, (await Page(bot)).RankScore[17]);
        var oldId = Session(bot).Role!.Id;
        long breakTime = Service<SeasonService>().Times().SeasonBreakStartTime;
        _clock.Set(breakTime);
        Assert.Equal(101005, (await Page(bot)).StageID);
        Assert.Equal(200, Session(bot).SeasonPage!.HistorySnapshot.FunctionScore[17]);
        Assert.Contains("cannot precede", Service<SeasonService>().AdvanceCycle(101, Service<SeasonService>().Start.AddDays(1)));
        Assert.Equal(1, Service<SeasonService>().SeasonId);
        var nextStart = FirstLocalMidnightAtOrAfter(breakTime);
        _clock.Set(new DateTimeOffset(nextStart).ToUnixTimeSeconds());
        string result = Service<SeasonService>().AdvanceCycle(101, nextStart);
        Assert.Contains("Season 2", result);
        Assert.Equal(2, Service<SeasonService>().SeasonId);
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Page(bot)).Result); // old login cannot claim new cycle
        await bot.DisposeAsync();

        var again = await InWorld("CycleAlice", create: false);
        Assert.Equal(oldId, Session(again).Role!.Id);
        var season = await Next(again, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser);
        Assert.Equal(2, season.Current.All.SeasonId);
        Assert.True(season.History.ContainsKey(1));
        var page = Session(again).SeasonPage!;
        Assert.Equal(200, page.HistoryFunctionScoreData[1].FunctionScore[17]);
        Assert.Empty(page.RankRewardReceiveInfo);
        Assert.Equal(0, (await Page(again)).RankScore[17]);
        var list = await again.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
        { ["Uid"] = oldId.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        var letters = (object?[])list["MailList"]!;
        Assert.Single(letters);
        var row = (Dictionary<object, object?>)letters[0]!;
        var parameters = (Dictionary<object, object?>)row["Params"]!;
        Assert.Equal(1081L, Convert.ToInt64(parameters["cfg_id"]));
        Assert.NotEmpty(MailItemList.Parser.ParseFrom((byte[])parameters["items"]!).Items);
        await again.DisposeAsync();
        await using var third = await InWorld("CycleAlice", create: false);
        list = await third.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
        { ["Uid"] = oldId.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        Assert.Single((object?[])list["MailList"]!);
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
        db.Open();
        using var receipt = db.CreateCommand();
        receipt.CommandText = """
            SELECT COUNT(*) FROM operation_receipt
            WHERE command_kind='cd13c/season-reissue' AND source_key=$source
            """;
        receipt.Parameters.AddWithValue("$source", $"season-rank-reissue:{oldId}:1");
        Assert.Equal(1L, (long)receipt.ExecuteScalar()!); // returning to an archived epoch cannot mint again
        receipt.CommandText = "SELECT COUNT(*) FROM economy_entry WHERE source_key=$source AND leg_kind=$kind";
        receipt.Parameters.AddWithValue("$kind", (int)EconomyLegKind.Source);
        Assert.True((long)receipt.ExecuteScalar()! > 0); // the retained mail goods have source legs
    }

    [ClientDataFact]
    public async Task RolloverMailboxFailureRollsBackPageAndRetriesOnce()
    {
        await using var bot = await InWorld("RollbackAlice");
        await Gm(bot, "!lvl 50");
        await Gm(bot, "!item 11 200");
        var session = Session(bot);
        long role = session.Role!.Id;
        var seasons = Service<SeasonService>();
        long breakTime = seasons.Times().SeasonBreakStartTime;
        _clock.Set(breakTime);
        var nextStart = FirstLocalMidnightAtOrAfter(breakTime);
        _clock.Set(new DateTimeOffset(nextStart).ToUnixTimeSeconds());
        Assert.Contains("Season 2", seasons.AdvanceCycle(101, nextStart));
        seasons.Load(session); // emulate the new login identity before its page sync
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER season_page_abort BEFORE UPDATE OF season_page_meta ON role BEGIN SELECT RAISE(ABORT, 'season page test'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Service<GameLoop>().InvokeAsync(
            () => Service<SeasonPageService>().LoginAsync(session)));
        Assert.Empty(Service<RoleStore>().LoadSeasonPage(role) is { } page
            ? DBSeasonPage.Parser.ParseFrom(page).HistoryFunctionScoreData : new DBSeasonPage().HistoryFunctionScoreData);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER season_page_abort");
        await Service<GameLoop>().InvokeAsync(() => Service<SeasonPageService>().LoginAsync(session));
        Assert.Equal(200, session.SeasonPage!.HistoryFunctionScoreData[1].FunctionScore[17]);
        var list = await bot.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
        { ["Uid"] = role.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        Assert.Single((object?[])list["MailList"]!);
    }

    [ClientDataFact]
    public async Task LoginSuppliesFiveClockStagesAndTrulyEmptyPageMaps()
    {
        await using var bot = await InWorld("Alice");
        var season = await Next(bot, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser);
        Assert.True(season.SyncAllMark);
        Assert.Equal(101, season.Current.All.PlayBookId);
        Assert.Equal(101001, season.Current.All.StageId);
        var time = await Next(bot, SeasonService.TimeInfoNotify, SeasonTimeInfoNotify.Parser);
        long start = new DateTimeOffset(Service<SeasonService>().Start).ToUnixTimeSeconds();
        Assert.Equal(start, time.SeasonStartTime);
        Assert.Equal(5, time.StageTimeInfo.Count);
        Assert.Equal(start + 7 * 86400, time.StageTimeInfo[101001].EndTime);
        Assert.Equal(time.StageTimeInfo[101001].EndTime, time.StageTimeInfo[101002].StartTime);
        Assert.Equal(start + 14 * 86400, time.StageTimeInfo[101002].EndTime);
        Assert.Equal(time.StageTimeInfo[101002].EndTime, time.StageTimeInfo[101003].StartTime);
        Assert.Equal(start + 28 * 86400, time.StageTimeInfo[101003].EndTime);
        Assert.Equal(time.StageTimeInfo[101003].EndTime, time.StageTimeInfo[101004].StartTime);
        Assert.Equal(start + 77 * 86400, time.SeasonBreakStartTime); // explicit inferred first-season policy
        Assert.Equal(time.SeasonBreakStartTime, time.StageTimeInfo[101004].EndTime);
        Assert.Equal(time.SeasonBreakStartTime, time.StageTimeInfo[101005].StartTime);
        Assert.Equal(time.SeasonBreakStartTime + 999999999, time.StageTimeInfo[101005].EndTime);
        Assert.Equal(season.Current.All.EndTime, time.StageTimeInfo[101005].EndTime);
        Assert.Equal(season.Current.All.BreakTime, time.SeasonBreakStartTime);
        Assert.Equal(time.SeasonBreakStartTime - 168 * 3600, time.ShowCountDownTime);
        Assert.Equal(0, time.NextSeasonTime); // no fabricated next-season scheduling

        var page = await Next(bot, SeasonPageService.PageSync, DBSeasonPageIncrement.Parser);
        Assert.True(page.SyncAllMark);
        Assert.True(Assert.Single(page.RankRewardReceiveInfo).Value.Del);
        Assert.True(Assert.Single(page.HistoryFunctionScoreData).Value.Del);
        Assert.True(Assert.Single(page.HonorBadgeData).Value.Del);
        Assert.True(Assert.Single(page.MailRewardReceiveInfo).Value.Del);
        await OnLoop(() =>
        {
            var real = Session(bot).SeasonPage!;
            Assert.Empty(real.RankRewardReceiveInfo); Assert.Empty(real.HonorBadgeData);
            Assert.Empty(real.HistoryFunctionScoreData); Assert.Empty(real.MailRewardReceiveInfo);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task ClockPushesStateAndTimeAtTheBoundaryWithoutARequest()
    {
        await using var bot = await InWorld("Alice");
        var initial = await Next(bot, SeasonService.TimeInfoNotify, SeasonTimeInfoNotify.Parser);
        long boundary = initial.StageTimeInfo[101001].EndTime;
        Assert.Equal(101001, Service<SeasonService>().StageAt(boundary - 1));
        Assert.Equal(101002, Service<SeasonService>().StageAt(boundary));
        _clock.Set(boundary);
        var change = await Next(bot, SeasonService.StateChangeNotify, SeasonStateChangeNotify.Parser);
        Assert.Equal((1L, 101L, 101001L), (change.OldSeasonState.SeasonId, change.OldSeasonState.PlayBookId, change.OldSeasonState.StageId));
        Assert.Equal(101002, change.NewSeasonState.StageId);
        Assert.Equal(initial, await Next(bot, SeasonService.TimeInfoNotify, SeasonTimeInfoNotify.Parser));
        var updated = await Next(bot, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser, p => !p.SyncAllMark);
        Assert.Equal(101002, updated.Current.Inc.StageId);
        await OnLoop(async () =>
        {
            long version = Session(bot).SyncVersions[SeasonService.SeasonSync];
            await Service<SeasonService>().ReconcileAsync(Session(bot));
            Assert.Equal(version, Session(bot).SyncVersions[SeasonService.SeasonSync]);
        });
        await Gm(bot, "!lvl 50");
        Assert.Equal(101002, (await Page(bot)).StageID);
    }

    [ClientDataTheory]
    [InlineData("2026-10-05", "2026-10-19")]
    [InlineData("2026-10-08", "2026-10-26")]
    [InlineData("2026-10-11", "2026-10-26")]
    public void InferredTypeSevenAlignsToMondayAndTypeEightAddsDuration(string firstDay, string stageTwoEnd)
    {
        var start = DateTime.SpecifyKind(DateTime.Parse(firstDay, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Local);
        var times = new SeasonClock(Service<GameData>(), start, 101, TimeZoneInfo.Local).Build();
        var end = DateTime.SpecifyKind(DateTime.Parse(stageTwoEnd, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Local);
        Assert.Equal(new DateTimeOffset(end).ToUnixTimeSeconds(), times.StageTimeInfo[101002].EndTime);
        Assert.Equal(1209600, times.StageTimeInfo[101003].EndTime - times.StageTimeInfo[101003].StartTime);
    }

    [ClientDataFact]
    public async Task GmPlaybookChangesApplyAtLoginWhileExistingSeasonKeepsAdvancing()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 50"); await Gm(alice, "!item 11 100");
        var times = await Next(alice, SeasonService.TimeInfoNotify, SeasonTimeInfoNotify.Parser);
        await Gm(alice, "!season 102");
        Assert.Equal(0, (await Page(alice)).Result);
        Assert.Equal(0, (await Claim(alice, (2, 17))).Result);
        await using var bob = await InWorld("Bob");
        await Gm(bob, "!lvl 50");
        var login = await Next(bob, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser);
        Assert.Equal(102, login.Current.All.PlayBookId);
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Page(bob)).Result);

        _clock.Set(times.StageTimeInfo[101001].EndTime);
        var change = await Next(alice, SeasonService.StateChangeNotify, SeasonStateChangeNotify.Parser);
        Assert.Equal(101, change.NewSeasonState.PlayBookId);
        Assert.Equal(101002, change.NewSeasonState.StageId);
        Assert.Equal(101002, (await Page(alice)).StageID);
        await Gm(alice, "!season 101");
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Page(bob)).Result);
        Assert.Equal(102, Session(bob).SeasonState!.PlayBookId);
    }

    [Fact]
    public async Task PageMessagesBeforeRoleSelectionAreRefusedUnanswered()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        await StageGate.AssertUnansweredAsync(bot, SeasonPageService.GetData, new SeasonPageGetDataReq());
        var claim = new SeasonPageReceiveRankRewardReq();
        claim.RankReward[2] = 17;
        await StageGate.AssertUnansweredAsync(bot, SeasonPageService.ReceiveRankReward, claim);
        await StageGate.AssertUnansweredAsync(bot, SeasonPageService.PutOnHonorBadge, new SeasonPagePutOnHonorBadgeReq { Id = 1300046, IsPutOn = true });
    }

    [ClientDataFact]
    public async Task RankPageUsesEarnedPointsAndTheClientDanThresholds()
    {
        await using var bot = await InWorld("Alice");
        Assert.Equal(ErrorCodes.FunctionLocked, (await Page(bot)).Result);
        await Gm(bot, "!lvl 49");
        Assert.Equal(ErrorCodes.FunctionLocked, (await Page(bot)).Result);
        await Gm(bot, "!lvl 50");
        var open = await Next(bot, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
            x => x.FunctionOpenInfos.TryGetValue(69, out var f) && f.All);
        Assert.True(open.FunctionOpenInfos[69].All);
        var page = await Page(bot);
        Assert.Equal(0, page.Result);
        Assert.Equal([17, 23, 36, 60, 65, 67], page.RankScore.Keys.Order());
        Assert.All(page.RankScore.Values, score => Assert.Equal(0, score));
        await Gm(bot, "!item 11 100");
        await Gm(bot, "!item 7 99");
        page = await Page(bot);
        Assert.Equal(100, page.RankScore[17]); Assert.Equal(99, page.RankScore[65]);
        Assert.Equal(0, page.RankScore[23]);
        var service = Service<SeasonPageService>();
        Assert.Equal(1, service.DanLevel(17, 99)); Assert.Equal(2, service.DanLevel(17, 100));
        Assert.Equal(3, service.DanLevel(17, 200)); Assert.Equal(26, service.DanLevel(17, long.MaxValue));
    }

    [ClientDataFact]
    public async Task ClaimsGrantConfiguredItemsOnceAndPersistAcrossLogin()
    {
        long roleId;
        await using (var bot = await InWorld("Alice"))
        {
            roleId = (long)bot.Self!.Uuid;
            Assert.Equal(ErrorCodes.FunctionLocked, (await Claim(bot, (2, 17))).Result);
            await Gm(bot, "!lvl 50");
            Assert.Equal(ErrorCodes.SeasonRankReward, (await Claim(bot, (2, 17))).Result);
            await Gm(bot, "!item 11 200");
            Assert.Equal(ErrorCodes.Parameter, (await Claim(bot)).Result);
            Assert.Equal(ErrorCodes.SeasonRankReward, (await Claim(bot, (2, 999))).Result);
            Assert.Equal(ErrorCodes.SeasonRankReward, (await Claim(bot, (999, 17))).Result);
            Assert.Equal(ErrorCodes.RewardMissing, (await Claim(bot, (1, 17))).Result);
            int oreBefore = Session(bot).Items.Where(i => i.Entry == 1200000).Sum(i => i.Stack);
            int enrichedBefore = Session(bot).Items.Where(i => i.Entry == 1200001).Sum(i => i.Stack);
            Assert.Equal(0, (await Claim(bot, (2, 17), (3, 17))).Result);
            var claimed = await Next(bot, SeasonPageService.PageSync, DBSeasonPageIncrement.Parser, x => !x.SyncAllMark);
            Assert.Equal(17, Assert.Single(claimed.RankRewardReceiveInfo[2].All.Functions));
            Assert.Equal(17, Assert.Single(claimed.RankRewardReceiveInfo[3].All.Functions));
            // AwardConfig 10050102 and 10050103: two then three of each ore, binding 0.
            Assert.Equal(oreBefore + 5, Session(bot).Items.Where(i => i.Entry == 1200000).Sum(i => i.Stack));
            Assert.Equal(enrichedBefore + 5, Session(bot).Items.Where(i => i.Entry == 1200001).Sum(i => i.Stack));
            Assert.All(Session(bot).Items.Where(i => i.Entry is 1200000 or 1200001), i => Assert.Equal(0, i.BindType));
            var itemsBeforeDuplicate = Service<ItemStore>().Load(roleId).Select(i => (i.Guid, i.Entry, i.Stack)).OrderBy(x => x.Guid).ToArray();
            Assert.Equal(ErrorCodes.RewardClaimed, (await Claim(bot, (2, 17))).Result);
            Assert.Equal(itemsBeforeDuplicate, Service<ItemStore>().Load(roleId).Select(i => (i.Guid, i.Entry, i.Stack)).OrderBy(x => x.Guid).ToArray());
        }
        await using var again = await InWorld("Alice", create: false);
        var saved = await Next(again, SeasonPageService.PageSync, DBSeasonPageIncrement.Parser);
        Assert.Equal(17, Assert.Single(saved.RankRewardReceiveInfo[2].All.Functions));
        Assert.Equal(ErrorCodes.RewardClaimed, (await Claim(again, (2, 17))).Result);
    }

    [ClientDataFact]
    public async Task SuspendedRankClaimKeepsRewardPrivateAndDefersSameRoleGm()
    {
        await using var bot = await InWorld("HeldRank");
        await Gm(bot, "!lvl 50");
        await Gm(bot, "!item 11 100");
        var session = Session(bot);
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var before = S3EconomyReader.Read(_dataDir, catalog);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(() =>
        {
            Service<AccountStore>().BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/cd13c/season-page-claim") return Task.CompletedTask;
                staged.TrySetResult();
                return release.Task;
            };
            Service<OwnerRegistry>().DeferredForTest = origin =>
            { if (origin == "GM") deferred.TrySetResult(); };
            return Task.CompletedTask;
        });
        var claim = Claim(bot, (2, 17));
        Task? gm = null;
        try
        {
            await staged.Task.WaitAsync(Wait);
            Assert.Empty(session.SeasonPage!.RankRewardReceiveInfo);
            Assert.DoesNotContain(session.Items, item => item.Entry is 1200000 or 1200001);
            gm = Gm(bot, "!item 2 1");
            await deferred.Task.WaitAsync(Wait);
            Assert.False(gm.IsCompleted);
            Assert.False(claim.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await OnLoop(() =>
            {
                Service<AccountStore>().BeforeOwnedCommitForTest = null;
                Service<OwnerRegistry>().DeferredForTest = null;
                return Task.CompletedTask;
            });
        }
        Assert.Equal(0, (await claim.WaitAsync(Wait)).Result);
        await gm!.WaitAsync(Wait);
        Assert.Equal(2, session.Items.Where(item => item.Entry == 1200000).Sum(item => item.Stack));
        Assert.Contains(17, session.SeasonPage!.RankRewardReceiveInfo[2].Functions);
        var window = S3EconomyReader.ReadLedger(_dataDir, catalog, before, S3EconomyReader.Read(_dataDir, catalog))!;
        // The deferred GM grant commits its own 13a receipt in the same window; the claim has exactly one.
        var receipt = Assert.Single(window.Receipts, r => r.CommandKind == "cd13c/season-page-claim");
        Assert.Equal("cd13c/season-page-claim",
            EconomyReceiptReader.Read(Path.Combine(_dataDir, "ro3emu.db"), receipt.OperationId)!.CommandKind);
        Assert.Contains(window.Legs, leg => leg.Kind == EconomyLegKind.Source && leg.Asset.Id == 1200000 && leg.Delta == 2);
    }

    [ClientDataFact]
    public async Task LostRankClaimAcknowledgementResolvesOneRewardAndOneSource()
    {
        await using var bot = await InWorld("AckRank");
        await Gm(bot, "!lvl 50");
        await Gm(bot, "!item 11 100");
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var before = S3EconomyReader.Read(_dataDir, catalog);
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        Assert.Equal(0, (await Claim(bot, (2, 17))).Result);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Claim(bot, (2, 17))).Result);
        Assert.Equal(2, Session(bot).Items.Where(item => item.Entry == 1200000).Sum(item => item.Stack));
        var window = S3EconomyReader.ReadLedger(_dataDir, catalog, before, S3EconomyReader.Read(_dataDir, catalog))!;
        var receipt = Assert.Single(window.Receipts);
        Assert.Equal("cd13c/season-page-claim",
            EconomyReceiptReader.Read(Path.Combine(_dataDir, "ro3emu.db"), receipt.OperationId)!.CommandKind);
        Assert.Single(window.Legs, leg => leg.Kind == EconomyLegKind.Source && leg.Asset.Id == 1200000);
    }

    [ClientDataFact]
    public async Task ClaimsShareThreeFunctionLimitAndRejectMixedInvalidBatchesAtomically()
    {
        await using var bot = await InWorld("Alice");
        await Gm(bot, "!lvl 50");
        foreach (int currency in new[] { 11, 12, 13, 14 }) await Gm(bot, $"!item {currency} 200");
        Assert.Equal(ErrorCodes.SeasonRankReward, (await Claim(bot, (2, 17), (3, 23))).Result);
        Assert.Empty(Session(bot).SeasonPage!.RankRewardReceiveInfo);
        foreach (int function in new[] { 17, 67, 60 }) Assert.Equal(0, (await Claim(bot, (2, function))).Result);
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var beforeCapRefusal = S3EconomyReader.Read(_dataDir, catalog);
        Assert.Equal(ErrorCodes.SeasonRankReward, (await Claim(bot, (2, 36))).Result);
        Assert.Equal(beforeCapRefusal.Cut.CommitSequence, S3EconomyReader.Read(_dataDir, catalog).Cut.CommitSequence);
        Assert.Equal(3, Session(bot).SeasonPage!.RankRewardReceiveInfo[2].Functions.Count);
        Assert.Equal(0, (await Claim(bot, (3, 36))).Result); // another dan has its own pool
    }

    [ClientDataFact]
    public async Task HonorWearChecksOwnershipTwoBadgeLimitAndPersistence()
    {
        // Persisted earned-award fixture, not a fabricated client claim. Award acquisition is a separate system.
        var state = new DBSeasonPage();
        foreach (int id in new[] { 1300046, 1300047, 1300048 })
            state.HonorBadgeData[id] = new() { Id = id, GetTime = _clock.GetUtcNow().ToUnixTimeSeconds(), GetSeasonId = 1, GetPlayBookId = 101 };
        async Task<int> Wear(BotClient bot, int id, bool on)
        {
            await bot.SendGameAsync(SeasonPageService.PutOnHonorBadge, new SeasonPagePutOnHonorBadgeReq { Id = id, IsPutOn = on });
            return (await Next(bot, SeasonPageService.PutOnHonorBadge, SeasonPagePutOnHonorBadgeResp.Parser)).Result;
        }
        await using (var bot = await InWorld("Alice", page: state))
        {
            await Gm(bot, "!lvl 50");
            Assert.Equal(ErrorCodes.ItemMissing, await Wear(bot, 999, true));
            Assert.Equal(0, await Wear(bot, 1300046, true));
            var updated = await Next(bot, SeasonPageService.PageSync, DBSeasonPageIncrement.Parser, m => !m.SyncAllMark);
            Assert.True(updated.HonorBadgeData[1300046].Inc.IsPutOn);
            Assert.Equal(0, await Wear(bot, 1300047, true));
            Assert.Equal(0, await Wear(bot, 1300047, true)); // idempotent at the limit
            Assert.Equal(ErrorCodes.Parameter, await Wear(bot, 1300048, true));
            Assert.Equal(0, await Wear(bot, 1300046, false));
            Assert.Equal(0, await Wear(bot, 1300048, true));
        }
        await using var again = await InWorld("Alice", create: false);
        var full = await Next(again, SeasonPageService.PageSync, DBSeasonPageIncrement.Parser);
        Assert.False(full.HonorBadgeData[1300046].All.IsPutOn);
        Assert.True(full.HonorBadgeData[1300047].All.IsPutOn);
        Assert.True(full.HonorBadgeData[1300048].All.IsPutOn);
        Assert.Equal(1, full.HonorBadgeData[1300048].All.GetSeasonId);
    }

    [ClientDataFact]
    public async Task ClaimStorageFailureRollsBackRewardsAndAllowsOneRetry()
    {
        await using var bot = await InWorld("Alice");
        await Gm(bot, "!lvl 50"); await Gm(bot, "!item 11 100");
        long role = (long)bot.Self!.Uuid;
        var before = Service<ItemStore>().Load(role).Select(i => (i.Guid, i.Entry, i.Stack)).OrderBy(x => x.Guid).ToArray();
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER fail_season_page BEFORE UPDATE OF season_page ON role BEGIN SELECT RAISE(ABORT, 'season page fault injection'); END");
        var request = new SeasonPageReceiveRankRewardReq { RankReward = { [2] = 17 } };
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => OnLoop(() => Service<SeasonPageService>().ClaimAsync(Session(bot), request)));
        Assert.Empty(Session(bot).SeasonPage!.RankRewardReceiveInfo);
        Assert.Equal(before, Service<ItemStore>().Load(role).Select(i => (i.Guid, i.Entry, i.Stack)).OrderBy(x => x.Guid).ToArray());
        Assert.Equal(before, Session(bot).Items.Select(i => (i.Guid, i.Entry, i.Stack)).OrderBy(x => x.Guid).ToArray());
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_season_page");
        Assert.Equal(0, (await Claim(bot, (2, 17))).Result);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Claim(bot, (2, 17))).Result);
        Assert.Equal(2, Session(bot).Items.Where(i => i.Entry == 1200000).Sum(i => i.Stack));
    }

    private static DateTime FirstLocalMidnightAtOrAfter(long unixSeconds)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.Date;
        return new DateTimeOffset(day).ToUnixTimeSeconds() < unixSeconds ? day.AddDays(1) : day;
    }


    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root");
    }
}
