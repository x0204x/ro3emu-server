using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ShopCommodityConfig (data/config/ShopCommodityConfig.json).</summary>
[ConfigTable("ShopCommodityConfig")]
public readonly partial record struct ShopCommodityConfig(JsonElement Row)
{
    [ConfigField("_iAppearanceAwardId")] public partial long AppearanceAwardId { get; }
    [ConfigField("_iAppearanceId")] public partial long AppearanceId { get; }
    [ConfigField("_iClassID")] public partial long ClassId { get; }
    [ConfigField("_iDiscount")] public partial long Discount { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsBuy")] public partial long IsBuy { get; }
    [ConfigField("_iIsLock")] public partial long IsLock { get; }
    [ConfigField("_iItemId")] public partial long ItemId { get; }
    [ConfigField("_iItemPerMount")] public partial long ItemPerMount { get; }
    [ConfigField("_iMerchantDiscount")] public partial long MerchantDiscount { get; }
    [ConfigField("_iPurchaseNum")] public partial long PurchaseNum { get; }
    [ConfigField("_iPurchaseTimesLimit")] public partial long PurchaseTimesLimit { get; }
    [ConfigField("_kAppearanceParts")] public partial long[] AppearanceParts { get; }
    [ConfigField("_kItemPerPrice")] public partial long[] ItemPerPrice { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_iGlobalPurchase")] public partial long GlobalPurchase { get; }
}
