using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TerritoryWarStageConfig (data/config/TerritoryWarStageConfig.json).</summary>
[ConfigTable("TerritoryWarStageConfig")]
public readonly partial record struct TerritoryWarStageConfig(JsonElement Row)
{
    [ConfigField("_iResultRule")] public partial long ResultRule { get; }
    [ConfigField("_iStageID")] public partial long StageId { get; }
    [ConfigField("_iStageType")] public partial long StageType { get; }
    [ConfigField("_iTimeLimitID")] public partial long TimeLimitId { get; }
}
