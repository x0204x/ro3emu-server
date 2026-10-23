using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AreaConcurConfig (data/config/AreaConcurConfig.json).</summary>
[ConfigTable("AreaConcurConfig")]
public readonly partial record struct AreaConcurConfig(JsonElement Row)
{
    [ConfigField("_iArea")] public partial long Area { get; }
    [ConfigField("_iAreaDropAward")] public partial long AreaDropAward { get; }
    [ConfigField("_iAreaLimit")] public partial long AreaLimit { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kAreaStageAward")] public partial long[][] AreaStageAward { get; }
}
