using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PronteraDefenseMapConfig (data/config/PronteraDefenseMapConfig.json).</summary>
[ConfigTable("PronteraDefenseMapConfig")]
public readonly partial record struct PronteraDefenseMapConfig(JsonElement Row)
{
    [ConfigField("_iDefensePoint")] public partial long DefensePoint { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMapId")] public partial long MapId { get; }
}
