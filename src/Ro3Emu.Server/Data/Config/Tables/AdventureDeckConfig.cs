using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AdventureDeckConfig (data/config/AdventureDeckConfig.json); Survivor Battle encounters, TimedBattleService.ChickenAdventure.</summary>
[ConfigTable("AdventureDeckConfig")]
public readonly partial record struct AdventureDeckConfig(JsonElement Row)
{
    [ConfigField("_iPoolId")] public partial long PoolId { get; }
    [ConfigField("_iLimit")] public partial long Limit { get; }
    [ConfigField("_iAutoSelection")] public partial long AutoSelection { get; }
    [ConfigField("_kRewardStagePersonal")] public partial long[] RewardStagePersonal { get; }
    [ConfigField("_kRewardStageAuction")] public partial long[] RewardStageAuction { get; }
}
