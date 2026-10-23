using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildChallengeConfig (data/config/GuildChallengeConfig.json).</summary>
[ConfigTable("GuildChallengeConfig")]
public readonly partial record struct GuildChallengeConfig(JsonElement Row)
{
    [ConfigField("_iActivityId")] public partial long ActivityId { get; }
    [ConfigField("_iGuildChallengeId")] public partial long GuildChallengeId { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iLimitNum")] public partial long LimitNum { get; }
    [ConfigField("_kBoss")] public partial long[][] Boss { get; }
    [ConfigField("_kCondition")] public partial long Condition { get; }
    [ConfigField("_kDungeonId")] public partial long[] DungeonId { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kStageId")] public partial long[] StageId { get; }
}
