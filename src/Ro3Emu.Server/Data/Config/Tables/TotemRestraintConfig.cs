using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TotemRestraintConfig (data/config/TotemRestraintConfig.json).</summary>
[ConfigTable("TotemRestraintConfig")]
public readonly partial record struct TotemRestraintConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
}
