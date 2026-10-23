using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CardUpgradeConfig (data/config/CardUpgradeConfig.json).</summary>
[ConfigTable("CardUpgradeConfig")]
public readonly partial record struct CardUpgradeConfig(JsonElement Row)
{
    [ConfigField("_iCost")] public partial long[][] Cost { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iParts")] public partial long Parts { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
}
