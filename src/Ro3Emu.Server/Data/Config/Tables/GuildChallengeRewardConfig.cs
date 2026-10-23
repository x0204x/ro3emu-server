using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildChallengeRewardConfig (data/config/GuildChallengeRewardConfig.json).</summary>
[ConfigTable("GuildChallengeRewardConfig")]
public readonly partial record struct GuildChallengeRewardConfig(JsonElement Row)
{
    [ConfigField("_iGuildChallengeId")] public partial long GuildChallengeId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iStage")] public partial long Stage { get; }
    [ConfigField("_kRewardStageAuction")] public partial long RewardStageAuction { get; }
    [ConfigField("_kRewardStagePersonal")] public partial long RewardStagePersonal { get; }
}
