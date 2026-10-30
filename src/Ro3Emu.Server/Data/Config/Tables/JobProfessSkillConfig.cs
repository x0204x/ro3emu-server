using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobProfessSkillConfig (data/config/JobProfessSkillConfig.json): a profession's recommended
/// skill levels (SkillConfig row ids) at a job rank and job level, id = profession * 1000 + rank * 100 + job level
/// (Lua_CFG_JobProfessSkillConfig.GetAddPointOneConfigByProfession).</summary>
[ConfigTable("JobProfessSkillConfig")]
public readonly partial record struct JobProfessSkillConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJobLv")] public partial long JobLv { get; }
    [ConfigField("_iJobRank")] public partial long JobRank { get; }
    [ConfigField("_iProfessionID")] public partial long ProfessionId { get; }
    [ConfigField("_kDescData")] public partial long[] DescData { get; }
}
