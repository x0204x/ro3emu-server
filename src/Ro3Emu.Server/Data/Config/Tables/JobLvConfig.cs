using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobLvConfig (data/config/JobLvConfig.json).</summary>
[ConfigTable("JobLvConfig")]
public readonly partial record struct JobLvConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJobRank")] public partial long JobRank { get; }
    [ConfigField("_iJobeExp")] public partial long JobeExp { get; }
    [ConfigField("_iMaxJobLv")] public partial long MaxJobLv { get; }
    [ConfigField("_iSkillPoint")] public partial long SkillPoint { get; }
}
