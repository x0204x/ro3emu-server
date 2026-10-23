using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AIConfig (data/config/AIConfig.json).</summary>
[ConfigTable("AIConfig")]
public readonly partial record struct AIConfig(JsonElement Row)
{
    [ConfigField("_iChaseDistance")] public partial long ChaseDistance { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRoamDis")] public partial long RoamDis { get; }
    [ConfigField("_iRoamTime")] public partial long RoamTime { get; }
    [ConfigField("_iSearchTime")] public partial long SearchTime { get; }
    [ConfigField("_iUnFightBUFF")] public partial long[] UnFightBUFF { get; }
    [ConfigField("_iUnFightParam")] public partial long[] UnFightParam { get; }
    [ConfigField("_iUnFightType")] public partial long[] UnFightType { get; }
    [ConfigField("_iViewRange")] public partial long ViewRange { get; }
    [ConfigField("_kSkills")] public partial long[] Skills { get; }
}
