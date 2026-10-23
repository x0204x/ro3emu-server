using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TradeConfig (data/config/TradeConfig.json).</summary>
[ConfigTable("TradeConfig")]
public readonly partial record struct TradeConfig(JsonElement Row)
{
    [ConfigField("_iBasePrice")] public partial long BasePrice { get; }
    [ConfigField("_iBuyLimit")] public partial long BuyLimit { get; }
    [ConfigField("_iMoney")] public partial long Money { get; }
    [ConfigField("_iTrade")] public partial long Trade { get; }
    [ConfigField("_iTradeType")] public partial long TradeType { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kGameSeason")] public partial long[] GameSeason { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kSpuName")] public partial string SpuName { get; }
    [ConfigField("_kStableRange")] public partial long[] StableRange { get; }
}
