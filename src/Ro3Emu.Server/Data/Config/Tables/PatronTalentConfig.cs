using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronTalentConfig (data/config/PatronTalentConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronTalentConfig")]
public readonly partial record struct PatronTalentConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMaxLevel")] public partial long MaxLevel { get; }
    [ConfigField("_iPostTalentID")] public partial long[] PostTalentId { get; }
    [ConfigField("_iSeasonPower")] public partial long[] SeasonPower { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kTalentAttrIds")] public partial long[] TalentAttrIds { get; }
}
