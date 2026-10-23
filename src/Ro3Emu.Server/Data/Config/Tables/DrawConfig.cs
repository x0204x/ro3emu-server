using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DrawConfig (data/config/DrawConfig.json).</summary>
[ConfigTable("DrawConfig")]
public readonly partial record struct DrawConfig(JsonElement Row)
{
    [ConfigField("_iFunctionID")] public partial long FunctionId { get; }
    [ConfigField("_iGuaranteedCnt")] public partial long GuaranteedCnt { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iNewAct")] public partial long NewAct { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kGuaranteedBanner")] public partial long[] GuaranteedBanner { get; }
    [ConfigField("_kMaxCnt")] public partial long[] MaxCnt { get; }
    [ConfigField("_kMaxRoll")] public partial long[] MaxRoll { get; }
    [ConfigField("_kMidRoll")] public partial long[] MidRoll { get; }
    [ConfigField("_kMinRoll")] public partial long[] MinRoll { get; }
    [ConfigField("_kRollNum")] public partial long[] RollNum { get; }
    [ConfigField("_kSubBanner")] public partial long[] SubBanner { get; }
}
