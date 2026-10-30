using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

/// <summary>自造配置驗證舊補售已退役，且一般商城的原價、交易及持久化仍正常。</summary>
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
    public async Task RetiredArrowOffersCannotChargeOrCreatePurchaseState()
    {
        await Start();
        await using var bot = await Host.InWorldAsync("RetiredArrows");
        var session = Session(bot); await Fund(session); await Ready(session);
        long before = Balance(session);
        foreach (var offer in ArrowShopCatalog.Offers)
            Assert.Equal(ErrorCodes.ShopNoConfig, (await Buy(bot, offer.CommodityId, 2)).Result);
        Assert.Equal(before, Balance(session));
        Assert.DoesNotContain(session.Items, item => ArrowIds.Contains(item.Entry));
        Assert.Empty(session.Shop!.Bought);
        Assert.Empty(session.Shop.Favorites);
        Assert.Equal(before, Service<RoleStore>().LoadProgress(session.Role!.Id).Currency[ArrowShopCatalog.CurrencyId]);
    }

    [Fact]
    public async Task OrdinaryCataloguePurchaseFailureKeepsPaymentGoodsAndCountersThenRetryCommits()
    {
        NativeShop(OtherShop, OtherClass, OtherShop);
        NativeCommodity(OriginalCommodity, OrdinaryItem, OtherClass);
        await Start();
        await using var bot = await Host.InWorldAsync("OrdinaryRollback");
        var session = Session(bot); long role = session.Role!.Id; await Fund(session);
        Assert.Equal(0, (await Buy(bot, OriginalCommodity, 3)).Result);
        long before = Balance(session);
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER ordinary_shop_fail BEFORE UPDATE OF shop ON role
            BEGIN SELECT RAISE(ABORT, '自造一般商城交易故障'); END
            """);
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() => Service<ShopService>().BuyAsync(session,
                new BuyCommodityReq { CommodityId = OriginalCommodity, Count = 2, Price = 1 })));
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER ordinary_shop_fail"); }
        Assert.Equal(before, Balance(session));
        Assert.Equal(3, Count(session, OrdinaryItem));
        Assert.Equal(3, SavedShop(role).Bought[OriginalCommodity].Count);
        Assert.Equal(before, Service<RoleStore>().LoadProgress(role).Currency[ArrowShopCatalog.CurrencyId]);
        var retry = await Buy(bot, OriginalCommodity, 2);
        Assert.Equal((0, 7, 5), (retry.Result, retry.Price, retry.BuyTimes));
        Assert.Equal(before - 14, Balance(session));
        Assert.Equal(5, Count(session, OrdinaryItem));
    }

    private async Task Start() => _host = await SyntheticHost.StartAsync(o => o.GameDataDir = _fixture);
    private Task Fund(GameSession session) => Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(session, [(ArrowShopCatalog.CurrencyId, 5000)]));
    private Task Ready(GameSession session) => Service<GameLoop>().InvokeAsync(async () => { await Service<CharacterService>().SetLevelAsync(session, 3, null); });
    private long Balance(GameSession session) => Service<CharacterService>().CountOf(session, ArrowShopCatalog.CurrencyId);
    private static long Count(GameSession session, int item) => session.Items.Where(i => i.Entry == item).Sum(i => (long)i.Stack);
    private static int[] ArrowIds => ArrowShopCatalog.Offers.Select(offer => offer.ItemId).ToArray();
    private ShopService.ShopState SavedShop(long role) => JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(role)!)!;
    private static async Task<BuyCommodityResp> Buy(BotClient bot, int id, int count = 1)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = id, Count = count, Price = 1 });
        return BuyCommodityResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(packet => packet.IsGame(ShopService.BuyCommodity), Wait)).Body);
    }
}
