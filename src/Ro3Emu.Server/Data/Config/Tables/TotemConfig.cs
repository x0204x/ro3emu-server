using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TotemConfig (data/config/TotemConfig.json).</summary>
[ConfigTable("TotemConfig")]
public readonly partial record struct TotemConfig(JsonElement Row)
{
    [ConfigField("_iIfAppraise")] public partial long IfAppraise { get; }
    [ConfigField("_iIfCompose")] public partial long IfCompose { get; }
    [ConfigField("_iJobRank")] public partial long JobRank { get; }
    [ConfigField("_kBasicAttribute")] public partial long[][] BasicAttribute { get; }
    [ConfigField("_kBornSkill")] public partial long[][] BornSkill { get; }
    [ConfigField("_kSkillGroup")] public partial long[][] SkillGroup { get; }
    [ConfigField("_kSkillNumInit")] public partial long[][] SkillNumInit { get; }
}
