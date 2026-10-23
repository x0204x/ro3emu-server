using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MonthCardConfig (data/config/MonthCardConfig.json).</summary>
[ConfigTable("MonthCardConfig")]
public readonly partial record struct MonthCardConfig(JsonElement Row)
{
    [ConfigField("_iDailyAward")] public partial long DailyAward { get; }
    [ConfigField("_iDay")] public partial long Day { get; }
    [ConfigField("_iDayLimit")] public partial long DayLimit { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iItem")] public partial long Item { get; }
}
