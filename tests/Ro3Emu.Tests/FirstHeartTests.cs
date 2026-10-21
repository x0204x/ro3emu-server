using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Seven-Day Goal (FirstHeartService): 5049 starts at base level 30 (NewActivityConfig 114, condition 21000012; level 10
/// before the 20 Oct 2026 client) with day 1 open; 83500 pays a completed task once and scores its point, 83501 a score reward once the score reaches it; the state
/// survives a relogin. Day 1 tasks: 100100200 (level 10), 101101100 (1 login day), 102101110 (a quality-3 card).
/// </summary>
public sealed class FirstHeartTests : IAsyncLifetime
{
    private const long LevelTen = 100100200, LevelTwenty = 100100201, LevelFifty = 100100206, LoginOne = 101101100,
        LoginSeven = 101101106, CardTask = 102101110;
    private const int OpenLevel = 30; // condition 21000012
    private const int Zeny = 1;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-firstheart-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
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

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private Task<FirstHeartTaskCfgData?> Cfg(GameSession s) => Read(() => s.FirstHeart?.ActivityData.GetValueOrDefault(1)?.Clone());

    private async Task Until(GameSession s, Func<FirstHeartTaskCfgData, bool> reached, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (await Cfg(s) is { } cfg && reached(cfg)) return;
            await Task.Delay(100);
        }
        Assert.Fail("Seven-Day Goal never reached: " + what);
    }

    private static FirstHeartTaskState Status(FirstHeartTaskCfgData cfg, long task) =>
        cfg.Tasks.GetValueOrDefault(task)?.Status ?? (FirstHeartTaskState)(-1);

    private async Task<int> ClaimTask(BotClient bot, long task, int type = 1)
    {
        await bot.SendGameAsync(FirstHeartService.GetTaskReward, new FirstHeartGetTaskRewardReq { Type = type, TaskId = (int)task });
        return (await Next(bot, FirstHeartService.GetTaskReward, FirstHeartGetTaskRewardResp.Parser)).Result;
    }

    private async Task<int> ClaimScore(BotClient bot, int reward, int type = 1)
    {
        await bot.SendGameAsync(FirstHeartService.GetScoreReward, new FirstHeartGetScoreRewardReq { Type = type, RewardId = reward });
        return (await Next(bot, FirstHeartService.GetScoreReward, FirstHeartGetScoreRewardResp.Parser)).Result;
    }

    private long OwnedSourceCount(string source)
    {
        using var db = Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key=$source AND r.command_kind='cd13e/firstheart-award'
            """;
        command.Parameters.AddWithValue("$source", source);
        return (long)command.ExecuteScalar()!;
    }

    [ClientDataFact]
    public async Task SyncStartsAtItsOpenLevelWithDayOneOpenAndAnInitializedRewardMap()
    {
        await using var alice = await InWorld("Alice");
        var s = Session(alice);
        // Below the open condition nothing is shown (the DAL indexes activityData[1] unguarded) and claims are refused.
        Assert.Null(await Cfg(s));
        Assert.False(await Read(() => s.Synced.ContainsKey(FirstHeartService.Sync)));
        Assert.Equal(ErrorCodes.ActivityClosed, await ClaimTask(alice, LevelTen));
        await Gm(alice, $"!lvl {OpenLevel - 1}");
        Assert.Equal(ErrorCodes.ActivityClosed, await ClaimTask(alice, LevelTen));

        await Gm(alice, $"!lvl {OpenLevel}");
        var full = await Next(alice, FirstHeartService.Sync, DBFirstHeartTaskActivityDataIncrement.Parser, m => m.SyncAllMark);
        var all = full.ActivityData[1].All;
        Assert.Equal(1, all.CfgData[1001]);
        Assert.All(Enumerable.Range(1002, 6), day => Assert.Equal(0, all.CfgData[day]));
        Assert.Equal([LevelTen, LoginOne, CardTask], all.Tasks.Keys.Order().ToArray());
        Assert.Equal(FirstHeartTaskState.Accept, all.Tasks[LevelTen].Status);
        Assert.Equal([10L], all.Tasks[LevelTen].Scores.ToArray());
        Assert.Equal(FirstHeartTaskState.Accept, all.Tasks[LoginOne].Status); // the login day counted at login
        Assert.Equal(FirstHeartTaskState.None, all.Tasks[CardTask].Status);
        Assert.Equal([0L], all.Tasks[CardTask].Scores.ToArray());
        Assert.Equal(0, all.Scores);
        // The wire-only empty-map marker, deleted by the next increment; the stored state never holds it.
        Assert.True(all.GetRewards.ContainsKey(0));
        var deleted = await Next(alice, FirstHeartService.Sync, DBFirstHeartTaskActivityDataIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(deleted.ActivityData[1].Inc.GetRewards[0].Del);
        Assert.Empty((await Cfg(s))!.GetRewards);
        var stored = DBFirstHeartTaskActivityData.Parser.ParseFrom(Service<RoleStore>().LoadFirstHeart(s.Role!.Id)!);
        Assert.True(stored.GetLvTime[1] > 0);
        Assert.Equal(FirstHeartTaskState.Accept, stored.ActivityData[1].Tasks[LevelTen].Status);
    }

    [ClientDataFact]
    public async Task ATaskPaysOnceScoresItsPointAndIneligibleClaimsAreRefused()
    {
        await using var alice = await InWorld("Alice");
        var s = Session(alice);
        await Gm(alice, $"!lvl {OpenLevel}");
        await Until(s, cfg => Status(cfg, LevelTen) == FirstHeartTaskState.Accept, "level 10 task completed");

        Assert.Equal(ErrorCodes.TaskRequirements, await ClaimTask(alice, CardTask));   // not completed
        Assert.Equal(ErrorCodes.TaskRequirements, await ClaimTask(alice, LevelTwenty)); // day 2 is locked
        Assert.Equal(ErrorCodes.Parameter, await ClaimTask(alice, LevelTen, type: 2));
        Assert.Equal(ErrorCodes.Parameter, await ClaimTask(alice, 999));

        long zeny = await Read(() => Service<CharacterService>().CountOf(s, Zeny));
        Assert.Equal(0, await ClaimTask(alice, LevelTen));
        Assert.Equal(zeny + 5000, await Read(() => Service<CharacterService>().CountOf(s, Zeny))); // group 1001: 5,000 Zeny
        var cfg = (await Cfg(s))!;
        Assert.Equal(FirstHeartTaskState.GetReward, Status(cfg, LevelTen));
        Assert.Equal(1, cfg.Scores);
        await Next(alice, FirstHeartService.Sync, DBFirstHeartTaskActivityDataIncrement.Parser,
            m => m.ActivityData.TryGetValue(1, out var a) && a.Inc?.Scores == 1);

        Assert.Equal(ErrorCodes.RewardClaimed, await ClaimTask(alice, LevelTen));
        Assert.Equal(zeny + 5000, await Read(() => Service<CharacterService>().CountOf(s, Zeny)));
        Assert.Equal(1, (await Cfg(s))!.Scores);
        long start = await Read(() => s.FirstHeart!.GetLvTime[1]);
        Assert.Equal(1, OwnedSourceCount($"firstheart/{s.Role!.Id}/{start}/task/{LevelTen}"));
    }

    [ClientDataFact]
    public async Task AScoreRewardNeedsItsScoreAndPaysOnce()
    {
        await using var alice = await InWorld("Alice");
        var s = Session(alice);
        await Gm(alice, $"!lvl {OpenLevel}");
        await Until(s, cfg => Status(cfg, LoginOne) == FirstHeartTaskState.Accept, "day 1 tasks completed");
        Assert.Equal(0, await ClaimTask(alice, LevelTen));
        Assert.Equal(0, await ClaimTask(alice, LoginOne));
        Assert.Equal(ErrorCodes.TaskRequirements, await ClaimScore(alice, 1)); // 2 of 3 points

        await Gm(alice, "!firstheart day 2");
        await Until(s, cfg => Status(cfg, LevelTwenty) == FirstHeartTaskState.Accept, "day 2 level task completed");
        Assert.Equal(1, (await Cfg(s))!.CfgData[1002]);
        Assert.Equal(0, await ClaimTask(alice, LevelTwenty));
        Assert.Equal(3, (await Cfg(s))!.Scores);

        Assert.Equal(ErrorCodes.Parameter, await ClaimScore(alice, 99));
        Assert.Equal(ErrorCodes.Parameter, await ClaimScore(alice, 1, type: 2));
        Assert.Equal(ErrorCodes.TaskRequirements, await ClaimScore(alice, 2)); // needs 6
        Assert.Equal(0, await ClaimScore(alice, 1));
        Assert.Equal(50, await Read(() => Service<CharacterService>().CountOf(s, 1250709))); // group 5306
        Assert.True((await Cfg(s))!.GetRewards.ContainsKey(1));
        Assert.Equal(ErrorCodes.RewardClaimed, await ClaimScore(alice, 1));
        Assert.Equal(50, await Read(() => Service<CharacterService>().CountOf(s, 1250709)));
    }

    [ClientDataFact]
    public async Task DaysLoginDaysAndClaimsSurviveARelogin()
    {
        var alice = await InWorld("Alice");
        var s = Session(alice);
        await Gm(alice, "!lvl 50");
        await Until(s, cfg => Status(cfg, LevelTen) == FirstHeartTaskState.Accept, "started");
        await Gm(alice, "!firstheart day 7");
        await Until(s, cfg => cfg.CfgData.Values.All(v => v == 1) && Status(cfg, LevelFifty) == FirstHeartTaskState.Accept,
            "all seven days open");
        Assert.Equal(FirstHeartTaskState.None, Status((await Cfg(s))!, LoginSeven));
        // 104100201 "log in 7 days": the achievement count is the login-day progress.
        await Gm(alice, "!ach 104100201 done");
        await Until(s, cfg => Status(cfg, LoginSeven) == FirstHeartTaskState.Accept, "seven login days");
        Assert.Equal(0, await ClaimTask(alice, LevelFifty));
        Assert.Equal(ErrorCodes.TaskRequirements, await ClaimScore(alice, 1)); // one point is not three
        long start = await Read(() => s.FirstHeart!.GetLvTime[1]);
        long role = s.Role!.Id;
        await alice.DisposeAsync();

        await using var again = await InWorld("Alice", create: false);
        var full = await Next(again, FirstHeartService.Sync, DBFirstHeartTaskActivityDataIncrement.Parser, m => m.SyncAllMark);
        var all = full.ActivityData[1].All;
        Assert.All(all.CfgData.Values, v => Assert.Equal(1, v));
        Assert.Equal(FirstHeartTaskState.GetReward, all.Tasks[LevelFifty].Status);
        Assert.Equal(FirstHeartTaskState.Accept, all.Tasks[LoginSeven].Status);
        Assert.Equal(1, all.Scores);
        var s2 = Session(again);
        Assert.Equal(start, await Read(() => s2.FirstHeart!.GetLvTime[1]));
        Assert.Equal(ErrorCodes.RewardClaimed, await ClaimTask(again, LevelFifty));
        Assert.Equal(1, OwnedSourceCount($"firstheart/{role}/{start}/task/{LevelFifty}"));
        Assert.Equal(0, await ClaimTask(again, LoginSeven));
        Assert.Equal(2, (await Cfg(s2))!.Scores);
    }
}
