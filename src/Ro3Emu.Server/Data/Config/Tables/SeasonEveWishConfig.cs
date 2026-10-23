using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonEveWishConfig (data/config/SeasonEveWishConfig.json).</summary>
[ConfigTable("SeasonEveWishConfig")]
public readonly partial record struct SeasonEveWishConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iRewardId")] public partial long RewardId { get; }
}
