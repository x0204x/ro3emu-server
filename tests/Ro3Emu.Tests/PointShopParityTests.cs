using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>原件配置契約與真實82332／5024管線；測試準備的等級／代幣不代表玩家取得方式驗收。</summary>
public sealed class PointShopParityTests : IAsyncLifetime
{
    private const int PetFunction = 70113, MonsterFunction = 70208, PvpFunction = 70114;
    private const int PetCommodity = 21102, PetToken = 1250710, PetGoods = 1633003;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private SyntheticHost _host = null!;
    private T Service<T>() where T : notnull => _host.Service<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    public async Task InitializeAsync() => _host = await SyntheticHost.StartAsync(o =>
    { o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.PlayBook = 101; });
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [ClientDataFact]
    public void ActualPointShopCatalogueKeepsPetHatsPvpAndTowerContracts()
    {
        var data = Service<GameData>();
        var pet = Commodities(210); Assert.Equal(5, pet.Length);
        Assert.All(pet, row => Assert.Equal(new long[] { PetToken, 500 }, Price(row)));
        Assert.Equal(0, GameData.Int(data.Row("ShopCommodityConfig", 21101)!.Value, "_iPurchaseNum"));
        Assert.All(pet.Where(row => GameData.Int(row, "_iID") != 21101), row =>
        { Assert.Equal(50, GameData.Int(row, "_iPurchaseNum")); Assert.Equal(4, GameData.Int(row, "_iPurchaseTimesLimit")); });
        var hats = Commodities(260); Assert.Equal(20, hats.Length);
        Assert.Equal(10, hats.Count(row => Price(row).SequenceEqual(new long[] { 500, 400 })));
        Assert.Equal(10, hats.Count(row => Price(row).SequenceEqual(new long[] { 500, 200 })));
        Assert.All(hats, row =>
        { Assert.Equal(1, GameData.Int(row, "_iPurchaseNum")); Assert.Equal(4, GameData.Int(row, "_iPurchaseTimesLimit")); });
        var pvp = Commodities(250); Assert.Equal(9, pvp.Length);
        Assert.All(pvp, row => Assert.Equal(6, Price(row)[0]));
        Assert.Equal(new long[] { 1002, 999 }, Condition(8000041));
        var tower = data.Row("ShopCommodityConfig", 24101)!.Value;
        Assert.Equal(new long[] { 1, 25000 }, Price(tower));
        Assert.Equal(3000, GameData.Int(tower, "_iDiscount"));
        Assert.Equal(new long[] { 1014, 10 }, Condition(GameData.Int(tower, "_iIsBuy")));
        Assert.Equal(1, GameData.Int(tower, "_iPurchaseNum"));
        Assert.Equal(5, GameData.Int(tower, "_iPurchaseTimesLimit"));
        Assert.All(pet.Concat(hats).Concat(pvp).Append(tower), row =>
        {
            Assert.NotNull(data.Row("ItemConfig", GameData.Int(row, "_iItemId")));
            Assert.NotNull(data.Row("ItemConfig", Price(row)[0]));
        });

        JsonElement[] Commodities(long classId) => data.Table("ShopCommodityConfig").Values
            .Where(row => GameData.Int(row, "_iClassID") == classId).ToArray();
        long[] Condition(long id) => data.Row("LC_ConditionList", id)!.Value.GetProperty("m_kSubConditionList")[0]
            .GetProperty("m_kData").EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
    }

    [ClientDataFact]
    public async Task Base31PetOpensThenMonsterAt37AndPvpAt40Publish5056()
    {
        await using var bot = await _host.InWorldAsync("PointGates");
        var s = Session(bot); var functions = Service<FunctionService>();
        foreach (var (level, changed, monster, pvp) in new[]
                 { (31, PetFunction, false, false), (37, MonsterFunction, true, false), (40, PvpFunction, true, true) })
        {
            await SetLevel(s, level);
            var update = FunctionOpenInfoIncrement.Parser.ParseFrom((await bot.Inbox.WaitAsync(packet =>
                packet.IsGame(FunctionService.FunctionOpenSync) && FunctionOpenInfoIncrement.Parser.ParseFrom(packet.Body)
                    .FunctionOpenInfos.TryGetValue(changed, out var opened) && opened.All, Wait)).Body);
            Assert.True(update.FunctionOpenInfos[changed].All);
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Assert.True(functions.IsOpen(s, PetFunction));
                Assert.Equal(monster, functions.IsOpen(s, MonsterFunction));
                Assert.Equal(pvp, functions.IsOpen(s, PvpFunction));
                var view = functions.Build(s).FunctionOpenInfos;
                Assert.True(view.ContainsKey(PetFunction));
                Assert.Equal(monster, view.ContainsKey(MonsterFunction));
                Assert.Equal(pvp, view.ContainsKey(PvpFunction));
                return Task.CompletedTask;
            });
        }
        var data = Service<GameData>();
        int visible = data.Table("ShopCommodityConfig").Values.Where(row => GameData.Int(row, "_iClassID") == 250)
            .Count(row => functions.ConditionMet(s, GameData.Int(row, "_iIsLock")));
        Assert.Equal(8, visible); // 25103 的 Base999 顯示鎖不因已開競技分頁而放行。
    }

    [ClientDataFact]
    public async Task Pet500PriceLifetime50RollbackAndReloginUseActualPurchaseProtocol()
    {
        long role, balance, initialGoods;
        await using (var bot = await _host.InWorldAsync("PointPet"))
        {
            var s = Session(bot); role = s.Role!.Id;
            await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(s, [(PetToken, 26000)]));
            balance = Currency(s); initialGoods = Goods(s);
            Assert.Equal(ErrorCodes.ShopClosed, (await Buy(bot, 1)).Result);
            Assert.Equal(balance, Currency(s)); Assert.Equal(initialGoods, Goods(s));
            await SetLevel(s, 10);
            var bought = await Buy(bot, 49);
            Assert.Equal((0, 500, 49), (bought.Result, bought.Price, bought.BuyTimes));
            await ShopCount(bot, 49);
            Assert.Equal(balance - 24500, Currency(s)); Assert.Equal(initialGoods + 49, Goods(s));
            FixtureSql.Execute(Service<AccountStore>(), """
                CREATE TRIGGER point_pet_fail BEFORE UPDATE OF shop ON role
                BEGIN SELECT RAISE(ABORT, '自造寵物商城交易故障'); END
                """);
            try
            {
                await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() =>
                    Service<ShopService>().BuyAsync(s, new BuyCommodityReq { CommodityId = PetCommodity, Count = 1 })));
            }
            finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER point_pet_fail"); }
            Assert.Equal(balance - 24500, Currency(s)); Assert.Equal(initialGoods + 49, Goods(s));
            Assert.Equal(49, SavedShop(role).Bought[PetCommodity].Count);
            Assert.Equal(49, s.Shop!.Bought[PetCommodity].Count);
            var last = await Buy(bot, 1);
            Assert.Equal((0, 500, 50), (last.Result, last.Price, last.BuyTimes));
            await ShopCount(bot, 50);
            Assert.Equal(balance - 25000, Currency(s)); Assert.Equal(initialGoods + 50, Goods(s));
            Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(bot, 1)).Result);
        }
        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("PointPet", create: false);
        var reloaded = Session(again);
        await ShopCount(again, 50);
        Assert.Equal(balance - 25000, Currency(reloaded)); Assert.Equal(initialGoods + 50, Goods(reloaded));
        Assert.Equal("", SavedShop(role).Bought[PetCommodity].Period);
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(again, 1)).Result);
        Assert.Equal(balance - 25000, Currency(reloaded)); Assert.Equal(initialGoods + 50, Goods(reloaded));
    }

    private Task SetLevel(GameSession s, int level) => Service<GameLoop>().InvokeAsync(async () =>
    { await Service<CharacterService>().SetLevelAsync(s, level, null); });
    private long Currency(GameSession s) => Service<CharacterService>().CountOf(s, PetToken);
    private static long Goods(GameSession s) => s.Items.Where(i => i.Entry == PetGoods).Sum(i => (long)i.Stack);
    private ShopService.ShopState SavedShop(long role) => JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(role)!)!;
    private static long[] Price(JsonElement row) => row.GetProperty("_kItemPerPrice").EnumerateArray()
        .Select(value => (long)(GameData.Number(value) ?? 0)).ToArray();
    private static async Task<BuyCommodityResp> Buy(BotClient bot, int count)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity,
            new BuyCommodityReq { CommodityId = PetCommodity, Count = count, Price = 1 });
        return BuyCommodityResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(p => p.IsGame(ShopService.BuyCommodity), Wait)).Body);
    }
    private static async Task ShopCount(BotClient bot, int count)
    {
        await bot.Inbox.WaitAsync(p => p.IsGame(ShopService.ShopSync) && DBShopInfoIncrement.Parser.ParseFrom(p.Body)
            .CommodityBuyTimes.TryGetValue(PetCommodity, out var value) && value.All == count, Wait);
    }
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
