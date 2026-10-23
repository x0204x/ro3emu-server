using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildHuntRewardConfig (data/config/GuildHuntRewardConfig.json).</summary>
[ConfigTable("GuildHuntRewardConfig")]
public readonly partial record struct GuildHuntRewardConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
}
