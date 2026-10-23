using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DailyActivityConfig (data/config/DailyActivityConfig.json).</summary>
[ConfigTable("DailyActivityConfig")]
public readonly partial record struct DailyActivityConfig(JsonElement Row)
{
    [ConfigField("_iCheckCondition")] public partial long CheckCondition { get; }
    [ConfigField("_iCount")] public partial long Count { get; }
    [ConfigField("_iFunction")] public partial long Function { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsCustome")] public partial long IsCustome { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
    [ConfigField("_iResetTimeType")] public partial long ResetTimeType { get; }
    [ConfigField("_iTaskReceiveAction")] public partial JsonElement? TaskReceiveAction { get; }
    [ConfigField("_iTimeCtrlId")] public partial long TimeCtrlId { get; }
    [ConfigField("_iTimeLimitId")] public partial long TimeLimitId { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_iiLv")] public partial long ILv { get; }
    [ConfigField("_kDay")] public partial long[] Day { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kTaskProgress")] public partial long[] TaskProgress { get; }
    [ConfigField("_kTime")] public partial long[] Time { get; }
}
