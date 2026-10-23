using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TenGameplayFinalTrialConfig (data/config/TenGameplayFinalTrialConfig.json).</summary>
[ConfigTable("TenGameplayFinalTrialConfig")]
public readonly partial record struct TenGameplayFinalTrialConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPersonAward")] public partial long PersonAward { get; }
    [ConfigField("_kAchieveConditions")] public partial long AchieveConditions { get; }
}
