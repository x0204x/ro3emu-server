using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobResonanceRestraintConfig (data/config/JobResonanceRestraintConfig.json).</summary>
[ConfigTable("JobResonanceRestraintConfig")]
public readonly partial record struct JobResonanceRestraintConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
}
