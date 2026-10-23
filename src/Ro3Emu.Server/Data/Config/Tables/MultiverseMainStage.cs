using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseMainStage (data/config/MultiverseMainStage.json).</summary>
[ConfigTable("MultiverseMainStage")]
public readonly partial record struct MultiverseMainStage(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMainStageLength")] public partial long MainStageLength { get; }
    [ConfigField("_iSeasonNum")] public partial long SeasonNum { get; }
    [ConfigField("_iStartTime")] public partial long StartTime { get; }
}
