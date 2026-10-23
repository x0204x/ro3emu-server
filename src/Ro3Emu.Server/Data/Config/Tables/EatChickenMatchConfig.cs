using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EatChickenMatchConfig (data/config/EatChickenMatchConfig.json).</summary>
[ConfigTable("EatChickenMatchConfig")]
public readonly partial record struct EatChickenMatchConfig(JsonElement Row)
{
    [ConfigField("_kStartQuantity")] public partial long StartQuantity { get; }
    [ConfigField("_kTeamQuantity")] public partial long TeamQuantity { get; }
}
