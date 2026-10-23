using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TotemSkillNumRandom (data/config/TotemSkillNumRandom.json).</summary>
[ConfigTable("TotemSkillNumRandom")]
public readonly partial record struct TotemSkillNumRandom(JsonElement Row)
{
    [ConfigField("_iJobRank")] public partial long JobRank { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iSkillNum")] public partial long SkillNum { get; }
    [ConfigField("_iTotalSkills")] public partial long TotalSkills { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
