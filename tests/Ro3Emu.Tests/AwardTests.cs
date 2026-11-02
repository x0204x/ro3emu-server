using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.LoadTest;

namespace Ro3Emu.Tests;

/// <summary>Actual AwardConfig resolution and durable grants.</summary>
public sealed class AwardTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-awards-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly int[] PoolTerminalEntries = [1200002, 1200003, 1420100, 1430100, 1440100, 1450100, 1450702, 2];
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _dataDir;
            options.GameDataDir = Path.Combine(RepoRoot(), "data");
            options.HttpPort = httpPort;
            options.MicroGatePort = microPort;
            options.GameGatePort = gamePort;
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

    [ClientDataFact]
    public async Task MultiStackOwnedGrantRollsBackAndThenCommitsOneSourceAndReceipt()
    {
        await using var bot = await InWorld("GrantAlice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "GrantAlice");
        var character = _app.Services.GetRequiredService<CharacterService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        long role = session.Role!.Id;
        long initial = items.Load(role).Where(i => i.Entry == 1000000).Sum(i => (long)i.Stack);
        var goods = new (int Entry, int Count, int Bind)[] { (1000000, 2500, 0), (1, 7, 0) };
        long Receipts()
        {
            using var db = accounts.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/grant'";
            return (long)command.ExecuteScalar()!;
        }

        FixtureSql.Execute(accounts, """
            CREATE TRIGGER e02_grant_fail BEFORE INSERT ON operation_receipt
            WHEN NEW.command_kind='cd13e/grant' BEGIN SELECT RAISE(ABORT, 'owned grant rollback'); END
            """);
        await Assert.ThrowsAsync<SqliteException>(() => loop.InvokeAsync(() => character.GrantBoundAsync(session, goods)));
        Assert.Equal(initial, items.Load(role).Where(i => i.Entry == 1000000).Sum(i => (long)i.Stack));
        Assert.Equal(0, Receipts());
        Assert.Equal(0, session.Progress!.Currency.GetValueOrDefault(1) - 1000);
        FixtureSql.Execute(accounts, "DROP TRIGGER e02_grant_fail");

        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var opening = S3EconomyReader.Read(_dataDir, catalog);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13e/grant") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var pending = loop.InvokeAsync(() => character.GrantBoundAsync(session, goods));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(initial, session.Items.Where(i => i.Entry == 1000000).Sum(i => (long)i.Stack));
            Assert.Equal(initial, items.Load(role).Where(i => i.Entry == 1000000).Sum(i => (long)i.Stack));
            Assert.Equal(0, Receipts());
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = items.Load(role).Where(i => i.Entry == 1000000).ToArray();
        Assert.Equal(initial + 2500, saved.Sum(i => (long)i.Stack));
        Assert.True(saved.Length > 1);
        Assert.Equal(1, Receipts());
        Assert.Equal(1007, session.Progress!.Currency[1]);
        Assert.Contains(saved[0].Guid, session.NewItems);
        var closing = S3EconomyReader.Read(_dataDir, catalog);
        var ledger = S3EconomyReader.ReadLedger(_dataDir, catalog, opening, closing)!;
        Assert.Single(ledger.Receipts);
        Assert.Single(ledger.Receipts[0].SourceKeys!);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, asset => { Assert.Equal("0", asset.Residual); Assert.Equal("0", asset.LegResidual); });
    }

    [ClientDataFact]
    public async Task HalfRatesScaleAwardExpAndZenyOnce()
    {
        await using var bot = await InWorld("HalfAwardAlice");
        await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        await Next(bot, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);
        var rates = _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.RewardRates;
        rates.BaseExperience = rates.JobExperience = rates.Zeny = 0.5m;
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Name == "HalfAwardAlice");
        var character = _app.Services.GetRequiredService<CharacterService>();
        var awards = _app.Services.GetRequiredService<AwardService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        long before = session.Progress!.Currency[1];
        var plan = new AwardPlan(0, 0, awards.ScaleRewardGrants(
            [new AwardGrant(1, 10), new AwardGrant(100, 4), new AwardGrant(101, 4)]));

        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
            plan, taskBaseExp: awards.ScaleQuestBaseExperience(2),
            ownedAward: new CharacterService.OwnedAwardSource("cd13e/grant",
                $"test/half-rate/{session.Role!.Id}", new { Version = 1 }))));

        var notice = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
            message => message.Rewards.Any(reward => reward.ItemEntry == 1 && reward.Stack == 5));
        Assert.Contains(notice.Rewards, reward => reward.ItemEntry == 100 && reward.Stack == 3);
        Assert.Contains(notice.Rewards, reward => reward.ItemEntry == 101 && reward.Stack == 2);
        Assert.Equal(before + 5, session.Progress.Currency[1]);
    }

    [ClientDataFact]
    public void FixedPlaybookEmptyAndUnresolvedPolicies()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var awards = new AwardService(data);
        var defaultPlan = awards.Resolve(null!, 1001);
        Assert.Equal(1001001, defaultPlan.AwardId);
        Assert.Equal(new AwardGrant(1, 5000), Assert.Single(defaultPlan.Grants));
        Assert.Equal(defaultPlan.Grants.ToArray(), awards.Resolve(null!, 1001, 103).Grants.ToArray()); // explicit zero falls back
        Assert.Equal(new AwardGrant(1250900, 10), Assert.Single(awards.Resolve(null!, 4).Grants)); // preview says 30
        Assert.Equal(new AwardGrant(2, 200000, 1), Assert.Single(awards.Resolve(null!, 31116).Grants));
        // 新版已移除這個群組；任務仍透過既有 ResolveTask 的直接 Award fallback 發獎。
        if (Ro3Emu.Server.Data.Config.MultiverseGroupConfig.Get(data, 920160) == null)
            Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, 920160));
        var taskPlan = awards.ResolveTask(null!, 920160, null);
        Assert.Equal(920160, taskPlan.AwardId);
        Assert.Equal(new AwardGrant(920132, 2000, 1), Assert.Single(taskPlan.Grants));
        Assert.Empty(awards.Resolve(null!, 31104).Grants); // novice trial's separate TaskConfig EXP remains
        Assert.Equal(new[] { new AwardGrant(102, 100), new AwardGrant(2, 2500) },
            awards.Resolve(null!, 2131).Grants.ToArray()); // declared but empty drop pool keeps its fixed grants
        Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, long.MaxValue)); // absent group in every client export
        Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, 21)); // type 1, NPC drop route
        Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, 40007)); // multirow day selector
        Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, 1005)); // AwardConfig _kEXP
        Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, 1002)); // AwardConfig _kCondition
        Assert.Throws<AwardResolutionException>(() => awards.Resolve(null!, 25001)); // restriction replacement
    }

    [ClientDataFact]
    public async Task BoundMintedCoinUsesCurrencyBalanceAndBoundNotification()
    {
        await using var bot = await InWorld("CoinAlice");
        await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "CoinAlice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var store = _app.Services.GetRequiredService<ItemStore>();
        var awards = new AwardService(_app.Services.GetRequiredService<GameData>());

        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, awards.Resolve(session, 31116))));
        var currency = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => !m.SyncAllMark && m.Currency.TryGetValue(2, out var value) && value.HasAll && value.All == 200000);
        Assert.Equal(200000, currency.Currency[2].All);
        var notify = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
            m => m.Rewards.Any(r => r.ItemEntry == 2 && r.Stack == 200000));
        var mintedCoin = Assert.Single(notify.Rewards);
        Assert.Equal(ItemBindType.EitemBindTypeIsBind, mintedCoin.ItemBindInfo.ItemBindType);
        Assert.True(mintedCoin.ItemInfos.ContainsKey(10001));
        Assert.Equal(200000, roles.LoadProgress(session.Role!.Id).Currency[2]);
        Assert.DoesNotContain(store.Load(session.Role.Id), i => i.Entry == 2);

        long expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        foreach (var invalid in new[] { new AwardGrant(2, 1, 2, expiry), new AwardGrant(2, 1, 1, expiry) })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await loop.InvokeAsync(() => character.ApplyAwardAsync(session, new AwardPlan(0, 0, [invalid])));
            });
        }
        Assert.Equal(200000, roles.LoadProgress(session.Role.Id).Currency[2]);
    }

    [ClientDataFact]
    public async Task FixedCurrencyExpBoundItemAndRollbackPersistWithActualWireState()
    {
        await using var bot = await InWorld("AwardAlice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var awards = new AwardService(_app.Services.GetRequiredService<GameData>());
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var store = _app.Services.GetRequiredService<ItemStore>();
        var session = sessions.All().Single(s => s.Role?.Name == "AwardAlice");
        long roleId = session.Role!.Id;
        await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser); // initial full state
        await Next(bot, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);

        var runtime = new TaskRuntimeState { CompletedGuides = [987654321] };
        byte[] runtimeBlob = JsonSerializer.SerializeToUtf8Bytes(runtime);
        bool committed = await loop.InvokeAsync(() => character.ApplyAwardAsync(session, awards.Resolve(session, 1001),
            taskBaseExp: 7, costs: [(1, 100)], taskRuntimeState: runtimeBlob));
        Assert.True(committed);
        var bag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(bag.Currency.ContainsKey(1));
        var progression = await Next(bot, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(2, progression.Exp); // 5 of the seven granted EXP paid for level 2.
        var notify = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
            m => m.Rewards.Any(r => r.ItemEntry == 1 && r.Stack == 5000));
        Assert.Equal(1, Assert.Single(notify.RewardType));
        Assert.Contains(notify.Rewards, r => r.ItemEntry == 100 && r.Stack == 7 && r.ItemInfos.ContainsKey(10001));
        Assert.Equal(5900, roles.LoadProgress(roleId).Currency[1]);
        Assert.Equal(2, roles.LoadProgress(roleId).Level);
        Assert.Equal(2, roles.LoadProgress(roleId).Exp);
        Assert.Contains(987654321, JsonSerializer.Deserialize<TaskRuntimeState>(roles.LoadTaskRuntime(roleId)!)!.CompletedGuides);
        Assert.Contains(987654321, session.TaskRuntime.CompletedGuides);

        long expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var bound = new AwardPlan(0, 0, [new AwardGrant(1200000, 2, 1), new AwardGrant(1200000, 1, 2, expiry)]);
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, bound)));
        var boundBag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => !m.SyncAllMark && m.PackInfo.ContainsKey(1) && m.PackInfo[1].Inc?.Items.Count >= 2);
        Assert.Contains(boundBag.PackInfo[1].Inc.Items.Values,
            i => i.All?.ItemBindInfo.ItemBindType == ItemBindType.EitemBindTypeIsBind);
        Assert.Contains(boundBag.PackInfo[1].Inc.Items.Values,
            i => i.All?.ItemBindInfo.ExpireTimeStamp == expiry);
        var boundNotify = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
            m => m.Rewards.Any(r => r.ItemEntry == 1200000 && r.ItemBindInfo.ItemBindType == ItemBindType.EitemBindTypeIsBind));
        Assert.Equal(2, boundNotify.Rewards.Count);
        Assert.Equal(expiry, boundNotify.Rewards.Single(r => r.ItemBindInfo.ItemBindType == ItemBindType.EitemBindTypeTimeLimitedBind)
            .ItemBindInfo.ExpireTimeStamp);
        var saved = store.Load(roleId).Where(i => i.Entry == 1200000).ToList();
        Assert.Contains(saved, i => i.BindType == 1 && i.Stack == 2);
        Assert.Contains(saved, i => i.BindType == 2 && i.ExpireTimeStamp == expiry && i.Stack == 1);
        Assert.All(saved, i => Assert.True(i.Guid > 0));

        long currencyBefore = roles.LoadProgress(roleId).Currency[1];
        Assert.False(await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
            new AwardPlan(0, 0, [new AwardGrant(1, 123)]), costs: [(1, int.MaxValue)])));
        Assert.Equal(currencyBefore, roles.LoadProgress(roleId).Currency[1]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
                new AwardPlan(0, 0, [new AwardGrant(1, 123), new AwardGrant(999999999, 1)]), costs: [(1, 10)]));
        });
        Assert.Equal(currencyBefore, roles.LoadProgress(roleId).Currency[1]);
    }

    [ClientDataFact]
    public async Task PoolUsesActualTerminalsAndNeverPreview()
    {
        await using var bot = await InWorld("PoolAlice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "PoolAlice");
        var awards = new AwardService(_app.Services.GetRequiredService<GameData>());
        var alternate = awards.Resolve(session, 1001, 102);
        Assert.Equal(1021001, alternate.AwardId);
        Assert.Contains(alternate.Grants, g => g.Entry == 2 && g.BindType == 1); // group 205 terminal
        var plan = awards.Resolve(session, 1040);
        Assert.Equal(1001040, plan.AwardId);
        Assert.Contains(plan.Grants, g => g.Entry == 100 && g.Count == 1500);
        Assert.Contains(plan.Grants, g => g.Entry == 101 && g.Count == 1000);
        Assert.Contains(plan.Grants, g => g.Entry == 2 && g.Count == 2000);
        Assert.InRange(plan.Grants.Count, 3, 4); // inferred 50% pool gate, max one terminal
        if (plan.Grants.Count == 4)
            Assert.Contains(plan.Grants[3].Entry, PoolTerminalEntries);
        Assert.DoesNotContain(plan.Grants, g => g.Entry is 1200500 or 1200200 or 1200300); // preview-only
    }

    [ClientDataFact]
    public async Task FirstGearInAnUnsentPackResendsTheWholeBag()
    {
        // Once the starter weapon and shirt are worn, a later login's 5009 has no equipment pack (2). The client
        // builds a pack's grid only from a full sync, so the first loose gear must bring 5009 whole again.
        await using var bot = await InWorld("PackAlice");
        await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "PackAlice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        // Stand-in for that later login: the kit gear worn, then a fresh full 5009 (no pack 2).
        await loop.InvokeAsync(async () =>
        {
            int grid = 1;
            foreach (var worn in session.Items.Where(i => i.Pack == 2)) worn.Grid = grid++;
            session.Synced.Remove(CharacterService.ItemsSync);
            Assert.True(await character.ApplyAwardAsync(session, new AwardPlan(0, 0, [new AwardGrant(1000000, 1)])));
        });
        var relogin = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.False(relogin.PackInfo.ContainsKey(2));
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
            new AwardPlan(0, 0, [new AwardGrant(2010120, 2)]))));
        var bag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(2));
        Assert.True(bag.SyncAllMark);
        Assert.Equal(2, bag.PackInfo[2].All.Items.Values.Count(i => i.ItemEntry == 2010120));
        Assert.True(bag.PackInfo.ContainsKey(1)); // the whole bag, not just the new pack

        // Once the client has the pack, more gear travels as an increment again.
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
            new AwardPlan(0, 0, [new AwardGrant(2010120, 1)]))));
        var more = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.TryGetValue(2, out var p) && p.Inc != null);
        Assert.False(more.SyncAllMark);
    }

    [ClientDataFact]
    public async Task NoviceAndSwordsmanPoolGiftKeepsActualPermanentBind()
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var store = _app.Services.GetRequiredService<ItemStore>();
        var awards = new AwardService(_app.Services.GetRequiredService<GameData>());
        foreach (var (name, career) in new[] { ("GiftNovice", 1000), ("GiftSword", 1100) })
        {
            await using var bot = await InWorld(name);
            await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            var session = sessions.All().Single(s => s.Role?.Name == name);
            if (career != 1000) await loop.InvokeAsync(async () => { await character.SetJobAsync(session, career); });

            var plan = awards.Resolve(session, 31125);
            Assert.Equal(10031125, plan.AwardId);
            Assert.Equal(new AwardGrant(1501014, 1, 1), Assert.Single(plan.Grants));
            Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session, plan)));

            var bag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => !m.SyncAllMark && m.PackInfo.TryGetValue(1, out var pack) && pack.Inc?.Items.Values.Any(i =>
                    i.All?.ItemEntry == 1501014 && i.All.ItemBindInfo.ItemBindType == ItemBindType.EitemBindTypeIsBind) == true);
            Assert.Contains(bag.PackInfo[1].Inc.Items.Values, i => i.All?.ItemEntry == 1501014);
            var notify = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
                m => m.Rewards.Any(r => r.ItemEntry == 1501014));
            var gift = Assert.Single(notify.Rewards);
            Assert.Equal(1, gift.Stack);
            Assert.Equal(ItemBindType.EitemBindTypeIsBind, gift.ItemBindInfo.ItemBindType);
            Assert.Equal(0, gift.ItemBindInfo.ExpireTimeStamp);
            Assert.True(gift.ItemInfos.ContainsKey(10001));
            var saved = Assert.Single(store.Load(session.Role!.Id), i => i.Entry == 1501014);
            Assert.Equal(1, saved.BindType);
            Assert.Equal(0, saved.ExpireTimeStamp);
        }
    }

    [ClientDataFact]
    public async Task ItemUseRejectsInvalidCountAndUnsupportedEffectWithoutConsumingIt()
    {
        await using var bot = await InWorld("UseAlice");
        var bag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long potion = bag.PackInfo[1].All.Items.Values.Single(i => i.ItemEntry == 1000000).ItemGuid;
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "UseAlice");
        var store = _app.Services.GetRequiredService<ItemStore>();

        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 0 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(20, store.Load(session.Role!.Id).Single(i => i.Guid == potion).Stack);

        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        await loop.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(19, store.Load(session.Role.Id).Single(i => i.Guid == potion).Stack);

        // 10051001 [[608,10051001],[206,10051001]]: server-hosted 608 UseTotemSkillBook_Sever has no implementation (the
        // Fly Wing and the 105 horn used to be the example; both are implemented now, StoryBlockerTests, ItemActionTests).
        Assert.True(await loop.InvokeAsync(() => character.ApplyAwardAsync(session,
            new AwardPlan(0, 0, [new AwardGrant(10051001, 1)]))));
        long unsupported = store.Load(session.Role.Id).Single(i => i.Entry == 10051001).Guid;
        await loop.InvokeAsync(() => { session.Progress!.Level = 10; return Task.CompletedTask; }); // its _iLevelNeed
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = unsupported, ItemCount = 1 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(1, store.Load(session.Role.Id).Single(i => i.Guid == unsupported).Stack);
    }

    /// <summary>Job Advance asks Base 30 and Job 40 together: the Base EXP up to Base 30 from quests alone reaches Job 40.</summary>
    [ClientDataFact]
    public async Task QuestExpUpToBaseThirtyReachesJobFortyOfTheSecondRank()
    {
        await using var bot = await InWorld("JobAlice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "JobAlice");
        var data = _app.Services.GetRequiredService<GameData>();
        long toBaseThirty = Enumerable.Range(1, 29).Sum(level => GameData.Int(data.Row("BaseLvConfig", level)!.Value, "_iBaseExp"));
        Assert.True(await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            session.Progress!.Career = 1100;
            session.Progress.Level = 1; session.Progress.Exp = 0; session.Progress.JobLevel = 1; session.Progress.JobExp = 0;
            return _app.Services.GetRequiredService<CharacterService>()
                .ApplyAwardAsync(session, new AwardPlan(0, 0, []), taskBaseExp: toBaseThirty);
        }));
        Assert.Equal(30, session.Progress!.Level);
        Assert.Equal(40, session.Progress.JobLevel);
    }

    /// <summary>An older role's first login pays what its Base EXP should have brought, once, even without its marker.</summary>
    [ClientDataFact]
    public async Task AnOlderRoleGetsItsMissingJobExpOnceAtLogin()
    {
        await using var bot = await InWorld("CatchAlice");
        Assert.Equal(40, await RelogAs(bot, "CatchAlice", level: 30, jobLevel: 1, older: true)); // Base 30's share fills the rank
        Assert.Equal(1, await RelogAs(bot, "CatchAlice", level: 30, jobLevel: 1, older: false)); // the marker holds
        Assert.Equal(1, await RelogAs(bot, "CatchAlice", level: 30, jobLevel: 1, older: true)); // and the natural source without it
    }

    /// <summary>The catch-up's receipt commits the marker with the EXP, and the ledger certifies it like any other grant.</summary>
    [ClientDataFact]
    public async Task TheLedgerAcceptsAJobExpCatchUpReceipt()
    {
        await using var bot = await InWorld("LedgerAlice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "LedgerAlice");
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        long role = session.Role!.Id;
        await loop.InvokeAsync(() =>
        {
            var progress = session.Progress!;
            progress.Career = 1100; progress.Level = 30; progress.Exp = 0; progress.JobLevel = 1; progress.JobExp = 0;
            roles.SaveProgress(role, progress);
            roles.SaveJobExpCatchUp(role, []);
            return Task.CompletedTask;
        });
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        await bot.SwitchRoleAsync();
        var opening = S3EconomyReader.Read(_dataDir, catalog);
        await bot.EnterWorldAsync(role); await WorldSettle.StallsAsync(_app.Services);
        Assert.Equal(40, await loop.InvokeAsync(() => Task.FromResult(session.Progress!.JobLevel)));

        var closing = S3EconomyReader.Read(_dataDir, catalog);
        var ledger = S3EconomyReader.ReadLedger(_dataDir, catalog, opening, closing)!;
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Contains(ledger.Receipts, receipt => receipt.SourceKeys!.Contains($"job-exp-catchup/{role}"));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    /// <summary>A role above its share loses nothing, and its one chance is used up all the same.</summary>
    [ClientDataFact]
    public async Task ARoleAboveItsShareKeepsItsJobExpAndIsDecided()
    {
        await using var bot = await InWorld("KeepAlice");
        Assert.Equal(30, await RelogAs(bot, "KeepAlice", level: 1, jobLevel: 30, older: true));
        Assert.Equal(1, await RelogAs(bot, "KeepAlice", level: 30, jobLevel: 1, older: false));
    }

    /// <summary>What does not fit the current rank is frozen and paid by the next advances, never worked out again.</summary>
    [ClientDataFact]
    public async Task TheRestOfTheJobExpDebtIsPaidByLaterAdvances()
    {
        await using var bot = await InWorld("RestAlice");
        var data = _app.Services.GetRequiredService<GameData>();
        long JobTotal(int rank) => Enumerable.Range(1, 39).Sum(level => GameData.Int(data.Row("JobLvConfig", rank * 100L + level)!.Value, "_iJobeExp"));
        long baseTotal = Enumerable.Range(1, 39).Sum(level => GameData.Int(data.Row("BaseLvConfig", level)!.Value, "_iBaseExp"));
        long firstRank = Enumerable.Range(1, 9).Sum(level => GameData.Int(data.Row("JobLvConfig", 100L + level)!.Value, "_iJobeExp"));
        long owed = baseTotal * 2000 / 10000 - firstRank;

        // Rank 2 at Base 40: the rank is filled, and what used to be lost stays owed.
        Assert.Equal(40, await RelogAs(bot, "RestAlice", level: 40, jobLevel: 1, older: true));
        var state = CatchUpState("RestAlice");
        Assert.Equal(JobTotal(2), state.Granted);
        long rest = owed - JobTotal(2);
        Assert.Equal(rest, state.Remaining);
        Assert.Equal(40, await Relog(bot, "RestAlice"));
        Assert.Equal(rest, CatchUpState("RestAlice").Remaining);

        // The second job change: rank 3 takes all it has room for.
        await Advance(bot, 1200);
        Assert.Equal(40, Progress("RestAlice").JobLevel);
        rest -= JobTotal(3);
        Assert.Equal(rest, CatchUpState("RestAlice").Remaining);
        Assert.Equal(40, await Relog(bot, "RestAlice"));
        Assert.Equal(rest, CatchUpState("RestAlice").Remaining);

        // The third: rank 4 has room for more than is left, so the debt ends, and the role keeps what it got.
        await Advance(bot, 1300);
        var progress = Progress("RestAlice");
        long earned = progress.JobExp + Enumerable.Range(1, progress.JobLevel - 1).Sum(level => GameData.Int(data.Row("JobLvConfig", 400L + level)!.Value, "_iJobeExp"));
        Assert.Equal(rest, earned);
        Assert.Equal(0, CatchUpState("RestAlice").Remaining);
        Assert.Equal(owed, CatchUpState("RestAlice").Granted);
        int jobLevel = progress.JobLevel;
        Assert.Equal(jobLevel, await Relog(bot, "RestAlice"));
    }

    /// <summary>A crash after the job change and before its payment: the next login pays it, and only once.</summary>
    [ClientDataFact]
    public async Task AnAdvanceLeftUnpaidIsPaidAtTheNextLoginOnce()
    {
        await using var bot = await InWorld("CrashAlice");
        Assert.Equal(40, await RelogAs(bot, "CrashAlice", level: 40, jobLevel: 1, older: true));
        long before = CatchUpState("CrashAlice").Remaining;
        Assert.Equal(40, await RelogAs(bot, "CrashAlice", level: 40, jobLevel: 1, older: false, career: 1200));
        var state = CatchUpState("CrashAlice");
        Assert.True(state.Remaining < before);
        Assert.Equal(1, await RelogAs(bot, "CrashAlice", level: 40, jobLevel: 1, older: false, career: 1200));
        Assert.Equal(state.Remaining, CatchUpState("CrashAlice").Remaining);
    }

    /// <summary>The last rank has no advance after it: the role takes what fits, keeps it, and nothing is taken away.</summary>
    [ClientDataFact]
    public async Task ARoleOfTheLastRankLosesNothing()
    {
        await using var bot = await InWorld("TopAlice");
        Assert.Equal(40, await RelogAs(bot, "TopAlice", level: 99, jobLevel: 15, older: true, career: 1400));
        var state = CatchUpState("TopAlice");
        Assert.True(state.Granted > 0);
        Assert.Equal(40, await Relog(bot, "TopAlice"));
        Assert.Equal(state.Granted, CatchUpState("TopAlice").Granted);
    }

    private RoleProgress Progress(string name) =>
        _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == name).Progress!;

    private CharacterService.JobExpCatchUpState CatchUpState(string name)
    {
        long role = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == name).Role!.Id;
        return JsonSerializer.Deserialize<CharacterService.JobExpCatchUpState>(_app.Services.GetRequiredService<RoleStore>().LoadJobExpCatchUp(role)!)!;
    }

    private static async Task Advance(BotClient bot, int job)
    {
        using var paid = bot.Inbox.Expect(e => e.IsGame(CharacterService.GetRewardNotify));
        await bot.SendGameAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = job });
        Assert.Equal(0, (await Next(bot, CharacterService.PlayerTransformJob, PlayerTransformJobResp.Parser, r => r.JobId == job)).Result);
        await paid.WaitAsync(Wait);
    }

    private async Task<int> Relog(BotClient bot, string name)
    {
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == name);
        long role = session.Role!.Id;
        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(role); await WorldSettle.StallsAsync(_app.Services);
        return await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(session.Progress!.JobLevel));
    }

    private async Task<int> RelogAs(BotClient bot, string name, int level, int jobLevel, bool older, int career = 1100)
    {
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == name);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        long role = session.Role!.Id;
        await loop.InvokeAsync(() =>
        {
            var progress = session.Progress!;
            progress.Career = career; progress.Level = level; progress.Exp = 0; progress.JobLevel = jobLevel; progress.JobExp = 0;
            roles.SaveProgress(role, progress);
            if (older) roles.SaveJobExpCatchUp(role, []);
            return Task.CompletedTask;
        });
        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(role); await WorldSettle.StallsAsync(_app.Services);
        return await loop.InvokeAsync(() => Task.FromResult(session.Progress!.JobLevel));
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id); await WorldSettle.StallsAsync(_app.Services);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var envelope = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(envelope.Body);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
