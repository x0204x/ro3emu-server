using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BecomeStrongerRankConfig (data/config/BecomeStrongerRankConfig.json).</summary>
[ConfigTable("BecomeStrongerRankConfig")]
public readonly partial record struct BecomeStrongerRankConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iReward")] public partial long Reward { get; }
    [ConfigField("_iStandard")] public partial long Standard { get; }
}
