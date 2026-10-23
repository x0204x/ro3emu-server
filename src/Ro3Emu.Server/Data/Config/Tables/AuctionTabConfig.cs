using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AuctionTabConfig (data/config/AuctionTabConfig.json).</summary>
[ConfigTable("AuctionTabConfig")]
public readonly partial record struct AuctionTabConfig(JsonElement Row)
{
    [ConfigField("_iAuctionType")] public partial long AuctionType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kDailyActivity")] public partial long[] DailyActivity { get; }
}
