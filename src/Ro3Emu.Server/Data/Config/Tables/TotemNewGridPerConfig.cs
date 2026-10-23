using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TotemNewGridPerConfig (data/config/TotemNewGridPerConfig.json).</summary>
[ConfigTable("TotemNewGridPerConfig")]
public readonly partial record struct TotemNewGridPerConfig(JsonElement Row)
{
    [ConfigField("_iNewGridPer")] public partial long NewGridPer { get; }
    [ConfigField("_iSkillMaxNum")] public partial long SkillMaxNum { get; }
    [ConfigField("_iSkillNum")] public partial long SkillNum { get; }
}
