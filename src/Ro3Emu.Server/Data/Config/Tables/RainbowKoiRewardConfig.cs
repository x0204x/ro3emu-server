using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RainbowKoiRewardConfig (data/config/RainbowKoiRewardConfig.json).</summary>
[ConfigTable("RainbowKoiRewardConfig")]
public readonly partial record struct RainbowKoiRewardConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
}
