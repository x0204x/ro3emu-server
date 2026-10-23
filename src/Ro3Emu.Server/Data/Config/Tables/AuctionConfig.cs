using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AuctionConfig (data/config/AuctionConfig.json).</summary>
[ConfigTable("AuctionConfig")]
public readonly partial record struct AuctionConfig(JsonElement Row)
{
    [ConfigField("_iAuction")] public partial long Auction { get; }
    [ConfigField("_iAuctionPrice")] public partial long AuctionPrice { get; }
    [ConfigField("_iBaseRaisePrice")] public partial long BaseRaisePrice { get; }
    [ConfigField("_iCurrency")] public partial long Currency { get; }
    [ConfigField("_iDungeonPrice")] public partial long DungeonPrice { get; }
    [ConfigField("_iDungeonPriceMax")] public partial long DungeonPriceMax { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSellPrice")] public partial long SellPrice { get; }
    [ConfigField("_iTimeLast")] public partial long TimeLast { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_iTypeAttr")] public partial long TypeAttr { get; }
    [ConfigField("_iWorldAuctionCeiling")] public partial long WorldAuctionCeiling { get; }
}
