using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LvExpCorrectionConfig (data/config/LvExpCorrectionConfig.json).</summary>
[ConfigTable("LvExpCorrectionConfig")]
public readonly partial record struct LvExpCorrectionConfig(JsonElement Row)
{
    [ConfigField("_iBaseLvGap")] public partial long BaseLvGap { get; }
    [ConfigField("_iLvDifferenceCorrection")] public partial long LvDifferenceCorrection { get; }
}
