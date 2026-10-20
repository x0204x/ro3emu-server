using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// GenericStageRewardsConfig types 3 (wardrobe fashion score), 4 (Codex Collection Points) and 6 (Survivor Battle rank)
/// through 31178/31175 and the shared 5059 list (DailyActivityService.StageError), and the Collection Points of a card
/// collection unlock (82404).
/// </summary>
public sealed class StageRewardTests : IAsyncLifetime
{
    private const int Coins = 1, Points = 118, ChickenPoints = 7, Lunatic = 1420100;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-stage-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await app.StartAsync();
        httpUrl = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(httpUrl, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
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

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{httpUrl}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private async Task<T> State<T>(Func<GameSession, T> read) =>
        await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            Task.FromResult(read(app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role != null))));

    private Task<long> Count(int entry) =>
        State(s => app.Services.GetRequiredService<CharacterService>().CountOf(s, entry));

    private async Task LoggedOut(BotClient bot)
    {
        string? account = bot.Account;
        await bot.DisposeAsync();
        var deadline = DateTime.UtcNow + Wait;
        while (app.Services.GetRequiredService<SessionRegistry>().All().Any(s => s.Account == account && s.Role != null)
               && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200);
    }

    private long OwnedSourceCount(string source, string kind)
    {
        using var db = app.Services.GetRequiredService<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key=$source AND r.command_kind=$kind
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<int> Claim(BotClient bot, int stage)
    {
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = stage });
        var reply = await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser, r => r.StageId == stage);
        return reply.Result;
    }

    private static async Task<int> Batch(BotClient bot, params int[] stages)
    {
        var req = new StageRewardBatchGainReq();
        req.StageIds.Add(stages);
        await bot.SendGameAsync(DailyActivityService.StageBatchGain, req);
        return (await Next(bot, DailyActivityService.StageBatchGain, StageRewardBatchGainResp.Parser,
            r => r.Req.StageIds.SequenceEqual(stages))).Result;
    }

    private Task<int[]> Claims() => State(s => s.Daily!.Claims.Order().ToArray());

    /// <summary>The coins of a stage's award (every type-3/4/6 award here pays item 1 first).</summary>
    private long StageCoins(int stage)
    {
        var data = app.Services.GetRequiredService<GameData>();
        long group = GameData.Int(data.Row("GenericStageRewardsConfig", stage)!.Value, "_iAward");
        if (group == 0) return 0;
        long award = GameData.Int(data.Row("MultiverseGroupConfig", group)!.Value, "_iValue_default");
        var fixedAward = data.Row("AwardConfig", award)!.Value[0].GetProperty("_kAward");
        return fixedAward.EnumerateArray().Where(a => GameData.Number(a[0]) == Coins).Sum(a => (long)GameData.Number(a[1])!.Value);
    }

    /// <summary>The 5059 the claim sends: the shared list with every claimed stage.</summary>
    private static Task<DBStageRewardInfoIncrement> ClaimsSynced(BotClient bot, params int[] stages) =>
        Next(bot, DailyActivityService.StageSync, DBStageRewardInfoIncrement.Parser,
            m => m.AllStageRewardsFinished?.All?.Value is { } v && stages.All(v.Contains));

    private async Task UseAppearanceItem(BotClient bot, string account, int item)
    {
        await Gm(account, $"!item {item} 1");
        long guid = await State(s => s.Items.Single(i => i.Entry == item).Guid);
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser, r => r.ItemGuid == guid)).Result);
    }

    [ClientDataFact]
    public async Task FashionStagesFollowThePermanentWardrobeScore()
    {
        var bot = await InWorld("Alice");
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 3002)); // level 1 needs 500, a new role has 0
        // WardRobeConfig._iFashionScore: 40050030 1000, 40050010 500, 40040010 500 -> 2000 (levels 1..3).
        await UseAppearanceItem(bot, "alice", 3045403);
        await UseAppearanceItem(bot, "alice", 3025401);
        await UseAppearanceItem(bot, "alice", 3034401);

        long coins = await Count(Coins);
        Assert.Equal(0, await Claim(bot, 3002));
        Assert.Equal(coins + StageCoins(3002), await Count(Coins));
        await ClaimsSynced(bot, 3002);
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, 3002));
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 3005)); // 3500

        // 31175 is all or nothing: a listed row out of reach or already claimed refuses the batch.
        coins = await Count(Coins);
        Assert.Equal(ErrorCodes.CurrencyShort, await Batch(bot, 3003, 3004, 3005));
        Assert.Equal(ErrorCodes.RewardClaimed, await Batch(bot, 3002, 3003));
        Assert.Equal(new[] { 3002 }, await Claims());
        Assert.Equal(coins, await Count(Coins));
        Assert.Equal(0, await Batch(bot, 3003, 3004));
        Assert.Equal(coins + StageCoins(3003) + StageCoins(3004), await Count(Coins));
        Assert.Equal(new[] { 3002, 3003, 3004 }, await Claims());
        string source = await State(s => $"daily-stage/{s.Role!.Id}/{s.Daily!.DailyEpoch}/3003,3004");
        Assert.Equal(1, OwnedSourceCount(source, "cd13e/daily-stage"));

        await LoggedOut(bot);
        await using var again = await InWorld("Alice", create: false);
        var login = await ClaimsSynced(again, 3002, 3003, 3004);
        Assert.True(login.SyncAllMark);
        coins = await Count(Coins);
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(again, 3003));
        Assert.Equal(ErrorCodes.RewardClaimed, await Batch(again, 3002, 3004));
        Assert.Equal(coins, await Count(Coins));
        Assert.Equal(1, OwnedSourceCount(source, "cd13e/daily-stage"));
    }

    [ClientDataFact]
    public async Task CodexStagesFollowTheirPointItemAndTheEmptyFirstLevelIsOnlyMarked()
    {
        var bot = await InWorld("Alice");
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 4002)); // 50 Collection Points
        // LC_Library_Helper.SendRewardByType lists 4001 (score 0, no award) until it is marked: it pays nothing.
        long coins = await Count(Coins);
        Assert.Equal(0, await Batch(bot, 4001));
        Assert.Equal(new[] { 4001 }, await Claims());
        Assert.Equal(coins, await Count(Coins));
        Assert.Equal(1, OwnedSourceCount(await State(s => $"daily-stage/{s.Role!.Id}/{s.Daily!.DailyEpoch}/4001"),
            "cd13e/daily-stage"));

        await Gm("alice", $"!item {Points} 130"); // 4002 (50), 4003 (130); 4004 needs 230
        Assert.Equal(0, await Claim(bot, 4002));
        Assert.Equal(coins + StageCoins(4002), await Count(Coins));
        Assert.Equal(130, await Count(Points)); // a threshold, not spent
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, 4002));
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 4004));
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 4101)); // the card tab's own item 119

        coins = await Count(Coins);
        Assert.Equal(ErrorCodes.CurrencyShort, await Batch(bot, 4003, 4004));
        Assert.Equal(ErrorCodes.RewardClaimed, await Batch(bot, 4001, 4003));
        Assert.Equal(new[] { 4001, 4002 }, await Claims());
        await Gm("alice", $"!item {Points} 100");
        Assert.Equal(0, await Batch(bot, 4003, 4004));
        Assert.Equal(coins + StageCoins(4003) + StageCoins(4004), await Count(Coins));
        await ClaimsSynced(bot, 4001, 4002, 4003, 4004);

        await LoggedOut(bot);
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(new[] { 4001, 4002, 4003, 4004 }, await Claims());
        coins = await Count(Coins);
        Assert.Equal(ErrorCodes.RewardClaimed, await Batch(again, 4003, 4004));
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(again, 4002));
        Assert.Equal(coins, await Count(Coins));
    }

    [ClientDataFact]
    public async Task SurvivorStagesFollowTheRankAndRankOneNeedsPoints()
    {
        var bot = await InWorld("Alice");
        Assert.Equal(1, await State(s => s.TimedBattle!.ChickenRank));
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 6001)); // rank 1 without points in it
        await Gm("alice", $"!item {ChickenPoints} 10");
        long coins = await Count(Coins);
        Assert.Equal(0, await Claim(bot, 6001));
        Assert.Equal(coins + StageCoins(6001), await Count(Coins));
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, 6001));
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(bot, 6002));

        await Gm("alice", "!chicken rank 3");
        Assert.Equal(3, await State(s => s.TimedBattle!.ChickenRank));
        coins = await Count(Coins);
        Assert.Equal(ErrorCodes.CurrencyShort, await Batch(bot, 6002, 6003, 6004));
        Assert.Equal(ErrorCodes.RewardClaimed, await Batch(bot, 6001, 6002, 6003));
        Assert.Equal(coins, await Count(Coins));
        Assert.Equal(0, await Batch(bot, 6002, 6003));
        Assert.Equal(coins + StageCoins(6002) + StageCoins(6003), await Count(Coins));
        await ClaimsSynced(bot, 6001, 6002, 6003);

        await LoggedOut(bot);
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(3, await State(s => s.TimedBattle!.ChickenRank));
        Assert.Equal(new[] { 6001, 6002, 6003 }, await Claims());
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(again, 6003));
        Assert.Equal(ErrorCodes.CurrencyShort, await Claim(again, 6004));
    }

    [ClientDataFact]
    public async Task ACardCollectionUnlockPaysItsCollectionPointsOnce()
    {
        var data = app.Services.GetRequiredService<GameData>();
        int Reward(long quality) => data.Table("PublicParamConfig")["LibraryCardUnlockReward"].GetProperty(quality.ToString())
            .GetProperty("PointValue").GetProperty("0").GetInt32();
        int treasurable = (int)GameData.Int(data.Table("ItemConfig").Values.First(row => GameData.Int(row, "_iType") == 4
            && GameData.Int(row, "_iSubType") == 400 && GameData.Int(row, "_iQuality") == 4), "_iID");

        var bot = await InWorld("Alice");
        await Gm("alice", $"!item {Lunatic} 1");
        await Gm("alice", $"!item {treasurable} 1");
        long role = await State(s => s.Role!.Id);
        var roles = app.Services.GetRequiredService<RoleStore>();
        async Task<int> Unlock(BotClient client, int card, bool treasure)
        {
            await client.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = card, IsTreasure = treasure });
            return (await Next(client, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser, r => r.CardId == card)).Result;
        }

        // The points ride the unlock's own commit: a failed write leaves neither the entry, the points nor a follow-up.
        FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER fail_card_points BEFORE UPDATE OF currency ON role BEGIN SELECT RAISE(ABORT, 'card points fault'); END");
        Assert.Equal(ErrorCodes.Unknown, await Unlock(bot, Lunatic, false));
        FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_card_points");
        Assert.False(await State(s => s.Library!.Cards[Lunatic].Unlock));
        Assert.False(DBLibraryInfo.Parser.ParseFrom(roles.LoadLibrary(role)!).Cards[Lunatic].Unlock);
        Assert.False(roles.HasTaskFollowup(role));
        Assert.Equal(0, await Count(Points));

        Assert.Equal(0, await Unlock(bot, Lunatic, false));
        long expected = Reward(GameData.Int(data.Row("ItemConfig", Lunatic)!.Value, "_iQuality"));
        Assert.True(expected > 0);
        Assert.Equal(expected, await Count(Points));
        Assert.True(await State(s => s.Library!.Cards[Lunatic].Unlock));
        Assert.Equal(1, OwnedSourceCount($"library-card/{role}/{Lunatic}", "cd13e/library-award"));
        Assert.Equal(ErrorCodes.AlreadyUnlocked, await Unlock(bot, Lunatic, false));
        Assert.Equal(expected, await Count(Points));

        Assert.Equal(0, await Unlock(bot, treasurable, false));
        expected += Reward(4);
        Assert.Equal(0, await Unlock(bot, treasurable, true)); // (inferred) the treasure unlock pays nothing
        Assert.Equal(expected, await Count(Points));

        await LoggedOut(bot);
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(expected, await Count(Points));
        Assert.True(await State(s => s.Library!.Cards[treasurable].TreasureUnlock));
        Assert.Equal(ErrorCodes.AlreadyUnlocked, await Unlock(again, Lunatic, false));
        Assert.Equal(expected, await Count(Points));
        Assert.Equal(1, OwnedSourceCount($"library-card/{role}/{Lunatic}", "cd13e/library-award"));
    }
}
