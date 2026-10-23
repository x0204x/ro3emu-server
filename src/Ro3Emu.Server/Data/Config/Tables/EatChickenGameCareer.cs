using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EatChickenGameCareer (data/config/EatChickenGameCareer.json).</summary>
[ConfigTable("EatChickenGameCareer")]
public readonly partial record struct EatChickenGameCareer(JsonElement Row)
{
    [ConfigField("_iDefaultSkillId")] public partial long DefaultSkillId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsShow")] public partial long IsShow { get; }
    [ConfigField("_iJobAttr")] public partial long[][] JobAttr { get; }
    [ConfigField("_iNormalSkillId")] public partial long NormalSkillId { get; }
    [ConfigField("_iTalentSkillId")] public partial long TalentSkillId { get; }
}
