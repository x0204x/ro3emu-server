using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeSkillConfig (data/config/LifeSkillConfig.json).</summary>
[ConfigTable("LifeSkillConfig")]
public readonly partial record struct LifeSkillConfig(JsonElement Row)
{
    [ConfigField("_iDescription")] public partial long[] Description { get; }
    [ConfigField("_iFuctionType")] public partial long FuctionType { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iProbability")] public partial long Probability { get; }
    [ConfigField("_kFuctionDatas")] public partial long[] FuctionDatas { get; }
    [ConfigField("_kFuctionExtraData")] public partial long[] FuctionExtraData { get; }
    [ConfigField("_kSkillTags")] public partial long[] SkillTags { get; }
}
