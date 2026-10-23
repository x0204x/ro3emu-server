using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's WorldConfig (data/config/WorldConfig.json).</summary>
[ConfigTable("WorldConfig")]
public readonly partial record struct WorldConfig(JsonElement Row)
{
    [ConfigField("_iDecayType")] public partial long DecayType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
}
