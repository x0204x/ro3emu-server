using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DragonIslandLevelConfig (data/config/DragonIslandLevelConfig.json).</summary>
[ConfigTable("DragonIslandLevelConfig")]
public readonly partial record struct DragonIslandLevelConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iCompensate")] public partial long Compensate { get; }
    [ConfigField("_iPointLimit")] public partial long PointLimit { get; }
    [ConfigField("_iSettleCurrencyLimit")] public partial long SettleCurrencyLimit { get; }
    [ConfigField("_iUpgradesClear")] public partial long UpgradesClear { get; }
}
