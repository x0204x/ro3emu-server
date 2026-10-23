using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CardReturnCostConfig (data/config/CardReturnCostConfig.json).</summary>
[ConfigTable("CardReturnCostConfig")]
public readonly partial record struct CardReturnCostConfig(JsonElement Row)
{
    [ConfigField("_iCost")] public partial long[][] Cost { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
}
