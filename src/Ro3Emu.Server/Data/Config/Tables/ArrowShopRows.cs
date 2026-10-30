using System.Text.Json;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Data.Config;

/// <summary>本地補售的完整原生形狀（inferred）；只建立 host 自有列，不寫入 GameData 或原始配置。</summary>
internal static class ArrowShopRows
{
    internal static ShopConfig Shop() => new(JsonSerializer.SerializeToElement(new
    {
        _iID = ArrowShopCatalog.ShopConfigId, _iShopId = ArrowShopCatalog.ShopId,
        _iClassID = ArrowShopCatalog.ClassId, _iClassName = new[] { 0 }, _iClassType = 0,
        _iFunctionId = ArrowShopCatalog.FunctionId, _iIndependentTab = 0, _iNpcId = 0,
        _iShopName = new[] { ArrowShopCatalog.ShopNameSid }, _iShopType = ArrowShopCatalog.ShopType,
        _kCurrencyDisplay = new[] { ArrowShopCatalog.CurrencyId }, _kEndTime = Array.Empty<int>(),
        _kGroup = Array.Empty<int[]>(), _kIsBindGive = Array.Empty<int>(), _kMultiverseArray = new[] { 0 },
        _kRefreshExpend = Array.Empty<int[]>(), _kRefreshTime = "", _kRefreshType = Array.Empty<int>(),
        _kStartTime = Array.Empty<int>()
    }));

    internal static ShopCommodityConfig Commodity(ArrowShopOffer offer) => new(JsonSerializer.SerializeToElement(new
    {
        _iID = offer.CommodityId, _iClassID = ArrowShopCatalog.ClassId, _iItemId = offer.ItemId,
        _iAppranceId = 0, _iAppearanceId = 0, _iAppranceAwardId = 0, _iAppearanceAwardId = 0,
        _kAppearanceParts = Array.Empty<int>(), _kItemPic = "", _kItemPicFemale = "", _iSortId = offer.SortId,
        _iRecommendID = 0, _kBooth = Array.Empty<int[]>(), _kReBanner = "", _iItemPerMount = 1,
        _iIsShowCount = 1, _kItemPerPrice = new[] { ArrowShopCatalog.CurrencyId, ArrowShopCatalog.UnitPrice },
        _iDiscount = 0, _kRecommended = "", _kRecommendedText = new[] { 0 }, _iIsLock = 0, _iIsBuy = 0,
        _iSiegeSettlementLabel = 0, _kTimeLimit = Array.Empty<int>(), _iGlobalPurchase = 0,
        _iPurchaseNum = 0, _iPurchaseTimesLimit = 0, _kIsBindGive = new[] { 0 }, _iMerchantDiscount = 0,
        _iDescribe = new[] { 0 }, _kStartTime = Array.Empty<int>(), _kEndTime = Array.Empty<int>(),
        _kBackground = "", _iTrack = new[] { new[] { "" } }, _kAnimation = "", _iGroup = 0,
        _iWeight = 0, _kMultiverseArray = new[] { 0 }
    }));
}
