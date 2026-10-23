using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeJobLevelConfig (data/config/LifeJobLevelConfig.json).</summary>
[ConfigTable("LifeJobLevelConfig")]
public readonly partial record struct LifeJobLevelConfig(JsonElement Row)
{
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iJobId")] public partial long JobId { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iNextNeedExp")] public partial long NextNeedExp { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
