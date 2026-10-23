using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TotemLvConfig (data/config/TotemLvConfig.json).</summary>
[ConfigTable("TotemLvConfig")]
public readonly partial record struct TotemLvConfig(JsonElement Row)
{
    [ConfigField("_iAttributeEnhance")] public partial long AttributeEnhance { get; }
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iLv")] public partial long Lv { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
}
