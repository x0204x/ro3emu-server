using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PerformanceConfig (data/config/PerformanceConfig.json).</summary>
[ConfigTable("PerformanceConfig")]
public readonly partial record struct PerformanceConfig(JsonElement Row)
{
    [ConfigField("_iDuration")] public partial long Duration { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPerformanceCD")] public partial long PerformanceCD { get; }
    [ConfigField("_iSex")] public partial long Sex { get; }
    [ConfigField("_iSort")] public partial long Sort { get; }
    [ConfigField("_iType")] public partial long[] Type { get; }
    [ConfigField("_iUnlockCondition")] public partial long UnlockCondition { get; }
}
