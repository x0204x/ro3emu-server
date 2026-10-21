using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;
using Ro3Emu.LoadTest;

namespace Ro3Emu.Tests;

/// <summary>5004/5059/43504/31178 over the game gate.</summary>
public sealed class DailyTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-daily-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
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
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private void Sql(string command)
    {
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), command);
    }

    private long OwnedSourceCount(string source, string kind)
    {
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key=$source AND r.command_kind=$kind
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }

    private static async Task FollowWarp(BotClient bot)
    {
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(bot, MsgId.SelfInfoEndFrameNotify, SelfInfoEndFrameNotify.Parser);
    }

    private async Task<T> State<T>(Func<GameSession, T> read) =>
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            Task.FromResult(read(_app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role != null))));

    private Task OwnedFixtureAsync(long role, string origin, Func<GameSession, Task> work)
    {
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        return _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync(origin,
                [ReservationKey.Role(role)], session, () => work(session)));
    }

    private async Task DisposeAndWaitDetachedAsync(BotClient bot, long role)
    {
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var handover = _app.Services.GetRequiredService<RoleHandover>();
        var session = sessions.All().Single(s => s.Role?.Id == role);
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handover.PhaseForTest = (phase, current) =>
        {
            if (phase == "Detached" && ReferenceEquals(current, session)) detached.TrySetResult();
        };
        sessions.UnregisteredForTest = current =>
        {
            if (ReferenceEquals(current, session)) unregistered.TrySetResult();
        };
        try
        {
            await bot.DisposeAsync();
            await Task.WhenAll(detached.Task, unregistered.Task).WaitAsync(Wait);
        }
        finally { handover.PhaseForTest = null; sessions.UnregisteredForTest = null; }
    }

    [Fact]
    public void LegacyDailyBindingMigratesOnceAndKeepsAllNewItemFields()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { DataDir = Path.Combine(_dataDir, "legacy-daily") });
        long roleId;
        using (var old = new AccountStore(options))
        {
            roleId = new RoleStore(old).Create("legacy-daily", 1, "LegacyDaily", 1, 1000, 1)!.Id;
            FixtureSql.Execute(old, """
                    DROP TABLE item;
                    CREATE TABLE item (guid INTEGER PRIMARY KEY AUTOINCREMENT, role_id INTEGER NOT NULL,
                        entry INTEGER NOT NULL, stack INTEGER NOT NULL, pack INTEGER NOT NULL, sort INTEGER NOT NULL,
                        grid INTEGER NOT NULL DEFAULT 0, attrs TEXT, bind INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO item (role_id, entry, stack, pack, sort, bind) VALUES ($role, 1250001, 1, 1, 1, 1);
                    """, ("$role", roleId));
        }
        using var accounts = new AccountStore(options); // opening the old database migrates its item table
        var store = new ItemStore(accounts);
        var item = Assert.Single(store.Load(roleId));
        Assert.Equal(1, item.BindType);
        item.BindType = 0; // The legacy column must not overwrite subsequent binding changes.
        item.BindExpire = 123456;
        item.IsLocked = true;
        item.Attrs = new() { [501] = 2 };
        item.StringAttrs = new() { [510] = "1250001|1|1|0|0" };
        item.Enchantments[17] = new ItemEnchantment { GetTime = 1234, Attr = new() { [103] = 42 } };
        store.Update(item);
        Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(Assert.Single(new ItemStore(accounts).Load(roleId))));
    }

    [ClientDataFact]
    public async Task StageClaimKeepsItemMetadataAndRestacksOnlyCompatibleRewards()
    {
        var bot = await InWorld("Alice");
        try
        {
            await Gm("alice", "!lvl 34");
            await Gm("alice", "!quest done 10105090");
            await Gm("alice", "!quest done 500080");
            var store = _app.Services.GetRequiredService<ItemStore>();
            var seeded = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
            {
                var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role != null);
                // A queued quest award may still publish its inventory copy. Seed under
                // the same character/owner admission so that publication cannot erase it.
                using var held = await CharacterService.Lock(s);
                var items = Enumerable.Range(0, 7).Select(_ => new InvItem
                { Entry = 1250900, Stack = 1, Pack = 1, Sort = CharacterService.NextBagSort(s, 1) }).ToArray();
                items[0].IsLocked = true;
                items[1].Attrs = new() { [501] = 1 };
                items[2].StringAttrs = new() { [510] = "1250900|1|0|0|0" };
                items[3].Enchantments[17] = new ItemEnchantment { GetTime = 1234, Attr = new() { [103] = 42 } };
                items[4].BindType = 1;
                items[5].BindExpire = 4102444800;
                foreach (var item in items)
                {
                    item.Sort = CharacterService.NextBagSort(s, 1);
                    store.Insert(s.Role!.Id, item);
                    s.Items.Add(item);
                }
                return (Role: s.Role!.Id, Protected: items.Take(6).ToDictionary(i => i.Guid, i => JsonSerializer.Serialize(i)),
                    Plain: items[6].Guid);
            });
            await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
            Assert.Equal(0, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
            Assert.Equal(101, await State(s => s.Items.Single(i => i.Guid == seeded.Plain).Stack));
            foreach (var (guid, json) in seeded.Protected)
            {
                Assert.Equal(json, await State(s => JsonSerializer.Serialize(s.Items.Single(i => i.Guid == guid))));
                Assert.Equal(json, JsonSerializer.Serialize(store.Load(seeded.Role).Single(i => i.Guid == guid)));
            }
            Assert.Equal(101, store.Load(seeded.Role).Single(i => i.Guid == seeded.Plain).Stack);
            await bot.DisposeAsync();
            bot = await InWorld("Alice", create: false);
            foreach (var (guid, json) in seeded.Protected)
                Assert.Equal(json, await State(s => JsonSerializer.Serialize(s.Items.Single(i => i.Guid == guid))));
            Assert.Contains(7001, await State(s => s.Daily!.Claims));
        }
        finally { await bot.DisposeAsync(); }
    }

    [ClientDataFact]
    public async Task FreshLoginSendsRowsAndRealEmptyClaimsWithoutFakeStage()
    {
        await using var bot = await InWorld("Alice");
        var rows = await Next(bot, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser);
        Assert.True(rows.SyncAllMark);
        int book = _app.Services.GetRequiredService<SeasonService>().PlayBook;
        var expected = _app.Services.GetRequiredService<GameData>().Table("DailyActivityConfig").Values
            .Where(r => !r.TryGetProperty("_kMultiverseArray", out var books) || books.ValueKind != JsonValueKind.Array
                || books.EnumerateArray().Any(v => GameData.Number(v) is double n && (n == 0 || n == book)))
            .Select(r => (int)GameData.Int(r, "_iID")).Where(id => id > 0).Order().ToArray();
        Assert.Equal(expected, rows.ActivityData.Keys.Order().ToArray());
        Assert.Equal(0, rows.ActivityData[3302].All.TaskCompleteTimes);
        Assert.Equal(1, rows.ActivityData[3302].All.ActivityInfo.ActivityParam.RunRingTask.RemainRingCount);

        var raw = await bot.Inbox.WaitAsync(e => e.IsGame(DailyActivityService.StageSync), Wait);
        var claims = DBStageRewardInfoIncrement.Parser.ParseFrom(raw.Body);
        Assert.True(claims.SyncAllMark);
        Assert.Empty(claims.AllStageRewardsFinished.All.Value);
        Assert.True(raw.Body.AsSpan().EndsWith(new byte[] { 0x52, 0x04, 0x0A, 0x02, 0x0A, 0x00 }));
        Assert.Equal(0, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
    }

    [ClientDataFact]
    public async Task LockedAndInvalidRequestsAreRefusedWithoutMutation()
    {
        await using var bot = await InWorld("Alice");
        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser)).Result);
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 9999 });
        Assert.Equal(ErrorCodes.RewardMissing,
            (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        Assert.Equal(0, await State(s => s.DailyRows!.ActivityData[3302].TaskCompleteTimes));
        Assert.Empty(await State(s => s.Daily!.Claims.ToArray()));
    }

    [Fact]
    public async Task RequestsAtRoleScreenCannotUsePreviousCharacterState()
    {
        await using var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        await StageGate.AssertUnansweredAsync(bot, DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        await StageGate.AssertUnansweredAsync(bot, DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });

        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await bot.SwitchRoleAsync();
        await StageGate.AssertUnansweredAsync(bot, DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
    }

    [ClientDataFact]
    public async Task RingTerminalPaysOnceAndStageClaimPersistsAcrossReconnect()
    {
        var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090"); // Set up the source-proven function 25/5902 gate.
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.CurrencyShort,
            (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);

        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RequestId = 0, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        Assert.Equal(0, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser)).Result);
        int root = await State(s => s.Daily!.ActiveRingStart);
        Assert.Contains(root, new[] { 3001, 3011, 3021 });
        Assert.True(await State(s => s.Tasks!.Infos.ContainsKey(root)));
        Assert.Equal(0, await State(s => s.DailyRows!.ActivityData[3302].ActivityInfo.ActivityParam.RunRingTask.RemainRingCount));

        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RequestId = 0, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        Assert.Equal(ErrorCodes.TaskExists,
            (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser, r => r.Result != 0)).Result);
        for (int id = root; id < root + 10; id++)
        {
            bool checkExp = id == (root == 3021 ? 3030 : root + 3);
            long expectedExp = 60000;
            if (checkExp && root != 3021)
            {
                // 3004/3014 also grant the guaranteed, level-specific EXP terminal of pool 84004/group 3514.
                int level = await State(s => s.Progress!.Level);
                var expRow = _app.Services.GetRequiredService<GameData>().Row("DropGroupConfig", 3514)!.Value
                    .EnumerateArray().Single(r => GameData.Int(r, "_iMinLevelLimit") == level);
                Assert.Equal(GameData.Int(expRow, "_iMinNumber"), GameData.Int(expRow, "_iMaxNumber"));
                expectedExp += GameData.Int(expRow, "_iMinNumber");
            }
            await Gm("alice", $"!quest done {id}");
            if (id < root + 9) Assert.True(await State(s => s.Tasks!.Infos.ContainsKey(id + 1)));
            if (checkExp)
            {
                var expNotice = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
                    m => m.Rewards.Any(r => r.ItemEntry == 100 && r.Stack == expectedExp));
                Assert.Equal(expectedExp, expNotice.Rewards.Where(r => r.ItemEntry == 100).Sum(r => (long)r.Stack));
            }
        }
        Assert.Equal(1, await State(s => s.DailyRows!.ActivityData[3302].TaskCompleteTimes));
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Equal(1, await State(s => s.Items.Single(i => i.Entry == 1250001).BindType));
        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RequestId = 0, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        Assert.Equal(ErrorCodes.AttemptsExhausted,
            (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser, r => r.Result != 0)).Result);

        long coinsBefore = await State(s => s.Progress!.Currency.GetValueOrDefault(2));
        long chipsBefore = await State(s => s.Progress!.Currency.GetValueOrDefault(407));
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        var gain = await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser);
        Assert.Equal(0, gain.Result);
        Assert.Equal(7001, gain.StageId);
        Assert.Contains(7001, await State(s => s.Daily!.Claims));
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5))); // threshold, not spend
        Assert.Equal(100, await State(s => s.Items.Where(i => i.Entry == 1250900 && i.BindType == 0).Sum(i => i.Stack)));
        Assert.Equal(chipsBefore + 200, await State(s => s.Progress!.Currency.GetValueOrDefault(407)));
        Assert.Equal(coinsBefore + 40000, await State(s => s.Progress!.Currency.GetValueOrDefault(2)));

        string stageSource = await State(s => $"daily-stage/{s.Role!.Id}/{s.Daily!.DailyEpoch}/7001");
        Assert.Equal(1, OwnedSourceCount(stageSource, "cd13e/daily-stage"));

        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.RewardClaimed,
            (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser, r => r.Result != 0)).Result);
        await bot.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Contains(7001, await State(s => s.Daily!.Claims));
        await again.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.RewardClaimed,
            (await Next(again, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        Assert.Equal(1, OwnedSourceCount(stageSource, "cd13e/daily-stage"));
    }

    [ClientDataFact]
    public async Task BatchStageClaimPaysEveryListedStageOnceInOneCommit()
    {
        var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!item 5 40");
        Assert.Equal(40, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        async Task<StageRewardBatchGainResp> Batch(BotClient client, params int[] ids)
        {
            var req = new StageRewardBatchGainReq();
            req.StageIds.Add(ids);
            await client.SendGameAsync(DailyActivityService.StageBatchGain, req);
            return await Next(client, DailyActivityService.StageBatchGain, StageRewardBatchGainResp.Parser);
        }

        // 7003 needs 60 liveness: the whole batch is refused and nothing is claimed or paid.
        long coins = await State(s => s.Progress!.Currency.GetValueOrDefault(2));
        Assert.Equal(ErrorCodes.CurrencyShort, (await Batch(bot, 7001, 7002, 7003)).Result);
        Assert.Empty(await State(s => s.Daily!.Claims.ToArray()));
        Assert.Equal(coins, await State(s => s.Progress!.Currency.GetValueOrDefault(2)));
        Assert.Equal(ErrorCodes.RewardMissing, (await Batch(bot, 7001, 9999)).Result);
        Assert.Equal(ErrorCodes.Parameter, (await Batch(bot)).Result);

        var paid = await Batch(bot, 7001, 7002, 7001);
        Assert.Equal(0, paid.Result);
        Assert.Equal(new[] { 7001, 7002, 7001 }, paid.Req.StageIds.ToArray());
        Assert.Equal(new[] { 7001, 7002 }, (await State(s => s.Daily!.Claims.Order().ToArray())));
        Assert.True(await State(s => s.Progress!.Currency.GetValueOrDefault(2)) >= coins + 40000); // 7001 alone pays 40,000
        Assert.Equal(40, await State(s => s.Progress!.Currency.GetValueOrDefault(5))); // threshold, not spend
        var synced = await Next(bot, DailyActivityService.StageSync, DBStageRewardInfoIncrement.Parser,
            m => m.AllStageRewardsFinished?.All?.Value.Count == 2);
        Assert.Equal(new[] { 7001, 7002 }, synced.AllStageRewardsFinished.All.Value.Order().ToArray());
        string source = await State(s => $"daily-stage/{s.Role!.Id}/{s.Daily!.DailyEpoch}/7001,7002");
        Assert.Equal(1, OwnedSourceCount(source, "cd13e/daily-stage"));

        Assert.Equal(ErrorCodes.RewardClaimed, (await Batch(bot, 7002)).Result);
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        await bot.DisposeAsync();

        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(new[] { 7001, 7002 }, (await State(s => s.Daily!.Claims.Order().ToArray())));
        Assert.Equal(ErrorCodes.RewardClaimed, (await Batch(again, 7001, 7002)).Result);
        Assert.Equal(1, OwnedSourceCount(source, "cd13e/daily-stage"));
    }

    [ClientDataFact]
    public async Task RingTerminalSqlAbortKeepsQuestRewardAndRowRetryableAfterReconnect()
    {
        var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        Assert.Equal(0, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser)).Result);
        int root = await State(s => s.Daily!.ActiveRingStart);
        for (int id = root; id < root + 9; id++) await Gm("alice", $"!quest done {id}");
        long roleId = await State(s => s.Role!.Id);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        byte[] dailyBefore = roles.LoadDaily(roleId)!, tasksBefore = roles.LoadTasks(roleId)!;
        var progressBefore = roles.LoadProgress(roleId);
        var itemsBefore = _app.Services.GetRequiredService<ItemStore>().Load(roleId).Select(i => (i.Entry, i.Stack, i.BindType)).ToArray();
        Sql("CREATE TRIGGER fail_daily_ring BEFORE INSERT ON item WHEN NEW.entry=1250001 BEGIN SELECT RAISE(ABORT, 'ring item fault'); END");
        await Gm("alice", $"!quest done {root + 9}");
        Assert.Equal(dailyBefore, roles.LoadDaily(roleId));
        Assert.Equal(tasksBefore, roles.LoadTasks(roleId));
        Assert.Equal(progressBefore.Currency.OrderBy(kv => kv.Key), roles.LoadProgress(roleId).Currency.OrderBy(kv => kv.Key));
        Assert.Equal(progressBefore.Exp, roles.LoadProgress(roleId).Exp);
        Assert.Equal(itemsBefore, _app.Services.GetRequiredService<ItemStore>().Load(roleId).Select(i => (i.Entry, i.Stack, i.BindType)));
        Assert.Equal(0, await State(s => s.DailyRows!.ActivityData[3302].TaskCompleteTimes));
        Assert.Equal(root, await State(s => s.Daily!.ActiveRingStart));
        Sql("DROP TRIGGER fail_daily_ring");
        await bot.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        await Gm("alice", $"!quest done {root + 9}");
        Assert.Equal(1, await State(s => s.DailyRows!.ActivityData[3302].TaskCompleteTimes));
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Equal(1, await State(s => s.Items.Single(i => i.Entry == 1250001).BindType));
    }

    [ClientDataFact]
    public async Task ResetClearsDailyQuestCurrencyAndTypeSevenClaimsButKeepsOtherClaims()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!lvl 34"); // Pool 3501's level-34 EXP row is exactly 127716.
        await Gm("alice", "!quest done 500080"); // accepted quest settlement, one configured 20-point grant
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        var initialKeys = await State(s => s.DailyRows!.ActivityData.Keys.Order().ToArray());
        var priorExp = await State(s => (s.Progress!.Level, s.Progress.Exp));
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(0, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        var grant = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
            m => m.Rewards.Any(r => r.ItemEntry == 407 && r.Stack == 200));
        Assert.Equal(127716, grant.Rewards.Single(r => r.ItemEntry == 100).Stack);
        Assert.Equal(40000, grant.Rewards.Single(r => r.ItemEntry == 2).Stack);
        // Pool 80001 guarantees one via 261001; slot 261002 has a 10% chance of a second.
        int shards = grant.Rewards.Single(r => r.ItemEntry == 1301007).Stack;
        Assert.InRange(shards, 1, 2);
        Assert.Equal(shards, await State(s => s.Items.Where(i => i.Entry == 1301007).Sum(i => i.Stack)));
        Assert.Equal(100, grant.Rewards.Single(r => r.ItemEntry == 1250900).Stack);
        Assert.Equal(200, grant.Rewards.Single(r => r.ItemEntry == 407).Stack);
        Assert.False(await State(s => s.Items.Any(i => i.Entry is 100 or 101)));
        Assert.NotEqual(priorExp, await State(s => (s.Progress!.Level, s.Progress.Exp)));
        long role = await State(s => s.Role!.Id);
        await OwnedFixtureAsync(role, "daily reset fixture", async s =>
        {
            s.Daily!.Claims.Add(1); // A shared non-daily claim, retained across the daily reset.
            s.Daily.DailyEpoch -= 86400;
            _app.Services.GetRequiredService<RoleStore>().SaveDaily(s.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
            await _app.Services.GetRequiredService<DailyActivityService>().ReconcileAsync(s);
        });
        var resetRows = await Next(bot, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser,
            m => !m.SyncAllMark && m.ActivityData.ContainsKey(101) && m.ActivityData[101].Inc?.TaskCompleteTimes == 0);
        Assert.Equal(0, resetRows.ActivityData[101].Inc.TaskCompleteTimes);
        var resetCurrency = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => !m.SyncAllMark && m.Currency.ContainsKey(5) && m.Currency[5].All == 0);
        Assert.Equal(0, resetCurrency.Currency[5].All);
        var keptClaim = await Next(bot, DailyActivityService.StageSync, DBStageRewardInfoIncrement.Parser,
            m => m.AllStageRewardsFinished?.All?.Value.Contains(1) == true);
        Assert.Equal(new[] { 1 }, keptClaim.AllStageRewardsFinished.All.Value);
        Assert.Equal(0, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Equal(0, await State(s => s.DailyRows!.ActivityData[101].TaskCompleteTimes));
        Assert.Equal(initialKeys, await State(s => s.DailyRows!.ActivityData.Keys.Order().ToArray()));
        Assert.Equal(new[] { 1 }, await State(s => s.Daily!.Claims.Order().ToArray()));
        Assert.Equal(0, await State(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(500080)));
    }

    [ClientDataFact]
    public async Task DailyResetLedgerFailureRollsBackWholePeriodAndHeldRetryOwnsOneSource()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!quest done 500080");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var daily = _app.Services.GetRequiredService<DailyActivityService>();
        long role = await State(s => s.Role!.Id);
        // Arm the failure before making the reset due: the daily ticker can run
        // as soon as the setup callback releases the loop.
        Sql("""
            CREATE TRIGGER fail_e02_reset_ledger BEFORE INSERT ON economy_entry
            WHEN NEW.reason_code=122 BEGIN SELECT RAISE(ABORT, 'daily reset ledger failure'); END
            """);
        await OwnedFixtureAsync(role, "daily reset rollback fixture", s =>
        {
            s.Daily!.DailyEpoch -= 86400;
            roles.SaveDaily(role, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
            s.WildAfk!.Info.IsSuspended = true; // No old-day clock accrual during this reset fixture.
            s.WildAfk.DailyEpoch = s.Daily.DailyEpoch;
            var afkBytes = JsonSerializer.SerializeToUtf8Bytes(s.WildAfk);
            s.WildAfk.Persisted = afkBytes;
            roles.SaveWildAfk(role, afkBytes);
            return Task.CompletedTask;
        });
        byte[] oldDaily = roles.LoadDaily(role)!, oldTasks = roles.LoadTasks(role)!, oldAfk = roles.LoadWildAfk(role)!;
        long oldLiveness = roles.LoadProgress(role).Currency[5];
        Assert.Equal(20, oldLiveness);
        long ResetReceipts()
        {
            using var db = accounts.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/daily-reset'";
            return (long)command.ExecuteScalar()!;
        }
        await Assert.ThrowsAsync<SqliteException>(() => loop.InvokeAsync(() => daily.ReconcileAsync(
            _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == role))));
        Assert.Equal(oldDaily, roles.LoadDaily(role));
        Assert.Equal(oldTasks, roles.LoadTasks(role));
        Assert.Equal(oldAfk, roles.LoadWildAfk(role));
        Assert.Equal(oldLiveness, roles.LoadProgress(role).Currency[5]);
        Assert.Equal(0, ResetReceipts());

        await using var bob = await InWorld("Bob");
        long bobRole = (long)bob.Self!.Uuid;
        Assert.False(await loop.InvokeAsync(() => Task.FromResult(
            _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == bobRole)
                .Dungeons!.FirstSetted)));
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13e/daily-reset") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var opening = S3EconomyReader.Read(_dataDir, catalog);
        Sql("DROP TRIGGER fail_e02_reset_ledger");
        var pending = loop.InvokeAsync(() => daily.ReconcileAsync(
            _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == role)));
        Task? gm = null, timer = null;
        byte[]? stagedDaily = null, stagedTasks = null, stagedAfk = null;
        long stagedLiveness = -1;
        try
        {
            var first = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
            if (first == pending) await pending; // Preserve the actual reset failure rather than masking it as a gate timeout.
            stagedDaily = roles.LoadDaily(role);
            stagedTasks = roles.LoadTasks(role);
            stagedAfk = roles.LoadWildAfk(role);
            stagedLiveness = roles.LoadProgress(role).Currency.GetValueOrDefault(5);
            Assert.Equal(JsonSerializer.Deserialize<DailyActivityService.DailyState>(oldDaily)!.DailyEpoch,
                JsonSerializer.Deserialize<DailyActivityService.DailyState>(stagedDaily!)!.DailyEpoch);
            Assert.Equal(0, ResetReceipts());
            gm = Gm("alice", "!item 1000000 1");
            timer = loop.InvokeAsync(() => daily.ReconcileAsync(
                _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == role)));
            await loop.InvokeAsync(() => _app.Services.GetRequiredService<DungeonService>().SetGuideAsync(
                _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == bobRole),
                new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = true })).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(JsonSerializer.Deserialize<DungeonService.State>(roles.LoadDungeons(bobRole)!)!.FirstSetted);
            await Assert.ThrowsAsync<TimeoutException>(() => gm.WaitAsync(TimeSpan.FromMilliseconds(150)));
            await Assert.ThrowsAsync<TimeoutException>(() => timer.WaitAsync(TimeSpan.FromMilliseconds(150)));
            Assert.Equal(stagedDaily, roles.LoadDaily(role));
            Assert.Equal(stagedTasks, roles.LoadTasks(role));
            Assert.Equal(stagedAfk, roles.LoadWildAfk(role));
            Assert.Equal(stagedLiveness, roles.LoadProgress(role).Currency.GetValueOrDefault(5));
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        if (gm != null) await gm.WaitAsync(TimeSpan.FromSeconds(10));
        if (timer != null) await timer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, roles.LoadProgress(role).Currency[5]);
        Assert.NotEqual(oldDaily, roles.LoadDaily(role));
        Assert.NotEqual(oldTasks, roles.LoadTasks(role));
        Assert.NotEqual(oldAfk, roles.LoadWildAfk(role));
        Assert.Equal(1, ResetReceipts());
        var closing = S3EconomyReader.Read(_dataDir, catalog);
        var ledger = S3EconomyReader.ReadLedger(_dataDir, catalog, opening, closing)!;
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, asset => { Assert.Equal("0", asset.Residual); Assert.Equal("0", asset.LegResidual); });
        var liveness = Assert.Single(check.Assets, x => x.Asset.Kind == EconomyAssetKind.Currency && x.Asset.Id == 5);
        Assert.Equal("0", liveness.Residual); Assert.Equal("0", liveness.LegResidual);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        var reset = Assert.Single(ledger.Receipts, r => r.CommandKind == "cd13e/daily-reset");
        Assert.Single(reset.SourceKeys!);
        Assert.Equal(stagedLiveness, Assert.Single(ledger.Legs, l => l.OperationId == reset.OperationId).Delta * -1);
    }

    [ClientDataFact]
    public async Task WeeklyOnlyResetWithZeroLivenessRetainsAnItemlessReceipt()
    {
        await using var bot = await InWorld("Alice");
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var daily = _app.Services.GetRequiredService<DailyActivityService>();
        long role = await State(s => s.Role!.Id);
        var before = JsonSerializer.Deserialize<DailyActivityService.DailyState>(roles.LoadDaily(role)!)!;
        long oldWeek = before.WeeklyEpoch;
        before.WeeklyEpoch -= 7 * 86400;
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var opening = S3EconomyReader.Read(_dataDir, catalog);
        await OwnedFixtureAsync(role, "weekly reset fixture", async session =>
        {
            session.Daily!.WeeklyEpoch = before.WeeklyEpoch;
            roles.SaveDaily(role, JsonSerializer.SerializeToUtf8Bytes(session.Daily));
            Assert.Equal(0, roles.LoadProgress(role).Currency.GetValueOrDefault(5));
            await daily.ReconcileAsync(session);
        });
        Assert.Equal(0, roles.LoadProgress(role).Currency.GetValueOrDefault(5));
        var after = JsonSerializer.Deserialize<DailyActivityService.DailyState>(roles.LoadDaily(role)!)!;
        Assert.Equal(before.DailyEpoch, after.DailyEpoch);
        Assert.Equal(oldWeek, after.WeeklyEpoch);
        Assert.Equal(0, roles.LoadProgress(role).Currency.GetValueOrDefault(5));
        var closing = S3EconomyReader.Read(_dataDir, catalog);
        var ledger = S3EconomyReader.ReadLedger(_dataDir, catalog, opening, closing)!;
        var reset = Assert.Single(ledger.Receipts, r => r.CommandKind == "cd13e/daily-reset");
        Assert.Single(reset.SourceKeys!);
        Assert.DoesNotContain(ledger.Legs, l => l.OperationId == reset.OperationId);
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(exact.Assets, asset => { Assert.Equal("0", asset.Residual); Assert.Equal("0", asset.LegResidual); });
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => daily.ReconcileAsync(
            _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == role)));
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/daily-reset'";
        Assert.Equal(1, (long)command.ExecuteScalar()!);
    }

    [ClientDataFact]
    public async Task LoginAfterDayResetSendsOneFullQuestSyncBeforeTheScene()
    {
        // A 5027 increment before the first scene made the client refresh NPC sprites without a current map
        // (GetAllNPCData nil index); the day's re-accepted tracking quests must ride in the login's one full 5027.
        var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!quest done 500080");
        long roleId = await State(s => s.Role!.Id);
        await DisposeAndWaitDetachedAsync(bot, roleId);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var saved = JsonSerializer.Deserialize<DailyActivityService.DailyState>(roles.LoadDaily(roleId)!)!;
        saved.DailyEpoch -= 86400;
        roles.SaveDaily(roleId, JsonSerializer.SerializeToUtf8Bytes(saved));

        await using var again = await InWorld("Alice", create: false);
        var events = again.Inbox.Snapshot().ToList();
        int scene = events.FindIndex(e => e.IsGame(MsgId.SwitchSceneLine));
        Assert.True(scene > 0);
        var questSyncs = events.Take(scene).Where(e => e.IsGame(TaskService.TaskInfoSync))
            .Select(e => DBTaskInfoIncrement.Parser.ParseFrom(e.Body)).ToList();
        var full = Assert.Single(questSyncs);
        Assert.True(full.SyncAllMark);
        Assert.Equal(TaskState.Running, full.Infos[500080].All.Status);
    }

    [ClientDataFact]
    public async Task OfflineLoginReconcilesEpochBeforeQuestAndCurrencySync()
    {
        var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!quest done 500080");
        long roleId = await State(s => s.Role!.Id);
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        await DisposeAndWaitDetachedAsync(bot, roleId);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var saved = JsonSerializer.Deserialize<DailyActivityService.DailyState>(roles.LoadDaily(roleId)!)!;
        saved.DailyEpoch -= 86400;
        roles.SaveDaily(roleId, JsonSerializer.SerializeToUtf8Bytes(saved));

        await using var again = await InWorld("Alice", create: false);
        var daily = await Next(again, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser);
        Assert.Equal(0, daily.ActivityData[101].All.TaskCompleteTimes);
        Assert.Equal(1, daily.ActivityData[3302].All.ActivityInfo.ActivityParam.RunRingTask.RemainRingCount);
        var currency = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(0, currency.Currency[5].All);
        Assert.Equal(0, await State(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(500080)));
    }

    [ClientDataFact]
    public async Task MvpSettlementAdapterKeepsPartialFiveStepProgressWithoutExtraPoints()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 37");
        await Gm("alice", "!quest done 10105090");
        Assert.True(await State(s => s.Tasks!.Infos.ContainsKey(500010)));
        Assert.False(await State(s => s.Tasks!.Infos.ContainsKey(2001000))); // condition 88000082 requires level 999
        for (int n = 1; n <= 5; n++)
        {
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
            {
                var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
                await _app.Services.GetRequiredService<TaskService>().OnMvpParticipantAsync(s);
            });
            Assert.Equal(n, await State(s => s.DailyRows!.ActivityData[105].TaskCompleteTimes));
            Assert.Equal(n * 4, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        }
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
            await _app.Services.GetRequiredService<TaskService>().OnMvpParticipantAsync(s);
        });
        Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Equal(5, await State(s => s.DailyRows!.ActivityData[105].TaskCompleteTimes));
    }

    [ClientDataFact]
    public async Task HeldStoryAwardDoesNotBlockDailySettlement()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 37");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!quest accept 10104260");
        await Gm("alice", "!quest finish 10104260"); // Its ConsumeItem 1009001 cost is absent, so its award stays held.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
            var daily = s.Tasks!.Infos[500010];
            s.Tasks.Infos.Remove(500010);
            s.Tasks.Infos[500010] = daily; // Exercise settlement after the held story task in the same snapshot.
            await _app.Services.GetRequiredService<TaskService>().OnMvpParticipantAsync(s);
            await _app.Services.GetRequiredService<TaskService>().RefreshAsync(s); // A passive refresh cannot replay the event.
        });
        Assert.Equal(TaskState.Finish, await State(s => s.Tasks!.Infos[10104260].Status));
        Assert.Equal(0, await State(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(10104260)));
        Assert.Equal(1, await State(s => s.Tasks!.CompleteTaskNum[500010]));
        Assert.Equal(1, await State(s => s.DailyRows!.ActivityData[105].TaskCompleteTimes));
        Assert.Equal(4, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
    }

    [ClientDataFact]
    public async Task DailyClonePreservesAnotherCompletedManualQuestInTheSameSettle()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 37");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!item 1009013 10");
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
            s.Tasks!.Infos[10102050] = new TaskInfo
            {
                TaskId = 10102050, Status = TaskState.Running,
                CompleteNum = { 0 }
            }; // A real item-submit objective; settlement must read committed inventory.
            var point = _app.Services.GetRequiredService<WorldData>().Map(10213)!.Points[1010203001];
            s.SceneEntry = 10213;
            s.Position = new Vec3 { X = point.X, Y = point.Y, Z = point.Z };
            return Task.CompletedTask;
        });
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
            await _app.Services.GetRequiredService<TaskService>().OnMvpParticipantAsync(s);
        });
        Assert.Equal(1, await State(s => s.DailyRows!.ActivityData[105].TaskCompleteTimes));
        Assert.Equal(TaskState.Finish, await State(s => s.Tasks!.Infos[10102050].Status));
        Assert.Equal(10, await State(s => s.Tasks!.Infos[10102050].CompleteNum[0]));
        await bot.SendGameAsync(TaskService.TaskDelivery,
            new TaskDeliveryReq
            {
                Req = { new TaskDeliverySingleReq { Id = 10102050 },
                new TaskDeliverySingleReq { Id = 10102050 } }
            });
        var delivery = await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser);
        Assert.Equal(0, Assert.Single(delivery.Data).Result);
        Assert.Equal(ErrorCodes.TaskStatus, (await Next(bot, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode);
        Assert.Equal(1, await State(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(10102050)));
        Assert.Equal(0, await State(s => _app.Services.GetRequiredService<CharacterService>().CountOf(s, 1009013)));
    }

    [ClientDataFact]
    public async Task SqlAbortDuringClaimItemWriteRollsBackClaimCurrencyAndExp()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!quest done 500080");
        int rewardsBefore = bot.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.GetRewardNotify));
        var roles = _app.Services.GetRequiredService<RoleStore>();
        long roleId = await State(s => s.Role!.Id);
        byte[] oldDaily = roles.LoadDaily(roleId)!;
        var before = await State(s => (Currency: s.Progress!.Currency.OrderBy(kv => kv.Key).ToArray(), Exp: s.Progress.Exp,
            Items: s.Items.Select(i => (i.Entry, i.Stack, i.BindType)).ToArray(), Claims: s.Daily!.Claims.ToArray()));
        Sql("CREATE TRIGGER fail_daily_claim BEFORE INSERT ON item WHEN NEW.entry=1250900 BEGIN SELECT RAISE(ABORT, 'daily item fault'); END");
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.Unknown,
            (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        var after = await State(s => (Currency: s.Progress!.Currency.OrderBy(kv => kv.Key).ToArray(), Exp: s.Progress.Exp,
            Items: s.Items.Select(i => (i.Entry, i.Stack, i.BindType)).ToArray(), Claims: s.Daily!.Claims.ToArray()));
        Assert.Equal(before.Currency, after.Currency);
        Assert.Equal(before.Exp, after.Exp);
        Assert.Equal(before.Items, after.Items);
        Assert.Equal(before.Claims, after.Claims);
        Assert.Equal(rewardsBefore, bot.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.GetRewardNotify)));
        Assert.Equal(oldDaily, roles.LoadDaily(roleId));
        Assert.Equal(before.Currency, roles.LoadProgress(roleId).Currency.OrderBy(kv => kv.Key));
        Assert.Equal(before.Exp, roles.LoadProgress(roleId).Exp);
        Assert.Equal(before.Items, _app.Services.GetRequiredService<ItemStore>().Load(roleId)
            .Select(i => (i.Entry, i.Stack, i.BindType)));
        Sql("DROP TRIGGER fail_daily_claim");
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(0, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser, r => r.Result == 0)).Result);
    }

    [ClientDataFact]
    public async Task ThreeEligibleDaysRotateThroughEveryConfiguredRingNode()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        var roots = new HashSet<int>();
        for (int day = 0; day < 3; day++)
        {
            if (day > 0)
            {
                long epoch = await State(s => s.Daily!.DailyEpoch);
                DateTime tomorrow = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.Date.AddDays(1).AddHours(6);
                await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
                {
                    var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
                    await _app.Services.GetRequiredService<DailyActivityService>().ReconcileAsync(s, tomorrow);
                });
            }
            await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
            { ActivityId = 3302, RunRingTaskAccept = new RunRingTaskAcceptReq() });
            Assert.Equal(0, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser,
                r => r.Result == 0)).Result);
            int root = await State(s => s.Daily!.ActiveRingStart);
            Assert.True(roots.Add(root), $"root {root} repeated on day {day}");
            for (int id = root; id < root + 10; id++) await Gm("alice", $"!quest done {id}");
            Assert.Equal(1, await State(s => s.DailyRows!.ActivityData[3302].TaskCompleteTimes));
            Assert.Equal(20, await State(s => s.Progress!.Currency.GetValueOrDefault(5)));
        }
        Assert.Equal(new[] { 3001, 3011, 3021 }, roots.Order());
    }

    [ClientDataFact]
    public async Task RingDialogueAndMapKillAdvanceOnlyFromTheirAuthoritativeSources()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await bot.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = 3302, RunRingTaskAccept = new RunRingTaskAcceptReq() });
        Assert.Equal(0, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser)).Result);
        int root = await State(s => s.Daily!.ActiveRingStart);
        var (point, dialog, map) = root switch
        {
            3001 => (100015L, 2060101, 10540L),
            3011 => (200008L, 2060121, 10315L),
            _ => (100018L, 2060141, 10150L),
        };
        // Ring roots rotate by local day; only one dialogue point is near Prontera's default spawn.
        var dialogPoint = _app.Services.GetRequiredService<WorldData>().Map(10100)!.Points[point];
        await Gm("alice", FormattableString.Invariant($"!warp 10100 {dialogPoint.X} {dialogPoint.Z}"));
        await FollowWarp(bot);
        await bot.SendGameAsync(SceneService.ClientNpcInteract,
            new ClientNpcInteractReq { Areapointid = point, DialogID = dialog });
        await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(root + 1));
        Assert.Equal(1, await State(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(root)));
        Assert.True(await State(s => s.Tasks!.Infos.ContainsKey(root + 1)));
        await Gm("alice", $"!warp {map}");
        await FollowWarp(bot);

        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null);
            var combat = _app.Services.GetRequiredService<CombatService>();
            var killed = combat.Killed.Single(h => h.Target is TaskService);
            var template = _app.Services.GetRequiredService<MonsterService>().Template(5000036)!;
            var point = new SpawnPoint(-1, 0, 0, 0, 0, template.NpcId);
            var group = new SpawnGroup(-1, false, 1, 0, [], [point]);
            var monster = new MonsterUnit { SceneEntry = 10100, Template = template, Group = group, Home = point, Hp = template.MaxHp };
            await killed(s, monster); // A kill on another map cannot satisfy 103043.
            Assert.Equal(0, s.Tasks!.Infos[root + 1].CompleteNum[0]);
            monster.SceneEntry = map;
            for (int n = 0; n < 20; n++) await killed(s, monster);
        });
        Assert.Equal(1, await State(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(root + 1)));
        Assert.Equal(0, await State(s => s.DailyRows!.ActivityData[3302].TaskCompleteTimes));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
