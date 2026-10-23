using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TotemSkillConfig (data/config/TotemSkillConfig.json).</summary>
[ConfigTable("TotemSkillConfig")]
public readonly partial record struct TotemSkillConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsCoexist")] public partial long IsCoexist { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iSuperiorSkillID")] public partial long SuperiorSkillId { get; }
    [ConfigField("_kBasicAttribute")] public partial JsonElement? BasicAttribute { get; }
    [ConfigField("_kSpecialAttribute")] public partial long[] SpecialAttribute { get; }
}
