using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ExplorePhaseConfig (data/config/ExplorePhaseConfig.json).</summary>
[ConfigTable("ExplorePhaseConfig")]
public readonly partial record struct ExplorePhaseConfig(JsonElement Row)
{
    [ConfigField("_iActivityid")] public partial long Activityid { get; }
    [ConfigField("_iPhaseID")] public partial long PhaseId { get; }
    [ConfigField("_kEndTime")] public partial long[] EndTime { get; }
    [ConfigField("_kNodes")] public partial long[] Nodes { get; }
    [ConfigField("_kStartTime")] public partial long[] StartTime { get; }
}
