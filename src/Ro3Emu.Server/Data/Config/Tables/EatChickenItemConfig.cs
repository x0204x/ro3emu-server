using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EatChickenItemConfig (data/config/EatChickenItemConfig.json).</summary>
[ConfigTable("EatChickenItemConfig")]
public readonly partial record struct EatChickenItemConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSkill")] public partial long Skill { get; }
    [ConfigField("_kAttribute")] public partial long[][] Attribute { get; }
}
