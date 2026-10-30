using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

/// <summary>全自造配置驗證自訂補售、真購買／切箭封包與交易；不是原服販售價或原生玩家驗收。</summary>
public sealed partial class ArrowShopTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private string _fixture = "";
    private SyntheticHost? _host;
    private SyntheticHost Host => _host ?? throw new InvalidOperationException("測試尚未啟動");
    private T Service<T>() where T : notnull => Host.Service<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private static ArrowShopOffer First => ArrowShopCatalog.Offers[0];

    public Task InitializeAsync() { _fixture = CreateFixture(); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_fixture, true);
    }

    [Fact]
    public async Task FiveElementalArrowPurchasesUse100ZenyAndSwitchFavouriteCountersPersistAfterRestart()
    {
        await Start();
        long role, opening;
        await using (var bot = await Host.InWorldAsync("ArrowBatch"))
        {
            var s = Session(bot); role = s.Role!.Id; await Fund(s); opening = Balance(s);
            Assert.Equal(ErrorCodes.ShopClosed, (await Buy(bot, First.CommodityId)).Result);
            Assert.Equal(opening, Balance(s)); Assert.DoesNotContain(s.Items, i => ArrowIds.Contains(i.Entry));
            await Ready(s);
            var batch = new BatchBuyCommodityReq();
            foreach (var offer in ArrowShopCatalog.Offers)
                batch.Items.Add(new BatchBuyCommodityItem { CommodityId = offer.CommodityId, Count = 2, Price = 1 });
            await bot.SendGameAsync(ShopService.BatchBuyCommodity, batch);
            var reply = BatchBuyCommodityResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(p => p.IsGame(ShopService.BatchBuyCommodity), Wait)).Body);
            Assert.Equal(0, reply.Result); Assert.Equal(5, reply.Results.Count);
            Assert.All(reply.Results, item => Assert.Equal((0, 100, 2), (item.Result, item.Price, item.BuyTimes)));
            Assert.Equal(opening - 1000, Balance(s));
            foreach (var offer in ArrowShopCatalog.Offers)
            {
                Assert.Equal(2, Count(s, offer.ItemId));
                await bot.SendGameAsync(CharacterService.ArrowSwitch, new ItemArrowSwitchReq { ItemId = offer.ItemId });
                var selected = ItemArrowSwitchResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(p => p.IsGame(CharacterService.ArrowSwitch), Wait)).Body);
                Assert.Equal((0, offer.ItemId, 2), (selected.Result, selected.ItemId, selected.ItemCount));
            }
            await bot.SendGameAsync(ShopService.CommodityCollect, new CommodityCollectReq
            { CommodityId = First.CommodityId, OpType = CollectOperateType.Add });
            Assert.Equal(0, CommodityCollectResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(p => p.IsGame(ShopService.CommodityCollect), Wait)).Body).Result);
            Assert.Contains(First.CommodityId, SavedShop(role).Favorites);
        }
        await Host.RestartAsync();
        await using var again = await Host.InWorldAsync("ArrowBatch", create: false);
        var reloaded = Session(again);
        Assert.Equal(opening - 1000, Balance(reloaded));
        Assert.Equal(ArrowShopCatalog.Offers[^1].ItemId, reloaded.BagState.Info.CurrentArrowID);
        Assert.Contains(First.CommodityId, reloaded.Shop!.Favorites);
        foreach (var offer in ArrowShopCatalog.Offers)
        { Assert.Equal(2, Count(reloaded, offer.ItemId)); Assert.Equal(2, SavedShop(role).Bought[offer.CommodityId].Count); }
        var sync = DBShopInfoIncrement.Parser.ParseFrom((await again.Inbox.WaitAsync(p => p.IsGame(ShopService.ShopSync), Wait)).Body);
        Assert.All(ArrowShopCatalog.Offers, offer => Assert.Equal(2, sync.CommodityBuyTimes[offer.CommodityId].All));
    }

    [Fact]
    public async Task FailedArrowPurchaseRollsBackPaymentGoodsAndCounterThenRetryCommits()
    {
        await Start();
        await using var bot = await Host.InWorldAsync("ArrowRollback");
        var s = Session(bot); long role = s.Role!.Id; await Fund(s); await Ready(s);
        Assert.Equal(0, (await Buy(bot, First.CommodityId, 3)).Result);
        long before = Balance(s);
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER arrow_shop_fail BEFORE UPDATE OF shop ON role
            BEGIN SELECT RAISE(ABORT, '自造箭矢購買交易故障'); END
            """);
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() => Service<ShopService>().BuyAsync(s,
                new BuyCommodityReq { CommodityId = First.CommodityId, Count = 2, Price = 1 })));
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER arrow_shop_fail"); }
        Assert.Equal(before, Balance(s)); Assert.Equal(3, Count(s, First.ItemId));
        Assert.Equal(3, SavedShop(role).Bought[First.CommodityId].Count);
        Assert.Equal(3, s.Shop!.Bought[First.CommodityId].Count);
        Assert.Equal(before, Service<RoleStore>().LoadProgress(role).Currency[ArrowShopCatalog.CurrencyId]);
        var retry = await Buy(bot, First.CommodityId, 2);
        Assert.Equal((0, 100, 5), (retry.Result, retry.Price, retry.BuyTimes));
        Assert.Equal(before - 200, Balance(s)); Assert.Equal(5, Count(s, First.ItemId));
        Assert.Equal(5, SavedShop(role).Bought[First.CommodityId].Count);
    }

    private Task Start() => StartCore();
    private async Task StartCore() => _host = await SyntheticHost.StartAsync(o => o.GameDataDir = _fixture);
    private Task Fund(GameSession s) => Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(s, [(ArrowShopCatalog.CurrencyId, 5000)]));
    private Task Ready(GameSession s) => Service<GameLoop>().InvokeAsync(async () => { await Service<CharacterService>().SetLevelAsync(s, 3, null); });
    private long Balance(GameSession s) => Service<CharacterService>().CountOf(s, ArrowShopCatalog.CurrencyId);
    private static long Count(GameSession s, int item) => s.Items.Where(i => i.Entry == item).Sum(i => (long)i.Stack);
    private static int[] ArrowIds => ArrowShopCatalog.Offers.Select(offer => offer.ItemId).ToArray();
    private ShopService.ShopState SavedShop(long role) => JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(role)!)!;
    private static async Task<BuyCommodityResp> Buy(BotClient bot, int id, int count = 1)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = id, Count = count, Price = 1 });
        return BuyCommodityResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(p => p.IsGame(ShopService.BuyCommodity), Wait)).Body);
    }
}
