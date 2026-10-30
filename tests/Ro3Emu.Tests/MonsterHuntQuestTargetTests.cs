using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// 自造領獎與任務資料：83621 的已提交地圖旗標應在 handler 返回前刷新 318001／318003。
/// 外層角色保留阻擋一般 StateChanged 的排程，避免另一個刷新事件掩蓋缺少直接 await 的情形。
/// 這是本地完成時序與交易回歸，不是原服畫面或經驗公式驗收。
/// </summary>
public sealed class MonsterHuntQuestTargetTests
{
    private const int Area = 977001, MapA = 977011, MapB = 977012, Group = 977021, Award = 977022;
    private const int CountQuest = 977031, SpecificQuest = 977032, MapNeed = 3, Reward = 7;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SuccessfulMapClaimsRefreshBothQuestTargetsBeforeReturningAndPayOnce()
    {
        await WithFixtureAsync(async host =>
        {
            await using var bot = await host.InWorldAsync("MapQuestClaims");
            var session = Session(host, bot);
            await SeedScoresAsync(host, session, MapNeed);
            long opening = Money(host, session);
            await ClaimOwnedAsync(host, session, MapA, () => AssertProgress(host, session, 1, 0));
            Assert.Equal(MapA, (await Next(bot, MonsterHuntService.GainMapAward, AreaConcurGainMapAwardResp.Parser)).Req.MapId);
            Assert.Equal(opening + Reward, Money(host, session));
            Assert.Equal(1, Receipts(host, session));

            Assert.Equal(ErrorCodes.RewardClaimed, await RejectedClaimAsync(bot, MapA));
            await AssertProgressAsync(host, session, 1, 0);
            Assert.Equal(opening + Reward, Money(host, session));
            Assert.Equal(1, Receipts(host, session));

            await ClaimOwnedAsync(host, session, MapB, () => AssertProgress(host, session, 2, 1));
            Assert.Equal(MapB, (await Next(bot, MonsterHuntService.GainMapAward, AreaConcurGainMapAwardResp.Parser)).Req.MapId);
            Assert.Equal(opening + 2 * Reward, Money(host, session));
            Assert.Equal(2, Receipts(host, session));
            var stored = DBAreaConcurInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadAreaConcur(session.Role!.Id)!);
            Assert.True(stored.MapInfo[MapA].IsClaimedAward);
            Assert.True(stored.MapInfo[MapB].IsClaimedAward);
        });
    }

    [Fact]
    public async Task InvalidOrUnreachedMapClaimsDoNotAdvanceQuestsOrGrantRewards()
    {
        await WithFixtureAsync(async host =>
        {
            await using var bot = await host.InWorldAsync("MapQuestRejected");
            var session = Session(host, bot);
            await SeedScoresAsync(host, session, MapNeed - 1);
            long opening = Money(host, session);
            Assert.Equal(ErrorCodes.Parameter, await RejectedClaimAsync(bot, 0));
            Assert.Equal(ErrorCodes.RewardMissing, await RejectedClaimAsync(bot, MapB + 100));
            Assert.Equal(ErrorCodes.TaskRequirements, await RejectedClaimAsync(bot, MapA));
            await AssertProgressAsync(host, session, 0, 0);
            Assert.False(session.AreaConcur!.MapInfo[MapA].IsClaimedAward);
            Assert.Equal(opening, Money(host, session));
            Assert.Equal(0, Receipts(host, session));
        });
    }

    [Fact]
    public async Task FailedMapClaimTransactionDoesNotAdvanceQuestsAndRetryPaysOnce()
    {
        await WithFixtureAsync(async host =>
        {
            await using var bot = await host.InWorldAsync("MapQuestRollback");
            var session = Session(host, bot);
            await SeedScoresAsync(host, session, MapNeed);
            long opening = Money(host, session);
            var accounts = host.Service<AccountStore>();
            FixtureSql.Execute(accounts, "CREATE TRIGGER fail_hunt_quest_claim BEFORE UPDATE OF area_concur ON role "
                + "BEGIN SELECT RAISE(ABORT,'synthetic map quest rollback'); END");
            try
            {
                Assert.Equal(ErrorCodes.Unknown, await RejectedClaimAsync(bot, MapB));
                await AssertProgressAsync(host, session, 0, 0);
                Assert.False(session.AreaConcur!.MapInfo[MapB].IsClaimedAward);
                Assert.Equal(opening, Money(host, session));
                Assert.Equal(0, Receipts(host, session));
            }
            finally { FixtureSql.Execute(accounts, "DROP TRIGGER fail_hunt_quest_claim"); }

            await ClaimOwnedAsync(host, session, MapB, () => AssertProgress(host, session, 1, 1));
            await Next(bot, MonsterHuntService.GainMapAward, AreaConcurGainMapAwardResp.Parser);
            Assert.Equal(ErrorCodes.RewardClaimed, await RejectedClaimAsync(bot, MapB));
            await AssertProgressAsync(host, session, 1, 1);
            Assert.Equal(opening + Reward, Money(host, session));
            Assert.Equal(1, Receipts(host, session));
        });
    }

    private static GameSession Session(SyntheticHost host, BotClient bot) => host.Service<SessionRegistry>().All()
        .Single(session => session.Account == bot.Account && session.Role != null);

    private static Task SeedScoresAsync(SyntheticHost host, GameSession session, int score) =>
        host.Service<GameLoop>().InvokeAsync(() =>
        {
            session.AreaConcur!.MapInfo[MapA] = new AreaConcurMapInfo { CurScore = score };
            session.AreaConcur.MapInfo[MapB] = new AreaConcurMapInfo { CurScore = score };
            AssertProgress(host, session, 0, 0);
            return Task.CompletedTask;
        });

    private static Task ClaimOwnedAsync(SyntheticHost host, GameSession session, int map, Action verify) =>
        host.Service<GameLoop>().InvokeAsync(() => host.Service<OwnerRegistry>().RunSessionAsync(
            "test/map-claim-quest", [ReservationKey.Role(session.Role!.Id)], session, async () =>
            {
                await host.Service<MonsterHuntService>().ClaimMapAsync(session, new AreaConcurGainMapAwardReq { MapId = map });
                verify(); // 同一角色保留內立即檢查；不輪詢、不另外觸發任務刷新。
            }));

    private static void AssertProgress(SyntheticHost host, GameSession session, long total, long specific)
    {
        var stored = DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(session.Role!.Id)!);
        foreach (var (id, value, required) in new[] { (CountQuest, total, 2L), (SpecificQuest, specific, 1L) })
        {
            Assert.Equal(value, stored.Infos[id].CompleteNum.Single());
            Assert.Equal(value, session.Tasks!.Infos[id].CompleteNum.Single());
            Assert.Equal(value == required ? TaskState.Finish : TaskState.Running, stored.Infos[id].Status);
        }
    }

    private static Task AssertProgressAsync(SyntheticHost host, GameSession session, long total, long specific) =>
        host.Service<GameLoop>().InvokeAsync(() =>
        {
            AssertProgress(host, session, total, specific);
            return Task.CompletedTask;
        });

    private static long Money(SyntheticHost host, GameSession session) =>
        host.Service<RoleStore>().LoadProgress(session.Role!.Id).Currency.GetValueOrDefault(1);

    private static long Receipts(SyntheticHost host, GameSession session)
    {
        using var db = host.Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/monsterhunt-award' AND actor_role=$role";
        command.Parameters.AddWithValue("$role", session.Role!.Id);
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<long> RejectedClaimAsync(BotClient bot, long map)
    {
        await bot.SendGameAsync(MonsterHuntService.GainMapAward, new AreaConcurGainMapAwardReq { MapId = map });
        return (await Next(bot, 99999, APIErrorMsg.Parser,
            error => error.OriginMsgId == MonsterHuntService.GainMapAward)).ErrorCode;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(packet => packet.IsGame(id)
            && (match == null || match(parser.ParseFrom(packet.Body))), Wait)).Body);

    private static async Task WithFixtureAsync(Func<SyntheticHost, Task> test)
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = fixture);
            await test(host);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(fixture, true);
        }
    }

    private static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-mapquest-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        Add(root, "FunctionConfig", MonsterHuntService.Function,
            new { _iID = MonsterHuntService.Function, _kOpenCondition = 0 });
        Add(root, "AreaConcurConfig", Area, new { _iID = Area, _iCondition = 0 });
        foreach (int map in new[] { MapA, MapB })
            Add(root, "AreaConcurMapConfig", map, new { _iID = map, _iArea = Area, _iMap = SyntheticData.Town,
                _iMapRequest = MapNeed, _iMapLimit = MapNeed, _iMapAward = Group });
        Add(root, "MultiverseGroupConfig", Group, new { _iID = Group, _itype = 0, _iValue_default = Award });
        Add(root, "AwardConfig", Award, new[] { new { _iID = Award, _kAward = new[] { new[] { 1, Reward, 0 } } } });
        Add(root, "TaskConfig", TaskService.FirstTask, new { _iTaskID = TaskService.FirstTask, _iTaskType = 1,
            _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 3 }, _kProgressTarget = new[] { new[] { 101002, 99 } } });
        AddQuest(root, CountQuest, [318001, 2]);
        AddQuest(root, SpecificQuest, [318003, MapB]);
        return root;
    }

    private static void AddQuest(string root, int id, int[] target) => Add(root, "TaskConfig", id,
        new { _iTaskID = id, _iTaskType = 2, _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 3 },
            _kProgressTarget = new[] { target } });

    private static void Add(string root, string table, long key, object row)
    {
        string path = Path.Combine(root, "config", table + ".json");
        var rows = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        rows.Add(key.ToString(CultureInfo.InvariantCulture), JsonSerializer.SerializeToNode(row));
        File.WriteAllText(path, rows.ToJsonString());
    }
}
