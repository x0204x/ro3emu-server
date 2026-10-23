using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeFormulaConfig (data/config/LifeFormulaConfig.json).</summary>
[ConfigTable("LifeFormulaConfig")]
public readonly partial record struct LifeFormulaConfig(JsonElement Row)
{
    [ConfigField("_iFormulaId")] public partial long FormulaId { get; }
    [ConfigField("_iJobId")] public partial long JobId { get; }
    [ConfigField("_iSuccessOutput")] public partial long[][] SuccessOutput { get; }
    [ConfigField("_iUnlockCondition")] public partial long UnlockCondition { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kUnlockCost")] public partial long[][] UnlockCost { get; }
    [ConfigField("_kcost")] public partial long[][] Cost { get; }
}
