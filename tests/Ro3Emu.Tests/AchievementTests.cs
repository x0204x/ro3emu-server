using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Achievements / Trophy (AchievementService) over the real wire: the 5019 full and
/// increments, snapshot and event conditions, the 76002/76003/76010 claims as one owned-award transaction, 76009,
/// persistence and the hook families.
/// </summary>
public sealed class AchievementTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-achievements-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepWait = TimeSpan.FromSeconds(30); // the 5 s probe sweep under suite load
    private WebApplication _app = null!;
    private string _http = "";

    // AchievementConfig rows used below.
    private const long CraftOrange = 100100200, Craft10 = 100100300, Craft50 = 100100301, Appraise10 = 100100600,
        FirstCard = 100100800, CardQuality4 = 100100900, JobRank2 = 104100100, JobRank3 = 104100101,
        LoginDays2 = 104100200, Mvp1 = 101100100, Tower5 = 101100200, Tower20 = 101100201, TeamBoss = 101100300,
        GodBoss = 101100400, Guild = 102100100, GuildPosition = 102100200, Friend1 = 102100300, Affection2 = 102100400,
        Phoenix = 103100200;
    private const int ZenyLike = 1; // AwardConfig 1001001 = 5000 x item 1

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task LoggedOut(string account)
    {
        var registry = Service<SessionRegistry>();
        var deadline = DateTime.UtcNow + Wait;
        while (registry.All().Any(s => s.Account == account && s.Role != null) && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(200); // the logout's asynchronous saves
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);
    private Task<T> Read<T>(string name, Func<GameSession, T> read) =>
        Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session(name))));
    private Task OnLoop(string name, Action<GameSession> change) =>
        Service<GameLoop>().InvokeAsync(() => { change(Session(name)); return Task.CompletedTask; });
    private Task<AchievementInfo?> Info(string name, long id) => Read(name, s => s.Achievements!.Infos.GetValueOrDefault(id)?.Clone());
    private Task<AchievementLevelInfo> LevelInfo(string name, int type) => Read(name, s => s.Achievements!.AchievementLevelInfo[type].Clone());

    private static DBRoleAchievementInfoIncrement.Types.infosFieldIncrement? Field(DBRoleAchievementInfoIncrement m, long id) =>
        m.Infos.TryGetValue(id, out var field) ? field : null;

    private static AchievementStatusType? Status(DBRoleAchievementInfoIncrement.Types.infosFieldIncrement? f) =>
        f?.All?.Status ?? (f?.Inc is { HasStatus: true } inc ? inc.Status : null);

    /// <summary>The in-play 5019 increment that reaches <paramref name="id"/>.</summary>
    private static async Task<DBRoleAchievementInfoIncrement> ReachedIncrement(BotClient bot, long id, TimeSpan? wait = null)
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(AchievementService.Sync) && DBRoleAchievementInfoIncrement.Parser.ParseFrom(e.Body)
            is { SyncAllMark: false } m && Status(Field(m, id)) == AchievementStatusType.Complete, wait ?? Wait);
        return DBRoleAchievementInfoIncrement.Parser.ParseFrom(e.Body);
    }

    private static Task<DBRoleAchievementInfoIncrement> Full(BotClient bot) =>
        Next(bot, AchievementService.Sync, DBRoleAchievementInfoIncrement.Parser, m => m.SyncAllMark);

    private async Task Until(string name, Func<GameSession, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await Read(name, condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    private static async Task<GetAchievementRewardResp> Claim(BotClient bot, params long[] ids)
    {
        await bot.SendGameAsync(AchievementService.GetReward, new GetAchievementRewardReq { ID = { ids } });
        return await Next(bot, AchievementService.GetReward, GetAchievementRewardResp.Parser);
    }

    private static async Task<GetAchievementPointRewardResp> ClaimLevel(BotClient bot, int type, int level)
    {
        await bot.SendGameAsync(AchievementService.GetPointReward, new GetAchievementPointRewardReq { Type = type, Level = level });
        return await Next(bot, AchievementService.GetPointReward, GetAchievementPointRewardResp.Parser);
    }

    private long Receipts(long role)
    {
        using var db = Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/achievement-award' AND actor_role=$role";
        command.Parameters.AddWithValue("$role", role);
        return (long)command.ExecuteScalar()!;
    }

    private EconomySnapshot Cut() => EconomySnapshotReader.Read(Service<AccountStore>().DatabasePath,
        EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")), "auto");

    private EconomyLedgerWindow Ledger(EconomySnapshot opening, EconomySnapshot closing) =>
        EconomyLedgerReader.Read(Service<AccountStore>().DatabasePath, EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")),
            "auto", opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);

    private DBRoleAchievementInfo Saved(long role) =>
        DBRoleAchievementInfo.Parser.ParseFrom(Service<RoleStore>().LoadAchievements(role) ?? []);

    // ---------------------------------------------------------------- 1 catalog

    [ClientDataFact]
    public void TheCatalogLoadsEveryRowAndLevelsFollowTheClientWalk()
    {
        var service = Service<AchievementService>();
        var rows = service.AllRows;
        Assert.Equal(134, rows.Count);
        Assert.All(rows, r => Assert.All(r.Required, required => Assert.True(required > 0, $"{r.Id} has no R")));
        var blocked = rows.Where(r => r.Blocked).ToList();
        Assert.Equal(15, blocked.Count); // 316002 (Echo Resonance) is read from the worn Soul Echoes
        Assert.All(blocked, r => Assert.Contains(r.Targets[0][0],
            new long[] { 106007, 106008, 303001, 302007, 326003, 303004 }));
        Assert.Single(blocked, r => r.Targets[0][0] == 303004); // the fashion-grid variant only
        Assert.Equal(new[] { 1L, 1, 1 }, rows.Single(r => r.Id == TeamBoss).Required); // 210008 x3, R = p1
        Assert.Equal(40060010, rows.Single(r => r.Id == Phoenix).Required[0]); // 303002: R is the appearance id

        var tables = service.LevelTables;
        Assert.Equal(new[] { 0, 100, 101, 102, 103, 104, 105 }, tables.Keys.Order());
        Assert.Equal(11, tables[0].Length);
        Assert.All(tables.Where(t => t.Key != 0), t => Assert.Equal(6, t.Value.Length));
        foreach (var (points, level) in new[] { (99L, 0), (100, 1), (149, 1), (150, 2), (599, 4), (600, 5), (1000, 5) })
            Assert.Equal(level, AchievementService.LevelOf(points, tables[100]));
        foreach (var (points, level) in new[] { (99L, 0), (100, 1), (499, 1), (500, 2), (22499, 9), (22500, 10), (200000, 10) })
            Assert.Equal(level, AchievementService.LevelOf(points, tables[0]));
    }

    // ---------------------------------------------------------------- 2, 3, 12 login

    [ClientDataFact]
    public async Task AFreshRoleGetsOneFullSyncWithLevelInfosAndNothingReached()
    {
        await using var alice = await InWorld("Alice");
        var full = await Full(alice);
        Assert.Equal(new long[] { 0, 100, 101, 102, 103, 104, 105 }, full.AchievementLevelInfo.Keys.Order());
        Assert.All(full.AchievementLevelInfo.Values, f =>
        {
            Assert.Equal(0, f.All.CurLevelRewardLevel);
            Assert.Equal(0, f.All.CurHintLevel);
            Assert.Equal(0, f.All.Level);
        });
        Assert.All(full.Infos.Values, f => Assert.Equal(AchievementStatusType.None, f.All.Status));
        Assert.Equal([1], full.Infos[LoginDays2].All.Scores); // the first counted login day
        await Task.Delay(300);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(AchievementService.Sync)
            && DBRoleAchievementInfoIncrement.Parser.ParseFrom(e.Body).SyncAllMark);
    }

    [ClientDataFact]
    public async Task SnapshotsAndLoginDaysAreEvaluatedAtLoginWithoutAnIncrement()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Full(alice);
        }
        await LoggedOut("alice");
        // Offline: the role became a first job and its last counted login day is yesterday's.
        var accounts = Service<AccountStore>();
        var runtime = System.Text.Json.JsonSerializer.Deserialize<AchievementService.RuntimeState>(
            Service<RoleStore>().LoadAchievementRuntime(role)!)!;
        runtime.LastLoginDay -= 86400;
        FixtureSql.Execute(accounts, "UPDATE role SET career=1100, achievement_runtime=$r WHERE id=$id",
            ("$r", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(runtime)), ("$id", role));

        await using var again = await InWorld("Alice", create: false);
        var full = await Full(again);
        Assert.Equal(AchievementStatusType.Complete, full.Infos[JobRank2].All.Status);
        Assert.Equal([1], full.Infos[JobRank2].All.Scores);
        Assert.Equal(AchievementStatusType.Complete, full.Infos[LoginDays2].All.Status);
        Assert.Equal([2], full.Infos[LoginDays2].All.Scores);
        Assert.Equal([2], full.Infos[104100201].All.Scores); // the 7-day row keeps counting
        Assert.Equal(AchievementStatusType.None, full.Infos[104100201].All.Status);
        Assert.InRange(full.Infos[JobRank2].All.Stamptime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await Task.Delay(300);
        Assert.DoesNotContain(again.Inbox.Snapshot(), e => e.IsGame(AchievementService.Sync)
            && !DBRoleAchievementInfoIncrement.Parser.ParseFrom(e.Body).SyncAllMark);
        Assert.Equal(AchievementStatusType.Complete, Saved(role).Infos[JobRank2].Status);
    }

    [ClientDataFact]
    public async Task StateSurvivesReconnectAndRelogAndALoginDayCountsOnce()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Full(alice);
            await Gm("alice", "!ach fill 100 20"); // 100 points: category 100 and global level 1
            await alice.SendGameAsync(AchievementService.UpdateHintLevel, new UpdateAchievementHintLevelReq());
            await Until("Alice", s => s.Achievements!.AchievementLevelInfo[0].CurHintLevel == 1, "hint level");
            Assert.Equal(1, (await LevelInfo("Alice", 100)).CurHintLevel);
            Assert.Equal(0, (await LevelInfo("Alice", 101)).CurHintLevel); // its level is 0
            Assert.Equal(0, (await ClaimLevel(alice, 100, 1)).Result);

            // 1023 asset replay of category 15 resends the dataset whole.
            await alice.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq { AssetType = { 15 } });
            var replay = await Next(alice, AchievementService.Sync, DBRoleAchievementInfoIncrement.Parser,
                m => m.SyncAllMark && m.Infos.Count >= 20);
            Assert.Equal(1, replay.AchievementLevelInfo[100].All.CurLevelRewardLevel);
            Assert.Equal(1, replay.AchievementLevelInfo[0].All.CurHintLevel);
        }
        await LoggedOut("alice");
        await using var again = await InWorld("Alice", create: false);
        var full = await Full(again);
        Assert.Equal(1, full.AchievementLevelInfo[100].All.CurLevelRewardLevel);
        Assert.Equal(1, full.AchievementLevelInfo[100].All.CurHintLevel);
        Assert.Equal(1, full.AchievementLevelInfo[0].All.CurHintLevel);
        Assert.Equal(1, full.AchievementLevelInfo[0].All.Level);
        Assert.Equal(20, full.Infos.Values.Count(f => f.All.Status == AchievementStatusType.Complete && f.All.AchieveId / 100000 == 1001));
        Assert.Equal([1], full.Infos[LoginDays2].All.Scores); // the same day is not counted twice
    }

    // ---------------------------------------------------------------- 4, 5 event counters and the increment shape

    [ClientDataFact]
    public async Task CraftsCountUpToRAndTheReachIncrementCarriesTheWholeEntry()
    {
        await using var alice = await InWorld("Alice");
        await Full(alice);
        await Gm("alice", "!quest done 10103020"); // opens crafting
        await Gm("alice", "!item 1254001 10");
        await Gm("alice", "!item 1200704 20");
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // EquipCraftConfig 2001 (rank 2 subtype 1): its quality weights make a quality-4 weapon certain.
        await alice.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 1 });
        Assert.Equal(0, (await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser)).Result);
        var reached = await ReachedIncrement(alice, CraftOrange); // 302010 [1,4]: a new key goes whole
        var entry = reached.Infos[CraftOrange].All;
        Assert.NotNull(entry);
        Assert.Equal(CraftOrange, entry.AchieveId);
        Assert.Equal([1], entry.Scores);
        Assert.InRange(entry.Stamptime, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal([1], reached.Infos[Craft10].All.Scores); // progress only: status 0
        Assert.Equal(AchievementStatusType.None, reached.Infos[Craft10].All.Status);

        for (int i = 2; i <= 10; i++)
        {
            await alice.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 1 });
            Assert.Equal(0, (await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser)).Result);
            int done = i;
            await Until("Alice", s => s.Achievements!.Infos[Craft50].Scores[0] == done, $"craft {done}");
            if (i < 10) Assert.Equal(AchievementStatusType.None, (await Info("Alice", Craft10))!.Status);
        }
        var ten = await ReachedIncrement(alice, Craft10);
        Assert.Equal(AchievementStatusType.Complete, Status(ten.Infos[Craft10]));
        Assert.Equal([10], (await Info("Alice", Craft10))!.Scores);
        Assert.Equal([10], (await Info("Alice", Craft50))!.Scores); // the series keeps counting
        Assert.Equal(AchievementStatusType.None, (await Info("Alice", Craft50))!.Status);
        Assert.Equal(10, (await LevelInfo("Alice", 0)).Point); // two reached rows of 5 points
    }

    // ---------------------------------------------------------------- 6, 11 76002

    [ClientDataFact]
    public async Task ClaimingOneRowGrantsItsAwardOnceInOneReceipt()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        await Full(alice);
        Assert.Equal(1901, (await Claim(alice)).Result);
        Assert.Equal(1901, (await Claim(alice, 999)).Result);
        var unreached = await Claim(alice, JobRank3);
        Assert.Equal(1902, unreached.Result);
        Assert.Equal(JobRank3, unreached.ErrorID);

        await Gm("alice", "!job 1100"); // JobChanged: 101019 [2] is reached in play
        await ReachedIncrement(alice, JobRank2);
        long zeny = await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike));
        var opening = Cut();
        var reply = await Claim(alice, JobRank2);
        Assert.Equal(0, reply.Result);
        Assert.Equal([JobRank2], reply.ID);
        var replyEvent = alice.Inbox.Snapshot().Last(e => e.IsGame(AchievementService.GetReward));
        var claimed = await Next(alice, AchievementService.Sync, DBRoleAchievementInfoIncrement.Parser,
            m => Status(Field(m, JobRank2)) == AchievementStatusType.GetReward);
        Assert.NotNull(claimed);
        var events = alice.Inbox.Snapshot().ToList();
        Assert.True(events.FindLastIndex(e => e.IsGame(CharacterService.GetRewardNotify)) < events.IndexOf(replyEvent),
            "the grant notice precedes the reply");
        Assert.Equal(zeny + 5000, await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike)));
        Assert.Equal(1, Receipts(role));
        var closing = Cut();
        var ledger = Ledger(opening, closing);
        Assert.DoesNotContain(closing.Issues, i => i.Kind is EconomyIssueKind.UnknownSchema or EconomyIssueKind.Corruption);
        var receipt = Assert.Single(ledger.Receipts);
        Assert.Equal("cd13e/achievement-award", receipt.CommandKind);
        Assert.All(ledger.Legs.Where(l => l.Kind == EconomyLegKind.Source), l => Assert.Equal(EconomyReason.AchievementReward, l.Reason));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));

        Assert.Equal(1903, (await Claim(alice, JobRank2)).Result); // replay
        Assert.Equal(1, Receipts(role));
        Assert.Equal(AchievementStatusType.GetReward, Saved(role).Infos[JobRank2].Status);
    }

    [ClientDataFact]
    public async Task AFailedClaimCommitChangesNeitherItemsNorAchievements()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        await Full(alice);
        await Gm("alice", "!job 1100");
        await ReachedIncrement(alice, JobRank2);
        var accounts = Service<AccountStore>();
        long zeny = await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike));
        FixtureSql.Execute(accounts, """
            CREATE TRIGGER achievement_claim_fail BEFORE INSERT ON operation_receipt
            WHEN NEW.command_kind='cd13e/achievement-award' BEGIN SELECT RAISE(ABORT, 'achievement rollback'); END
            """);
        Assert.Equal(ErrorCodes.Unknown, (await Claim(alice, JobRank2)).Result);
        Assert.Equal(zeny, await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike)));
        Assert.Equal(AchievementStatusType.Complete, (await Info("Alice", JobRank2))!.Status);
        Assert.Equal(AchievementStatusType.Complete, Saved(role).Infos[JobRank2].Status);
        Assert.Equal(0, Receipts(role));
        FixtureSql.Execute(accounts, "DROP TRIGGER achievement_claim_fail");

        Assert.Equal(0, (await Claim(alice, JobRank2)).Result);
        Assert.Equal(zeny + 5000, await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike)));
        Assert.Equal(1903, (await Claim(alice, JobRank2)).Result);
        Assert.Equal(1, Receipts(role));
    }

    // ---------------------------------------------------------------- 7, 9 76010

    [ClientDataFact]
    public async Task ClaimAllSettlesReachedRowsAndTheLevelInOneTransactionAndSkipsTheRest()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        await Full(alice);
        await Gm("alice", "!ach fill 100 20"); // ids 100100100.. ; category 100 level 1
        await Until("Alice", s => s.Achievements!.AchievementLevelInfo[100].Level == 1, "level 1");
        var reachedIds = await Read("Alice", s => s.Achievements!.Infos.Values
            .Where(i => i.Status == AchievementStatusType.Complete).Select(i => i.AchieveId).Order().ToList());
        Assert.Equal(20, reachedIds.Count);
        long zeny = await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike));
        int awarded = reachedIds.Count(id => Service<AchievementService>().AllRows.Single(r => r.Id == id).Award != 0);

        await alice.SendGameAsync(AchievementService.GetAllReward, new GetAchievementAllRewardReq
        {
            TaskIds = { reachedIds.Append(JobRank3).Append(999) },
            LevelRewards = { new LevelRewardInfo { Type = 100, Level = 1 }, new LevelRewardInfo { Type = 101, Level = 1 } },
        });
        var reply = await Next(alice, AchievementService.GetAllReward, GetAchievementAllRewardResp.Parser);
        Assert.Equal(0, reply.Result);
        Assert.Equal(reachedIds, reply.SuccessTaskIds.Order());
        var level = Assert.Single(reply.SuccessLevelRewards);
        Assert.Equal((100, 1), (level.Type, level.Level));
        Assert.Equal(1, Receipts(role));
        Assert.Equal(zeny + 5000L * (awarded + 1), await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike)));
        Assert.All(reachedIds, id => Assert.Equal(AchievementStatusType.GetReward, Saved(role).Infos[id].Status));
        Assert.Equal(1, Saved(role).AchievementLevelInfo[100].CurLevelRewardLevel);

        // Award-0 rows only: status 2, no grant, no receipt.
        await Gm("alice", "!ach 104100200 done");
        await Until("Alice", s => s.Achievements!.Infos[LoginDays2].Status == AchievementStatusType.Complete, "login days");
        int notices = alice.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.GetRewardNotify));
        await alice.SendGameAsync(AchievementService.GetAllReward, new GetAchievementAllRewardReq { TaskIds = { LoginDays2 } });
        var plain = await Next(alice, AchievementService.GetAllReward, GetAchievementAllRewardResp.Parser);
        Assert.Equal(0, plain.Result);
        Assert.Equal([LoginDays2], plain.SuccessTaskIds);
        Assert.Equal(AchievementStatusType.GetReward, Saved(role).Infos[LoginDays2].Status);
        Assert.Equal(1, Receipts(role));
        Assert.Equal(notices, alice.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.GetRewardNotify)));

        // Nothing eligible: the first failure's code.
        await alice.SendGameAsync(AchievementService.GetAllReward, new GetAchievementAllRewardReq { TaskIds = { LoginDays2 } });
        Assert.Equal(1903, (await Next(alice, AchievementService.GetAllReward, GetAchievementAllRewardResp.Parser)).Result);

        // Levels only: the client's handler stops at the nil task list, so the 76003 response follows.
        await Gm("alice", "!ach fill 100 10"); // 150 points: level 2
        await Until("Alice", s => s.Achievements!.AchievementLevelInfo[100].Level == 2, "level 2");
        await alice.SendGameAsync(AchievementService.GetAllReward, new GetAchievementAllRewardReq
        { LevelRewards = { new LevelRewardInfo { Type = 100, Level = 2 } } });
        var levelsOnly = await Next(alice, AchievementService.GetAllReward, GetAchievementAllRewardResp.Parser);
        Assert.Equal(0, levelsOnly.Result);
        Assert.Empty(levelsOnly.SuccessTaskIds);
        var compat = await Next(alice, AchievementService.GetPointReward, GetAchievementPointRewardResp.Parser);
        Assert.Equal((100, 0, 2), (compat.Type, compat.Result, compat.CurLevelRewardLevel));
        Assert.Equal(2, Receipts(role));
    }

    // ---------------------------------------------------------------- 8 76003

    [ClientDataFact]
    public async Task LevelClaimsValidateAndAClaimAtLevelTwoGrantsBothTiers()
    {
        await using var alice = await InWorld("Alice");
        await Full(alice);
        await Gm("alice", "!ach fill 100 30"); // 150 points: category 100 level 2, global level 1
        await Until("Alice", s => s.Achievements!.AchievementLevelInfo[100].Level == 2, "level 2");
        Assert.Equal(1905, (await ClaimLevel(alice, 99, 1)).Result);
        Assert.Equal(1904, (await ClaimLevel(alice, 100, 0)).Result); // level 0 has no award
        Assert.Equal(1904, (await ClaimLevel(alice, 100, 9)).Result); // no such level row
        Assert.Equal(1906, (await ClaimLevel(alice, 100, 3)).Result);
        Assert.Equal(1906, (await ClaimLevel(alice, 101, 1)).Result);

        long zeny = await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike));
        var reply = await ClaimLevel(alice, 100, 2);
        Assert.Equal((100, 0, 2), (reply.Type, reply.Result, reply.CurLevelRewardLevel));
        Assert.Equal(zeny + 10000, await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike))); // tiers 1 and 2
        Assert.Equal(2, (await LevelInfo("Alice", 100)).CurLevelRewardLevel);
        Assert.Equal(1903, (await ClaimLevel(alice, 100, 1)).Result);
        Assert.Equal(1903, (await ClaimLevel(alice, 100, 2)).Result);

        var global = await ClaimLevel(alice, 0, 1);
        Assert.Equal((0, 0, 1), (global.Type, global.Result, global.CurLevelRewardLevel));
        Assert.Equal(zeny + 15000, await Read("Alice", s => s.Progress!.Currency.GetValueOrDefault(ZenyLike)));
        Assert.Equal(1906, (await ClaimLevel(alice, 0, 2)).Result);
    }

    // ---------------------------------------------------------------- 10 76009

    [ClientDataFact]
    public async Task HintLevelUpdateSendsNoReplyAndPersists()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Full(alice);
            await Gm("alice", "!ach fill 103 20");
            await Until("Alice", s => s.Achievements!.AchievementLevelInfo[103].Level == 1, "level 1");
            await alice.SendGameAsync(AchievementService.UpdateHintLevel, new UpdateAchievementHintLevelReq());
            Assert.Equal(1901, (await Claim(alice)).Result); // a barrier after 76009
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(AchievementService.UpdateHintLevel));
            Assert.Equal(1, (await LevelInfo("Alice", 103)).CurHintLevel);
            Assert.Equal(1, (await LevelInfo("Alice", 0)).CurHintLevel);
            Assert.Equal(0, (await LevelInfo("Alice", 100)).CurHintLevel);
        }
        await LoggedOut("alice");
        Assert.Equal(1, Saved(role).AchievementLevelInfo[103].CurHintLevel);
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(1, (await Full(again)).AchievementLevelInfo[103].All.CurHintLevel);
    }

    // ---------------------------------------------------------------- 13 routes

    [Fact]
    public void ClaimsAndHintLevelAreRoutedAndShareIsNot()
    {
        var routes = Service<GameMessageHandler>().Routes;
        foreach (uint id in new uint[] { 76002, 76003, 76009, 76010 })
            Assert.True(routes.TryGet(id, out var route) && route.Owner == "achievements", $"{id}");
        Assert.False(routes.TryGet(76004, out _));
    }

    // ---------------------------------------------------------------- 14 hook families

    [ClientDataFact]
    public async Task AppraisalsAndCardGainsCount()
    {
        await using var alice = await InWorld("Alice");
        await Full(alice);
        await Gm("alice", "!quest done 10103020"); // opens appraisal
        await Gm("alice", "!item 2010250 10"); // unappraised orange gear, one instance each
        var guids = await Read("Alice", s => s.Items.Where(i => i.Entry == 2010250).Select(i => i.Guid).ToList());
        Assert.Equal(10, guids.Count);
        await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { guids } });
        Assert.Equal(0, (await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser)).Result);
        await ReachedIncrement(alice, Appraise10);
        Assert.Equal([10], (await Info("Alice", 100100601))!.Scores);

        var data = Service<GameData>();
        int card = (int)GameData.Int(data.Table("ItemConfig").Values.First(row => GameData.Int(row, "_iType") == 4
            && GameData.Int(row, "_iSubType") == 400 && GameData.Int(row, "_iQuality") >= 4), "_iID");
        await Gm("alice", $"!item {card} 3");
        await ReachedIncrement(alice, FirstCard);
        await Until("Alice", s => s.Achievements!.Infos.GetValueOrDefault(100100801)?.Scores[0] == 3, "3 cards counted");
        await Until("Alice", s => s.Achievements!.Infos.GetValueOrDefault(CardQuality4)?.Status == AchievementStatusType.Complete,
            "a quality-4 card");
    }

    [ClientDataFact]
    public async Task CreatingAGuildReachesMembershipAndPosition()
    {
        await using var alice = await InWorld("Alice");
        await Full(alice);
        await Gm("alice", "!lvl 21");
        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Trophy", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        await ReachedIncrement(alice, Guild);
        // 107003 [5, 20]: CheckGuildPosition with operator 5 (<): the leader (1) holds a position below 20.
        await Until("Alice", s => s.Achievements!.Infos.GetValueOrDefault(GuildPosition)?.Status == AchievementStatusType.Complete,
            "guild position");
    }

    [ClientDataFact]
    public async Task MvpTowerDungeonBossesAndAppearancesComeFromTheirHooksAndTheTick()
    {
        await using var alice = await InWorld("Alice");
        await Full(alice);
        var loop = Service<GameLoop>();
        // MVP settlement: once per online participant.
        await OnLoop("Alice", s => { foreach (var handler in Service<MvpService>().ParticipantSettled) handler(s); });
        await ReachedIncrement(alice, Mvp1);
        Assert.Equal([1], (await Info("Alice", 101100101))!.Scores);

        // A committed Spirit Tower clear of floor 20.
        var data = Service<GameData>();
        long floor20 = GameData.Int(data.Table("PetFloorConfig").Values.First(r => GameData.Int(r, "_iFloor") == 20), "_iID");
        await OnLoop("Alice", s =>
        {
            s.Tower!.LayerConfID = floor20;
            foreach (var handler in Service<TowerService>().FloorCleared) handler(s);
        });
        await ReachedIncrement(alice, Tower20);
        Assert.Equal(AchievementStatusType.Complete, (await Info("Alice", Tower5))!.Status);
        Assert.Equal([20], (await Info("Alice", 101100202))!.Scores);

        // Dungeon records: the three bosses of TenGameplayConfig 50101 and one Realm of the Gods boss.
        await loop.InvokeAsync(async () =>
        {
            var s = Session("Alice");
            foreach (string key in new[] { "50101:1", "50101:2", "100101:1" })
                s.Dungeons!.Records[key] = new DungeonService.Record { Passed = true };
            foreach (var handler in Service<DungeonService>().Completed) await handler(s, 0, false);
        });
        await Until("Alice", s => s.Achievements!.Infos.GetValueOrDefault(TeamBoss)?.Scores.SequenceEqual(new long[] { 1, 1, 0 }) == true,
            "two of three bosses");
        Assert.Equal(AchievementStatusType.Complete, (await Info("Alice", GodBoss))!.Status);
        await OnLoop("Alice", s => s.Dungeons!.Records["50101:3"] = new DungeonService.Record { Passed = true });
        await ReachedIncrement(alice, TeamBoss, SweepWait); // no hook fired: the 5 s sweep re-reads

        // A lit-up costume without any hook: the tick again. R and the score are the appearance id.
        await OnLoop("Alice", s => s.Wardrobe!.AllLightUpAppearances[40060010] = new DBAppearanceInfo { AppearanceId = 40060010 });
        await ReachedIncrement(alice, Phoenix, SweepWait);
        Assert.Equal([40060010], (await Info("Alice", Phoenix))!.Scores);
    }

    [ClientDataFact]
    public async Task FriendsAndAffectionAreReadFromTheRelation()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Full(alice);
        long a = (long)alice.Self!.Uuid, b = (long)bob.Self!.Uuid;
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });
        await ReachedIncrement(alice, Friend1); // 106017 [1, 1]: one friend of affection level >= 1
        Assert.Equal([1], (await Info("Alice", 102100301))!.Scores);
        // FriendlinessConfig: level 2 from 200 intimacy; a gift adds one per unit (inferred by FriendExtraService).
        await Gm("alice", "!item 1004000 200");
        await alice.SendGameAsync(FriendExtraService.GiveGift, new GiveFriendGiftReq { RoleId = b, ItemId = 1004000, Count = 200 });
        Assert.Equal(0, (await Next(alice, FriendExtraService.GiveGift, GiveFriendGiftResp.Parser)).Result);
        await Until("Alice", s => s.Achievements!.Infos.GetValueOrDefault(Affection2)?.Status == AchievementStatusType.Complete,
            "affection level 2");
        Assert.Equal([2], (await Info("Alice", 102100401))!.Scores);
    }

    [ClientDataFact]
    public async Task BlockedRowsNeverProgress()
    {
        await using var alice = await InWorld("Alice");
        await Full(alice);
        // 303004's fashion variant (grids 8-13) and 326003 stay at status 0 whatever the role owns.
        await OnLoop("Alice", s => s.Wardrobe!.AllLightUpAppearances[40060010] = new DBAppearanceInfo { AppearanceId = 40060010 });
        await ReachedIncrement(alice, Phoenix, SweepWait);
        var blocked = Service<AchievementService>().AllRows.Where(r => r.Blocked).Select(r => r.Id).ToList();
        var state = await Read("Alice", s => s.Achievements!.Clone());
        Assert.All(blocked, id => Assert.False(state.Infos.ContainsKey(id), $"{id} progressed"));
    }
}
