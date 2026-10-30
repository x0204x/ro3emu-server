using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed partial class ArrowShopTests
{
    [Fact]
    public async Task OrdinaryOfferKeepsItsOriginalPriceAndPersistsAcrossRestart()
    {
        NativeShop(OtherShop, OtherClass, OtherShop);
        NativeCommodity(OriginalCommodity, OrdinaryItem, OtherClass);
        await Start();
        long role, before;
        await using (var bot = await Host.InWorldAsync("OrdinaryOriginal"))
        {
            var session = Session(bot); role = session.Role!.Id; await Fund(session); before = Balance(session);
            string original = Service<GameData>().Row("ShopCommodityConfig", OriginalCommodity)!.Value.GetRawText();
            var reply = await Buy(bot, OriginalCommodity, 2);
            Assert.Equal((0, 7, 2), (reply.Result, reply.Price, reply.BuyTimes));
            Assert.Equal(before - 14, Balance(session));
            Assert.Equal(2, Count(session, OrdinaryItem));
            Assert.Equal(original, Service<GameData>().Row("ShopCommodityConfig", OriginalCommodity)!.Value.GetRawText());
            Assert.Null(Service<GameData>().Row("ShopConfig", ArrowShopCatalog.ShopConfigId));
            await bot.SendGameAsync(ShopService.CommodityCollect, new CommodityCollectReq
            { CommodityId = OriginalCommodity, OpType = CollectOperateType.Add });
            Assert.Equal(0, CommodityCollectResp.Parser.ParseFrom(
                (await bot.Inbox.WaitAsync(packet => packet.IsGame(ShopService.CommodityCollect), Wait)).Body).Result);
        }
        await Host.RestartAsync();
        await using var again = await Host.InWorldAsync("OrdinaryOriginal", create: false);
        var restored = Session(again);
        Assert.Equal(before - 14, Balance(restored));
        Assert.Equal(2, Count(restored, OrdinaryItem));
        Assert.Equal(2, SavedShop(role).Bought[OriginalCommodity].Count);
        Assert.Contains(OriginalCommodity, restored.Shop!.Favorites);
    }

    [Fact]
    public async Task RetiredPurchaseHistoryStaysStoredButOnlyCurrentCatalogueIsSentToClient()
    {
        NativeShop(OtherShop, OtherClass, OtherShop);
        NativeCommodity(OriginalCommodity, OrdinaryItem, OtherClass);
        await Start();
        long role;
        await using (var bot = await Host.InWorldAsync("LegacyArrow"))
        {
            role = Session(bot).Role!.Id;
            var history = new ShopService.ShopState();
            history.Bought[First.CommodityId] = new ShopService.Counter { Count = 2 };
            history.Bought[OriginalCommodity] = new ShopService.Counter { Count = 3 };
            history.Favorites.UnionWith([First.CommodityId, OriginalCommodity]);
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Service<RoleStore>().SaveShop(role, JsonSerializer.SerializeToUtf8Bytes(history));
                return Task.CompletedTask;
            });
        }
        await Host.RestartAsync();
        await using var again = await Host.InWorldAsync("LegacyArrow", create: false);
        var wire = DBShopInfoIncrement.Parser.ParseFrom(
            (await again.Inbox.WaitAsync(packet => packet.IsGame(ShopService.ShopSync), Wait)).Body);
        Assert.DoesNotContain(First.CommodityId, wire.CommodityBuyTimes.Keys);
        Assert.DoesNotContain(First.CommodityId, wire.CollectCommodities.Keys);
        Assert.Contains(OriginalCommodity, wire.CommodityBuyTimes.Keys);
        Assert.Contains(OriginalCommodity, wire.CollectCommodities.Keys);
        Assert.Equal(2, SavedShop(role).Bought[First.CommodityId].Count);
        Assert.Contains(First.CommodityId, SavedShop(role).Favorites);
    }
}
