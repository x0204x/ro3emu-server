using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SkillTreeConfig (data/config/SkillTreeConfig.json).</summary>
[ConfigTable("SkillTreeConfig")]
public readonly partial record struct SkillTreeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJob")] public partial long Job { get; }
    [ConfigField("_iY")] public partial long Y { get; }
}
