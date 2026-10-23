using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DailyActivityCustomConfig (data/config/DailyActivityCustomConfig.json).</summary>
[ConfigTable("DailyActivityCustomConfig")]
public readonly partial record struct DailyActivityCustomConfig(JsonElement Row)
{
    [ConfigField("_iIsFriday")] public partial long IsFriday { get; }
    [ConfigField("_iIsMonday")] public partial long IsMonday { get; }
    [ConfigField("_iIsSaturday")] public partial long IsSaturday { get; }
    [ConfigField("_iIsSunday")] public partial long IsSunday { get; }
    [ConfigField("_iIsThursday")] public partial long IsThursday { get; }
    [ConfigField("_iIsTuesday")] public partial long IsTuesday { get; }
    [ConfigField("_iIsWednesday")] public partial long IsWednesday { get; }
    [ConfigField("_iOpenedWeek")] public partial long OpenedWeek { get; }
    [ConfigField("_kWeeklyOpenActivity")] public partial long[][] WeeklyOpenActivity { get; }
}
