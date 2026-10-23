using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonTalentEffectConfig (data/config/SeasonTalentEffectConfig.json).</summary>
[ConfigTable("SeasonTalentEffectConfig")]
public readonly partial record struct SeasonTalentEffectConfig(JsonElement Row)
{
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iJobId")] public partial long[] JobId { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iSkillPoint")] public partial long SkillPoint { get; }
    [ConfigField("_kAttrs")] public partial long[][] Attrs { get; }
    [ConfigField("_kCosts")] public partial long[][] Costs { get; }
    [ConfigField("_kProfessionID")] public partial long[] ProfessionId { get; }
    [ConfigField("_kSkillPointCosts")] public partial long SkillPointCosts { get; }
    [ConfigField("_kSpecialEffectList")] public partial long[] SpecialEffectList { get; }
}
