using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobProfessConfig (data/config/JobProfessConfig.json): a class build (school), whose
/// _iProfessionID keys the recommended stats and skills (JobProfessPointConfig, JobProfessSkillConfig).</summary>
[ConfigTable("JobProfessConfig")]
public readonly partial record struct JobProfessConfig(JsonElement Row)
{
    [ConfigField("_iProfessionID")] public partial long ProfessionId { get; }
}
