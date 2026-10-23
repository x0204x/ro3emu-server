using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DragonIslandMatchConfig (data/config/DragonIslandMatchConfig.json).</summary>
[ConfigTable("DragonIslandMatchConfig")]
public readonly partial record struct DragonIslandMatchConfig(JsonElement Row)
{
    [ConfigField("_kStartQuantity")] public partial long StartQuantity { get; }
    [ConfigField("_kTeamQuantity")] public partial long TeamQuantity { get; }
}
