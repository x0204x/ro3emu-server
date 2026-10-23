using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TradeConfigTax (data/config/TradeConfigTax.json).</summary>
[ConfigTable("TradeConfigTax")]
public readonly partial record struct TradeConfigTax(JsonElement Row)
{
    [ConfigField("_iAmountBooth")] public partial long AmountBooth { get; }
    [ConfigField("_iTaxBooth")] public partial long TaxBooth { get; }
}
