using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TimeLimitConfig (data/config/TimeLimitConfig.json).</summary>
[ConfigTable("TimeLimitConfig")]
public readonly partial record struct TimeLimitConfig(JsonElement Row)
{
    [ConfigField("_iCycleCount")] public partial long CycleCount { get; }
    [ConfigField("_iMonthRefreshCount")] public partial long MonthRefreshCount { get; }
    [ConfigField("_iTimeLimitId")] public partial long TimeLimitId { get; }
    [ConfigField("_iWeekRefreshCount")] public partial long WeekRefreshCount { get; }
    [ConfigField("_iWeekStart")] public partial long WeekStart { get; }
    [ConfigField("_strStateName")] public partial string TrStateName { get; }
}
