using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildLeagueRankConfig (data/config/GuildLeagueRankConfig.json).</summary>
[ConfigTable("GuildLeagueRankConfig")]
public readonly partial record struct GuildLeagueRankConfig(JsonElement Row)
{
    [ConfigField("_iGroupNum")] public partial long GroupNum { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
    [ConfigField("_kAuctionAwardList")] public partial long[][] AuctionAwardList { get; }
    [ConfigField("_kAuctionLoseAwardList")] public partial long[][] AuctionLoseAwardList { get; }
}
