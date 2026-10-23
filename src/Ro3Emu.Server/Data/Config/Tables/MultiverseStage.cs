using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseStage (data/config/MultiverseStage.json).</summary>
[ConfigTable("MultiverseStage")]
public readonly partial record struct MultiverseStage(JsonElement Row)
{
    [ConfigField("_iEndTime")] public partial long[] EndTime { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMultiverseID")] public partial long MultiverseId { get; }
    [ConfigField("_iOpenTime")] public partial long[] OpenTime { get; }
    [ConfigField("_iPlace")] public partial long Place { get; }
    [ConfigField("_kStageActivity")] public partial long[] StageActivity { get; }
}
