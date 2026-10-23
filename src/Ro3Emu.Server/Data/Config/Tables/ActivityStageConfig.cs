using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ActivityStageConfig (data/config/ActivityStageConfig.json).</summary>
[ConfigTable("ActivityStageConfig")]
public readonly partial record struct ActivityStageConfig(JsonElement Row)
{
    [ConfigField("_iActivityID")] public partial long ActivityId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kValue")] public partial long[][] Value { get; }
}
