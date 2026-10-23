using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattlePassRewardConfig (data/config/BattlePassRewardConfig.json).</summary>
[ConfigTable("BattlePassRewardConfig")]
public readonly partial record struct BattlePassRewardConfig(JsonElement Row)
{
    [ConfigField("_iActivityId")] public partial long ActivityId { get; }
    [ConfigField("_iExpRequired")] public partial long ExpRequired { get; }
    [ConfigField("_iFreeReward")] public partial long FreeReward { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iPremiumReward")] public partial long PremiumReward { get; }
}
