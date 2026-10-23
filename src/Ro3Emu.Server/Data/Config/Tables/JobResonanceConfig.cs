using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobResonanceConfig (data/config/JobResonanceConfig.json).</summary>
[ConfigTable("JobResonanceConfig")]
public readonly partial record struct JobResonanceConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iJobResonanceId")] public partial long JobResonanceId { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_kAttribute")] public partial long[][] Attribute { get; }
}
