using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SevenDaySignUpConfig (data/config/SevenDaySignUpConfig.json).</summary>
[ConfigTable("SevenDaySignUpConfig")]
public readonly partial record struct SevenDaySignUpConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRewardDate")] public partial long RewardDate { get; }
    [ConfigField("_iRewardID")] public partial long RewardId { get; }
}
