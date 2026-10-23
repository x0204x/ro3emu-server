using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonPreProgressRewardConfig (data/config/SeasonPreProgressRewardConfig.json).</summary>
[ConfigTable("SeasonPreProgressRewardConfig")]
public readonly partial record struct SeasonPreProgressRewardConfig(JsonElement Row)
{
    [ConfigField("_iActivityid")] public partial long Activityid { get; }
    [ConfigField("_iEndType")] public partial long EndType { get; }
    [ConfigField("_iReward")] public partial long Reward { get; }
}
