using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PersonalPerformanceConfig (data/config/PersonalPerformanceConfig.json).</summary>
[ConfigTable("PersonalPerformanceConfig")]
public readonly partial record struct PersonalPerformanceConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iUnlockCondition")] public partial long UnlockCondition { get; }
}
