using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EatChickenLevelConfig (data/config/EatChickenLevelConfig.json).</summary>
[ConfigTable("EatChickenLevelConfig")]
public readonly partial record struct EatChickenLevelConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iCompensate")] public partial long Compensate { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPointLimit")] public partial long PointLimit { get; }
    [ConfigField("_iUpgradesClear")] public partial long UpgradesClear { get; }
}
