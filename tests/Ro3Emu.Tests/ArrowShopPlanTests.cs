using Ro3Emu.ClientHooks;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed class ArrowShopPlanTests
{
    [Fact]
    public void ANewCanonicalOfferWithADifferentIdTakesPrecedenceOverTheSupplement()
    {
        var offer = ArrowShopCatalog.Offers[0];
        var existing = new[] { new ArrowShopPlan.Commodity(7654321, ArrowShopCatalog.ClassId, offer.ItemId) };

        var missing = ArrowShopPlan.MissingOffers(existing);

        Assert.DoesNotContain(missing, row => row.ItemId == offer.ItemId);
        Assert.Equal(ArrowShopCatalog.Offers.Count - 1, missing.Count);
    }

    [Fact]
    public void AnotherShopsOfferDoesNotHideTheMissingZenyShopOffer()
    {
        var offer = ArrowShopCatalog.Offers[0];
        var existing = new[] { new ArrowShopPlan.Commodity(7654321, 8765432, offer.ItemId) };

        Assert.Contains(ArrowShopPlan.MissingOffers(existing), row => row.ItemId == offer.ItemId);
    }

    [Fact]
    public void ReloadingAnAlreadySupplementedCatalogueProducesNoDuplicateOffers()
    {
        var first = ArrowShopPlan.MissingOffers([]);
        var after = first.Select(row => new ArrowShopPlan.Commodity(row.CommodityId, ArrowShopCatalog.ClassId, row.ItemId)).ToArray();

        Assert.Empty(ArrowShopPlan.MissingOffers(after));
        Assert.False(ArrowShopPlan.NeedsShop([
            new ArrowShopPlan.Shop(7654321, ArrowShopCatalog.ShopId, ArrowShopCatalog.ClassId, ArrowShopCatalog.ShopType)]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOccupiedSupplementIdRefusesTheWholePlanBeforeAnyWrite(bool anotherItem)
    {
        var last = ArrowShopCatalog.Offers[^1];
        var existing = new[] { new ArrowShopPlan.Commodity(last.CommodityId,
            anotherItem ? ArrowShopCatalog.ClassId : 8765432, anotherItem ? 7654321 : last.ItemId) };

        Assert.Throws<InvalidOperationException>(() => ArrowShopPlan.MissingOffers(existing));
    }

    [Fact]
    public void AnExistingClassWithDifferentShopOwnershipIsNeverReplaced()
    {
        Assert.Throws<InvalidOperationException>(() => ArrowShopPlan.NeedsShop([
            new ArrowShopPlan.Shop(7654321, 8765432, ArrowShopCatalog.ClassId, ArrowShopCatalog.ShopType)]));
        Assert.Throws<InvalidOperationException>(() => ArrowShopPlan.NeedsShop([
            new ArrowShopPlan.Shop(ArrowShopCatalog.ShopConfigId, 7654321, 8765432, ArrowShopCatalog.ShopType)]));
    }

    [Fact]
    public void UnrelatedExistingRowsRemainUntouchedAndDoNotSuppressTheNewEntry()
    {
        var existing = new[] { new ArrowShopPlan.Shop(123456, 123456, 123456, 9) };

        Assert.True(ArrowShopPlan.NeedsShop(existing));
        Assert.Equal(123456, existing[0].ClassId);
    }
}
