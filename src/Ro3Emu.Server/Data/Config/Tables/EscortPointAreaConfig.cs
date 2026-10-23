using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EscortPointAreaConfig (data/config/EscortPointAreaConfig.json).</summary>
[ConfigTable("EscortPointAreaConfig")]
public readonly partial record struct EscortPointAreaConfig(JsonElement Row)
{
    [ConfigField("_iMapId")] public partial long MapId { get; }
    [ConfigField("_iPointId")] public partial long PointId { get; }
    [ConfigField("_kOutpostlNpcConfig")] public partial long[][] OutpostlNpcConfig { get; }
}
