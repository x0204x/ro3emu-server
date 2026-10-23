using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeJobConfig (data/config/LifeJobConfig.json).</summary>
[ConfigTable("LifeJobConfig")]
public readonly partial record struct LifeJobConfig(JsonElement Row)
{
    [ConfigField("_iChangeConsume")] public partial long[][] ChangeConsume { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iDescription")] public partial long[] Description { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
