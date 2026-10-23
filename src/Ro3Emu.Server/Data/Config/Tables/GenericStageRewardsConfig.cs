using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GenericStageRewardsConfig (data/config/GenericStageRewardsConfig.json).</summary>
[ConfigTable("GenericStageRewardsConfig")]
public readonly partial record struct GenericStageRewardsConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iItem")] public partial long Item { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iScore")] public partial long Score { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kMultiverseArray")] public partial JsonElement? MultiverseArray { get; }
}
