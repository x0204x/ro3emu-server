using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobResonanceActiveConfig (data/config/JobResonanceActiveConfig.json).</summary>
[ConfigTable("JobResonanceActiveConfig")]
public readonly partial record struct JobResonanceActiveConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iJob")] public partial long Job { get; }
    [ConfigField("_iJobResonanceId")] public partial long JobResonanceId { get; }
    [ConfigField("_iJobResonanceSkills")] public partial long[] JobResonanceSkills { get; }
}
