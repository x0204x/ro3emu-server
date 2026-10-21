using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>326xx player market with two roles, durable escrow, mail delivery and 5033.</summary>
[Collection("Market server")]
public sealed partial class TradeTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-trade-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly MutableClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        }, _clock);
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
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(
            e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private List<InvItem> Items(BotClient bot) => _app.Services.GetRequiredService<ItemStore>().Load((long)bot.Self!.Uuid);

    private async Task Stage(BotClient bot, Action<MarketParticipant> edit)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var store = _app.Services.GetRequiredService<MarketStore>();
        long role = (long)bot.Self!.Uuid;
        int result = await loop.InvokeAsync(async () =>
        {
            var session = registry.All().Single(x => x.Role?.Id == role);
            return await store.RunAsync(session, tx => { MarketFixtureEconomy.Edit(tx, tx.Role(role), edit); return 0; });
        });
        Assert.Equal(0, result);
    }

    private static async Task<(string Id, int Template, MailItemList Goods)[]> Mail(BotClient bot, long role)
    {
        var answer = await bot.MicroAsync("MailService", "getUserMailList", new()
        { ["Uid"] = role.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        return Assert.IsType<object?[]>(answer["MailList"]).Select(raw =>
        {
            var row = Assert.IsType<Dictionary<object, object?>>(raw);
            var args = Assert.IsType<Dictionary<object, object?>>(row["Params"]);
            return (Assert.IsType<string>(row["MailId"]), Convert.ToInt32(args["cfg_id"]),
                MailItemList.Parser.ParseFrom(Assert.IsType<byte[]>(args["items"])));
        }).ToArray();
    }

    [ClientDataFact]
    public async Task OrdinaryListingPurchaseAndMailClaimPreserveInstanceState()
    {
        await using var seller = await InWorld("Seller");
        await using var buyer = await InWorld("Buyer");
        var initial = await Next(seller, TradeService.TradeSync, DBUserTradeInfoIncrement.Parser);
        Assert.True(initial.SyncAllMark);
        Assert.True(initial.LastRefreshTime > 0);
        Assert.Equal(6, initial.StallsNum);
        Assert.Equal(1000, initial.CreditPoint);

        await Gm("seller", "!lvl 30");
        await Gm("buyer", "!lvl 30");
        await Gm("seller", "!item 1254013 2");
        await Gm("seller", "!item 2 700");
        var source = Assert.Single(Items(seller), i => i.Entry == 1254013);
        long originalGuid = source.Guid;
        await Stage(seller, p =>
        {
            var item = p.Items.Single(i => i.Guid == originalGuid);
            item.Attrs = new() { [999] = 42 };
            item.StringAttrs = new() { [510] = "escrow-string" };
            item.Enchantments = new() { [7] = new ItemEnchantment { Attr = new() { [103] = 5 }, GetTime = 123 } };
        });

        await seller.SendGameAsync(TradeService.FindPrice, new FindItemPriceRequest { ItemId = "1254013" });
        var price = await Next(seller, TradeService.FindPrice, FindItemPriceResponse.Parser);
        Assert.Equal(0, price.Errno);
        Assert.True(price.Data.MinPrice < 600 && price.Data.MaxPrice > 600);
        Assert.Contains(600d, price.Data.PriceLadder);

        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = originalGuid, ItemPrice = 600, CurrencyType = 1, Quantity = 2, ItemName = "forged display name" });
        var listed = await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser);
        Assert.Equal(0, listed.Result);
        Assert.Equal(700, listed.Tax);
        var commodity = Assert.Single(listed.BoothInfos);
        var tradeUpdate = await Next(seller, TradeService.TradeSync, DBUserTradeInfoIncrement.Parser,
            state => state.TradeTimes.TryGetValue(1254013, out var count) && count.All == 2);
        Assert.Equal(2, tradeUpdate.TradeTimes[1254013].All);
        Assert.Equal(2, commodity.Quantity);
        // Commodity times are Unix seconds (the client compares them with GetSystemTime); the listing is not expired.
        long nowSeconds = _clock.GetUtcNow().ToUnixTimeSeconds();
        Assert.InRange(commodity.SaleTime, nowSeconds - 5, nowSeconds + 5);
        Assert.True(commodity.SuspensionTime > nowSeconds && commodity.SuspensionTime == commodity.TakeDownTime);
        Assert.Equal(1254013, ItemBaseInfo.Parser.ParseFrom(commodity.ItemBaseInfo).ItemEntry);
        Assert.DoesNotContain(Items(seller), i => i.Guid == originalGuid);

        await buyer.SendGameAsync(TradeService.QueryProduct, new SQueryProductByParamRequest
        { ItemId = "1254013", PageNo = 1, PageSize = 10 });
        var page = await Next(buyer, TradeService.QueryProduct, CQueryProductByParamWithPageResp.Parser);
        Assert.Equal(0, page.Errno);
        Assert.Equal(1, page.Data.TotalCount);
        Assert.Equal(commodity.SkuId, Assert.Single(page.Data.DataList).SkuId);
        Assert.Equal("Asc", page.Data.SortFiled["item_price"]);
        await buyer.SendGameAsync(TradeService.BuyTop, new TradeBuyItemsReq
        { ItemId = 1254013, SkuId = commodity.SkuId, Price = 600, TotalPrice = 600, TotalQuantity = 1 });
        Assert.Equal(ErrorCodes.TradeRequest,
            (await Next(buyer, TradeService.BuyTop, TradeBuyItemsResp.Parser)).Result);
        await Stage(buyer, p => p.Currency[1] = 0);
        await buyer.SendGameAsync(TradeService.BuyNormal, new TradeBuyNormalItemsReq
        { ItemId = 1254013, SpuId = commodity.SpuId, Price = 600, TotalPrice = 600, TotalQuantity = 1 });
        Assert.Equal(ErrorCodes.TradeCurrencyShort,
            (await Next(buyer, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser)).Result);
        await Stage(buyer, p => p.Currency[1] = 600);

        await buyer.SendGameAsync(TradeService.BuyNormal, new TradeBuyNormalItemsReq
        { ItemId = 1254013, SpuId = commodity.SpuId, Price = 600, TotalPrice = 600, TotalQuantity = 1 });
        Assert.Equal(0, (await Next(buyer, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser)).Result);
        Assert.DoesNotContain(Items(buyer), i => i.Entry == 1254013); // goods remain a mail attachment.
        var inbox = await Mail(buyer, (long)buyer.Self!.Uuid);
        var award = Assert.Single(inbox, m => m.Template == 1064);
        Assert.Equal(42, award.Goods.Items[0].ItemInfos[999].LongVal);
        Assert.Equal(1, award.Goods.Items[0].Stack);
        Assert.Contains((await Mail(seller, (long)seller.Self!.Uuid)), m => m.Template == 1063);

        await buyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { award.Id } });
        Assert.Equal(0, (await Next(buyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        var claimed = Assert.Single(Items(buyer), i => i.Entry == 1254013);
        Assert.Equal(42, claimed.Attrs![999]);
        Assert.Equal("escrow-string", claimed.StringAttrs![510]);
        Assert.Equal(5, claimed.Enchantments[7].Attr[103]);
        Assert.NotEqual(originalGuid, claimed.Guid); // split-off bag copy gets a new GUID (inferred).
        await buyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { award.Id } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(buyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await seller.SendGameAsync(TradeService.OwnBooth, new QueryPlayerBoothInfoRequest());
        Assert.Equal(0, (await Next(seller, TradeService.OwnBooth, CQueryPlayerBoothInfoResp.Parser)).Data.TotalCount);
        Assert.Equal(1, (await Next(seller, 32630, TradeCommonNtf.Parser, n => n.ExpireNtf != null)).ExpireNtf.ExpiredCount);
        var expired = Assert.Single(await Mail(seller, (long)seller.Self!.Uuid), m => m.Template == 1067);
        Assert.Equal(1, Assert.Single(expired.Goods.Items).Stack);
        await seller.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { expired.Id } });
        Assert.Equal(0, (await Next(seller, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(1, Assert.Single(Items(seller), i => i.Entry == 1254013).Stack);
    }

    [ClientDataFact]
    public async Task SpecificTopBuyRefusalsAndListingSurviveRelog()
    {
        var seller = await InWorld("Seller");
        await using var buyer = await InWorld("Buyer");
        await Gm("seller", "!lvl 30"); await Gm("buyer", "!lvl 30");
        await Gm("seller", "!item 1450100 1"); await Gm("seller", "!item 2 50000");
        await Gm("buyer", "!item 1 2000000");
        long guid = Assert.Single(Items(seller), i => i.Entry == 1450100).Guid;

        await Stage(seller, p => p.Items.Single(i => i.Guid == guid).IsLocked = true);
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 2000000, CurrencyType = 1, Quantity = 1 });
        Assert.Equal(ErrorCodes.ItemLocked, (await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser)).Result);
        await Stage(seller, p => { var item = p.Items.Single(i => i.Guid == guid); item.IsLocked = false; item.BindType = 1; });
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 2000000, CurrencyType = 1, Quantity = 1 });
        Assert.Equal(ErrorCodes.TradeBound, (await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser)).Result);
        await Stage(seller, p => p.Items.Single(i => i.Guid == guid).BindType = 0);

        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 2000000, CurrencyType = 1, Quantity = 1 });
        var listed = await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser);
        Assert.Equal(0, listed.Result);
        string sku = Assert.Single(listed.BoothInfos).SkuId;

        await buyer.SendGameAsync(TradeService.BuyTop, new TradeBuyItemsReq
        { ItemId = 1450100, SkuId = sku, Price = 1999999, TotalPrice = 1999999, TotalQuantity = 1 });
        Assert.NotEqual(0, (await Next(buyer, TradeService.BuyTop, TradeBuyItemsResp.Parser)).Result);
        await buyer.SendGameAsync(TradeService.PutOff, new TradePutOffItemReq { ItemId = 1450100, SkuId = sku, Quantity = 1 });
        Assert.NotEqual(0, (await Next(buyer, TradeService.PutOff, TradePutOffItemResp.Parser)).Result);

        await seller.DisposeAsync();
        await using (var sellerAgain = await InWorld("Seller", create: false))
        {
            await sellerAgain.SendGameAsync(TradeService.OwnBooth, new QueryPlayerBoothInfoRequest());
            var booth = await Next(sellerAgain, TradeService.OwnBooth, CQueryPlayerBoothInfoResp.Parser);
            Assert.Equal(sku, Assert.Single(booth.Data.DataList).SkuId);
        }
        // The top item starts in publicity for TradeItemHideTime._kHideTimeMin=1800 seconds.
        await buyer.SendGameAsync(TradeService.BuyTop, new TradeBuyItemsReq
        { ItemId = 1450100, SkuId = sku, Price = 2000000, TotalPrice = 2000000, TotalQuantity = 1 });
        Assert.NotEqual(0, (await Next(buyer, TradeService.BuyTop, TradeBuyItemsResp.Parser)).Result);
        _clock.Advance(TimeSpan.FromSeconds(1801));
        await buyer.SendGameAsync(TradeService.BuyTop, new TradeBuyItemsReq
        { ItemId = 1450100, SkuId = sku, Price = 2000000, TotalPrice = 2000000, TotalQuantity = 1 });
        Assert.Equal(0, (await Next(buyer, TradeService.BuyTop, TradeBuyItemsResp.Parser)).Result);
        var delivery = Assert.Single(await Mail(buyer, (long)buyer.Self!.Uuid), m => m.Template == 1064);
        Assert.Equal(1450100, delivery.Goods.Items[0].ItemEntry);
        Assert.DoesNotContain(Items(buyer), i => i.Entry == 1450100);
        await buyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { delivery.Id } });
        Assert.Equal(0, (await Next(buyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Contains(Items(buyer), i => i.Entry == 1450100);
    }

    [ClientDataFact]
    public async Task PublicityPreordersSettleOnceInReservationOrderAndRefundLoser()
    {
        await using var seller = await InWorld("Seller");
        await using var first = await InWorld("First");
        await using var second = await InWorld("Second");
        await using var locked = await InWorld("Locked");
        foreach (string name in new[] { "seller", "first", "second" }) await Gm(name, "!lvl 30");
        await Gm("seller", "!item 1450100 1"); await Gm("seller", "!item 2 50000");
        await Gm("first", "!item 1 2000000"); await Gm("second", "!item 1 2000000");
        long guid = Assert.Single(Items(seller), i => i.Entry == 1450100).Guid;
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 2000000, CurrencyType = 1, Quantity = 1 });
        var listed = await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser);
        Assert.Equal(0, listed.Result);
        var listing = Assert.Single(listed.BoothInfos);
        await Gm("locked", "!item 1 2000000");
        await locked.SendGameAsync(TradeService.Preorder, new CPreTradeTopRequest
        {
            SkuId = listing.SkuId, SpuId = listing.SpuId, ItemId = 1450100,
            Price = 2000000, TotalPrice = 2000000, TotalQuantity = 1,
        });
        Assert.Equal(ErrorCodes.FunctionLocked,
            (await Next(locked, TradeService.Preorder, CPreTradeTopResponse.Parser)).Result);
        Assert.Empty(await Mail(locked, (long)locked.Self!.Uuid));
        await Gm("locked", "!lvl 30");
        await locked.SendGameAsync(TradeService.Preorder, new CPreTradeTopRequest
        {
            SkuId = listing.SkuId, SpuId = listing.SpuId, ItemId = 1450100,
            Price = 2000000, TotalPrice = 2000000, TotalQuantity = 1,
        });
        Assert.Equal(0, (await Next(locked, TradeService.Preorder, CPreTradeTopResponse.Parser)).Result);
        await locked.SendGameAsync(TradeService.QueryPreorders, new CPreTradeTopQueryRequest { PageNo = 1, PageSize = 10 });
        var cancelId = Assert.Single((await Next(locked, TradeService.QueryPreorders,
            CPreTradeTopQueryResponse.Parser)).Data.PreTradeOrder).PreTradeId;
        await locked.SendGameAsync(TradeService.RefundPreorder, new CRefundPreTradeTopRequest
        { PreTradeOrderIds = { cancelId } });
        Assert.Equal(0, (await Next(locked, TradeService.RefundPreorder, CRefundPreTradeTopResponse.Parser)).Errno);
        await locked.SendGameAsync(TradeService.QueryPreorders, new CPreTradeTopQueryRequest { PageNo = 1, PageSize = 10 });
        Assert.Equal(0, (await Next(locked, TradeService.QueryPreorders, CPreTradeTopQueryResponse.Parser)).Data.TotalCount);
        Assert.Equal(2000000, Assert.Single(Assert.Single(await Mail(locked, (long)locked.Self!.Uuid),
            m => m.Template == 1065).Goods.Items).Stack);

        foreach (var bot in new[] { first, second })
        {
            await bot.SendGameAsync(TradeService.Preorder, new CPreTradeTopRequest
            {
                SkuId = listing.SkuId, SpuId = listing.SpuId, ItemId = 1450100,
                Price = 2000000, TotalPrice = 2000000, TotalQuantity = 1,
            });
            Assert.Equal(0, (await Next(bot, TradeService.Preorder, CPreTradeTopResponse.Parser)).Result);
            Assert.Empty(await Mail(bot, (long)bot.Self!.Uuid));
            // 32622: "You have joined the Lineup... In ${1} seconds" counts down to the end of publicity.
            var lineup = await Next(bot, TradeService.InQueue, skuQueueNtf.Parser);
            Assert.Equal((1450100, 2000000, 1, listing.SkuId), (lineup.ItemId, lineup.Price, lineup.Quantity, lineup.SkuId));
            Assert.InRange(lineup.SaleTime - _clock.GetUtcNow().ToUnixTimeSeconds(), 1, 1801); // _kHideTimeMin 1800 s, rounded up
        }
        Assert.Single(locked.Inbox.Snapshot(), e => e.IsGame(TradeService.InQueue)); // the refused attempt joined nothing
        await first.SendGameAsync(TradeService.QueryPreorders, new CPreTradeTopQueryRequest { PageNo = 1, PageSize = 10 });
        var before = await Next(first, TradeService.QueryPreorders, CPreTradeTopQueryResponse.Parser);
        Assert.Equal(PreTradeStatusEnums.Init, Assert.Single(before.Data.PreTradeOrder).Status);
        string firstOrderId = Assert.Single(before.Data.PreTradeOrder).PreTradeId;
        await first.SendGameAsync(TradeService.RefundPreorder, new CRefundPreTradeTopRequest
        { PreTradeOrderIds = { firstOrderId, firstOrderId } });
        Assert.NotEqual(0, (await Next(first, TradeService.RefundPreorder, CRefundPreTradeTopResponse.Parser)).Errno);
        Assert.Empty(await Mail(first, (long)first.Self!.Uuid));

        _clock.Advance(TimeSpan.FromSeconds(1801));
        await seller.SendGameAsync(TradeService.OwnBooth, new QueryPlayerBoothInfoRequest());
        Assert.Equal(0, (await Next(seller, TradeService.OwnBooth, CQueryPlayerBoothInfoResp.Parser)).Data.TotalCount);
        var winner = Assert.Single(await Mail(first, (long)first.Self!.Uuid), m => m.Template == 1064);
        Assert.Equal(1450100, Assert.Single(winner.Goods.Items).ItemEntry);
        var refund = Assert.Single(await Mail(second, (long)second.Self!.Uuid), m => m.Template == 1065);
        Assert.Equal(2000000, Assert.Single(refund.Goods.Items).Stack);
        await second.SendGameAsync(TradeService.QueryPreorders, new CPreTradeTopQueryRequest { PageNo = 1, PageSize = 10 });
        var after = await Next(second, TradeService.QueryPreorders, CPreTradeTopQueryResponse.Parser);
        var lost = Assert.Single(after.Data.PreTradeOrder);
        Assert.Equal(PreTradeStatusEnums.NotSelected, lost.Status);
        await second.SendGameAsync(TradeService.RefundPreorder, new CRefundPreTradeTopRequest
        { PreTradeOrderIds = { lost.PreTradeId } });
        Assert.NotEqual(0, (await Next(second, TradeService.RefundPreorder, CRefundPreTradeTopResponse.Parser)).Errno);
        await second.SendGameAsync(TradeService.RefundPreorder, new CRefundPreTradeTopRequest
        { IsReward = true, PreTradeOrderIds = { lost.PreTradeId } });
        Assert.Equal(0, (await Next(second, TradeService.RefundPreorder, CRefundPreTradeTopResponse.Parser)).Errno);
        await second.SendGameAsync(TradeService.QueryPreorders, new CPreTradeTopQueryRequest { PageNo = 1, PageSize = 10 });
        Assert.Equal(0, (await Next(second, TradeService.QueryPreorders, CPreTradeTopQueryResponse.Parser)).Data.TotalCount);
        Assert.Single(await Mail(second, (long)second.Self!.Uuid), m => m.Template == 1065);
    }

    [ClientDataFact]
    public async Task TwoBuyersCannotConsumeTheSameEscrow()
    {
        await using var seller = await InWorld("Seller");
        await using var first = await InWorld("First");
        await using var second = await InWorld("Second");
        foreach (string name in new[] { "seller", "first", "second" }) await Gm(name, "!lvl 30");
        await Gm("seller", "!item 1254013 1"); await Gm("seller", "!item 2 700");
        long guid = Assert.Single(Items(seller), i => i.Entry == 1254013).Guid;
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = guid, ItemPrice = 600, CurrencyType = 1, Quantity = 1 });
        var listed = await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser);
        Assert.Equal(0, listed.Result);
        string spu = Assert.Single(listed.BoothInfos).SpuId;
        var request = new TradeBuyNormalItemsReq
        { ItemId = 1254013, SpuId = spu, Price = 600, TotalPrice = 600, TotalQuantity = 1 };
        await Task.WhenAll(first.SendGameAsync(TradeService.BuyNormal, request),
            second.SendGameAsync(TradeService.BuyNormal, request));
        var outcomes = await Task.WhenAll(
            Next(first, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser),
            Next(second, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser));
        Assert.Single(outcomes, r => r.Result == 0);
        Assert.Single(outcomes, r => r.Result != 0);
        Assert.Equal(1, (await Mail(first, (long)first.Self!.Uuid)).Count(m => m.Template == 1064)
            + (await Mail(second, (long)second.Self!.Uuid)).Count(m => m.Template == 1064));
        await seller.SendGameAsync(TradeService.OwnBooth, new QueryPlayerBoothInfoRequest());
        Assert.Equal(0, (await Next(seller, TradeService.OwnBooth, CQueryPlayerBoothInfoResp.Parser)).Data.TotalCount);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }


    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task MaintenanceRunsOnlyAfterAMarketCommitOrWhenAListingIsDue()
    {
        // Stage G: every trade message and the 1 s stall maintenance ran a full market transaction here.
        var trade = _app.Services.GetRequiredService<TradeService>();
        var market = _app.Services.GetRequiredService<MarketStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await loop.InvokeAsync(() => trade.TickMaintenanceAsync());
        long passes = trade.MaintenancePasses;
        await loop.InvokeAsync(() => trade.TickMaintenanceAsync());
        await loop.InvokeAsync(() => trade.TickMaintenanceAsync());
        Assert.Equal(passes, trade.MaintenancePasses); // nothing committed, nothing due
        await loop.InvokeAsync(() => { market.CommittedForTest(); return Task.CompletedTask; });
        await loop.InvokeAsync(() => trade.TickMaintenanceAsync());
        Assert.Equal(passes + 1, trade.MaintenancePasses); // a market commit since the last pass
    }
}
