using Ro3Emu.ClientHooks;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed class ArrowShopPlanTests
{
    [Fact]
    public void RetiredSupplementNeverCreatesAShopOrOffers()
    {
        Assert.False(ArrowShopCatalog.Enabled);
        Assert.False(ArrowShopPlan.NeedsShop([]));
        Assert.Empty(ArrowShopPlan.MissingOffers([]));
    }

    [Fact]
    public void RetiredPlannerDoesNotRewriteOrRejectTheExistingCatalogue()
    {
        var shops = new[] { new ArrowShopPlan.Shop(ArrowShopCatalog.ShopConfigId, 7654321, 8765432, 9) };
        var offers = new[] { new ArrowShopPlan.Commodity(ArrowShopCatalog.Offers[0].CommodityId, 8765432, 7654321) };
        Assert.False(ArrowShopPlan.NeedsShop(shops));
        Assert.Empty(ArrowShopPlan.MissingOffers(offers));
        Assert.Equal(8765432, shops[0].ClassId);
        Assert.Equal(7654321, offers[0].ItemId);
    }
}
