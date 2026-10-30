using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class LifeFoodSeasonTests
{
    [Fact]
    public async Task FailedLoginReconciliationRetainsOldBlobAndFoodUntilSuccessfulRetry()
    {
        await using var bot = await _host.InWorldAsync("FoodRollback");
        long role = (long)bot.Self!.Uuid; var s = Session(role); long attack = Power(s);
        await Give(s, 2); Assert.Equal(0, await Eat(bot, s, 2));
        byte[] beforeLife = Service<RoleStore>().LoadLifeJob(role)!, beforeRuntime = Service<RoleStore>().LoadLifeRuntime(role)!;
        await Advance();
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER food_load_fail BEFORE UPDATE OF life_runtime ON role
            BEGIN SELECT RAISE(ABORT, '自造料理換季交易失敗'); END
            """);
        try { await Assert.ThrowsAsync<SqliteException>(() => ReconcileLogin(s)); }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER food_load_fail"); }
        Assert.Equal(beforeLife, Service<RoleStore>().LoadLifeJob(role));
        Assert.Equal(beforeRuntime, Service<RoleStore>().LoadLifeRuntime(role));
        AssertIdentity(s.LifeRuntime, 1);
        Assert.Equal(2, s.LifeJob!.MagicFoodTimes[Food]);
        Assert.Equal(attack + 2 * PerUse, Power(s));
        await ReconcileLogin(s);
        Assert.Empty(SavedLife(role).MagicFoodTimes);
        Assert.Empty(s.LifeJob.MagicFoodTimes);
        AssertIdentity(SavedRuntime(role), 2);
        Assert.Equal(attack, Power(s));
        Assert.Equal(0, Bag(role));
    }

    [Fact]
    public async Task PendingLoginReconciliationPublishesCountIdentityAndAttributesOnlyAfterCommit()
    {
        await using var bot = await _host.InWorldAsync("FoodPending");
        long role = (long)bot.Self!.Uuid; var s = Session(role); long attack = Power(s);
        await Give(s, 2); Assert.Equal(0, await Eat(bot, s, 2));
        await Advance();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = Service<AccountStore>();
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13c/life-login-reconcile") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        Task pending = ReconcileLogin(s);
        try
        {
            await entered.Task.WaitAsync(Wait);
            Assert.False(pending.IsCompleted);
            AssertIdentity(s.LifeRuntime, 1); AssertIdentity(SavedRuntime(role), 1);
            Assert.Equal(2, s.LifeJob!.MagicFoodTimes[Food]);
            Assert.Equal(2, SavedLife(role).MagicFoodTimes[Food]);
            Assert.Equal(attack + 2 * PerUse, Power(s));
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        await pending.WaitAsync(Wait);
        AssertIdentity(s.LifeRuntime, 2); AssertIdentity(SavedRuntime(role), 2);
        Assert.Empty(s.LifeJob!.MagicFoodTimes);
        Assert.Equal(attack, Power(s));
    }

    [Fact]
    public async Task FailedFoodUseDoesNotLoseItemCountIdentityOrAttributesAndRetryPersists()
    {
        long role, attack;
        await using (var bot = await _host.InWorldAsync("FoodUseFail"))
        {
            role = (long)bot.Self!.Uuid; var s = Session(role); attack = Power(s);
            await Give(s, 2); Assert.Equal(0, await Eat(bot, s, 1));
            long guid = s.Items.Single(i => i.Entry == Food).Guid;
            FixtureSql.Execute(Service<AccountStore>(), """
                CREATE TRIGGER food_use_fail BEFORE INSERT ON operation_receipt
                WHEN NEW.command_kind='cd13c/life-food'
                BEGIN SELECT RAISE(ABORT, '自造料理食用交易失敗'); END
                """);
            try
            {
                await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() =>
                    Service<CharacterService>().UseItemAsync(s, new ItemUseReq { ItemGuid = guid, ItemCount = 1 })));
            }
            finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER food_use_fail"); }
            Assert.Equal(1, Bag(role)); Assert.Equal(1, s.Items.Single(i => i.Entry == Food).Stack);
            Assert.Equal(1, SavedLife(role).MagicFoodTimes[Food]);
            AssertIdentity(s.LifeRuntime, 1); AssertIdentity(SavedRuntime(role), 1);
            Assert.Equal(attack + PerUse, Power(s));
            Assert.Equal(0, await Eat(bot, s, 1));
            Assert.Equal(0, Bag(role));
            Assert.Equal(attack + 2 * PerUse, Power(s));
        }
        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("FoodUseFail", create: false);
        Assert.Equal(2, SavedLife(role).MagicFoodTimes[Food]);
        Assert.Equal(0, Bag(role));
        AssertIdentity(SavedRuntime(role), 1);
        Assert.Equal(attack + 2 * PerUse, Power(Session(role)));
    }

    // 重現 RoleSystems 的載入順序，保留角色預約；不把測試直接改 SeasonState 當成正式換季。
    private Task ReconcileLogin(GameSession s) => Service<GameLoop>().InvokeAsync(async () =>
    {
        using var held = await CharacterService.Lock(s);
        await Service<LifeJobService>().LoadAsync(s);
        Service<SeasonService>().Load(s);
        await Service<CharacterService>().RefreshStatsAsync(s);
        await Service<LifeJobService>().LoginAsync(s);
    });

    private sealed class FoodClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 3, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-food-season-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
        // 全部列與數值由本測試自造；未複製原客戶端料理、配方或取得機率。
        string config = Path.Combine(root, "config");
        Row("ItemConfig", Food, new
        {
            _iID = Food, _iType = 2, _iBagTab = 1, _iStackLimit = 50,
            _iIsUse = 1, _iUseEffect = new[] { new[] { 607, Food } }, _iLevelNeed = 1,
            _kMultiverseArray = new[] { Book }
        });
        Row("LifeMagicFoodAttrConfig", Food, new
        {
            _iID = Food, _kLimit = Limit,
            _kAttrs = new[] { new[] { Attack, PerUse } }, _kMultiverseArray = new[] { Book }
        });
        Row("RestrictConfig", LifeJobService.VigorId, new { _iID = LifeJobService.VigorId, _iMaxRestrictNum = 500 });
        Row("RestrictConfig", LifeJobService.VigorUsesId, new { _iID = LifeJobService.VigorUsesId, _iMaxRestrictNum = 3 });
        Row("MultiverseConfig", Book, new { _iID = Book, _iDanSystem = Array.Empty<int>() });
        Row("MultiverseMainStage", 906942, new { _iSeasonNum = 1, _iStartTime = 1, _iMainStageLength = 1 });
        return root;

        void Row(string table, int key, object row)
        {
            string path = Path.Combine(config, table + ".json");
            var rows = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            rows[key.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(row);
            File.WriteAllText(path, rows.ToJsonString());
        }
    }
}
