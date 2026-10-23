using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonPrepProgressReward (data/config/SeasonPrepProgressReward.json).</summary>
[ConfigTable("SeasonPrepProgressReward")]
public readonly partial record struct SeasonPrepProgressReward(JsonElement Row)
{
    [ConfigField("_iActivityid")] public partial long Activityid { get; }
    [ConfigField("_iEndType")] public partial long EndType { get; }
    [ConfigField("_iReward")] public partial long Reward { get; }
}
