using Ro3Emu.Server.Data;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed partial class ArrowShopTests
{
    [Fact]
    public async Task OriginalSameIdAndItemUsesOriginalPriceWithoutChangingOriginalData()
    {
        NativeCommodity(First.CommodityId, First.ItemId, ArrowShopCatalog.ClassId);
        await Start();
        await using var bot = await Host.InWorldAsync("ArrowOriginal");
        var s = Session(bot); await Fund(s); await Ready(s); long before = Balance(s);
        string row = Service<GameData>().Row("ShopCommodityConfig", First.CommodityId)!.Value.GetRawText();
        var reply = await Buy(bot, First.CommodityId);
        Assert.Equal((0, 7, 1), (reply.Result, reply.Price, reply.BuyTimes));
        Assert.Equal(before - 7, Balance(s)); Assert.Equal(1, Count(s, First.ItemId));
        Assert.Equal(row, Service<GameData>().Row("ShopCommodityConfig", First.CommodityId)!.Value.GetRawText());
        Assert.Null(Service<GameData>().Row("ShopConfig", ArrowShopCatalog.ShopConfigId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingOfferSuppressesSupplementOnlyInItsOwnZenyClass(bool sameClass)
    {
        int classId = sameClass ? ArrowShopCatalog.ClassId : OtherClass;
        NativeShop(OtherShop, classId, sameClass ? ArrowShopCatalog.ShopId : OtherShop);
        NativeCommodity(OriginalCommodity, First.ItemId, classId);
        await Start();
        await using var bot = await Host.InWorldAsync("ArrowCanonical");
        var s = Session(bot); await Fund(s); await Ready(s); long before = Balance(s);
        var supplement = await Buy(bot, First.CommodityId);
        Assert.Equal(sameClass ? Ro3Emu.Server.Game.ErrorCodes.ShopNoConfig : 0, supplement.Result);
        Assert.Equal(before - (sameClass ? 0 : 100), Balance(s));
        Assert.Equal(sameClass ? 0 : 1, Count(s, First.ItemId));
        var official = await Buy(bot, OriginalCommodity);
        Assert.Equal((0, 7), (official.Result, official.Price));
        Assert.Equal(sameClass ? 1 : 2, Count(s, First.ItemId));
    }

    [Theory]
    [InlineData("commodity-item")]
    [InlineData("commodity-class")]
    [InlineData("shop-id")]
    [InlineData("shop-class")]
    [InlineData("wrong-item-type")]
    public async Task IdentityCollisionOrWrongItemTypeRefusesWithoutChargeOrStateMutation(string collision)
    {
        switch (collision)
        {
            case "commodity-item": NativeCommodity(First.CommodityId, 1000000, ArrowShopCatalog.ClassId); break;
            case "commodity-class": NativeCommodity(First.CommodityId, First.ItemId, OtherClass); break;
            case "shop-id": NativeShop(ArrowShopCatalog.ShopConfigId, OtherClass, OtherShop); break;
            case "shop-class": NativeShop(OtherShop, ArrowShopCatalog.ClassId, OtherShop); break;
            case "wrong-item-type":
                Row(_fixture, "ItemConfig", First.ItemId,
                new { _iID = First.ItemId, _iType = 2, _iSubType = 205, _iBagTab = 1, _iStackLimit = 7 }); break;
            default: throw new ArgumentException("未知的碰撞測試");
        }
        await Start();
        await using var bot = await Host.InWorldAsync("ArrowCollision");
        var s = Session(bot); await Fund(s); await Ready(s); long before = Balance(s);
        Assert.Equal(Ro3Emu.Server.Game.ErrorCodes.ShopNoConfig, (await Buy(bot, First.CommodityId)).Result);
        Assert.Equal(before, Balance(s)); Assert.Equal(0, Count(s, First.ItemId));
        Assert.False(s.Shop!.Bought.ContainsKey(First.CommodityId));
        Assert.DoesNotContain(First.CommodityId, s.Shop.Favorites);
    }
}
