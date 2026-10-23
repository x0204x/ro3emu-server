using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireLevel (data/config/CampFireLevel.json).</summary>
[ConfigTable("CampFireLevel")]
public readonly partial record struct CampFireLevel(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
}
