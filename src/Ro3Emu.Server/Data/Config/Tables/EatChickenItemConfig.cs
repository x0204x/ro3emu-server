using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EatChickenItemConfig (data/config/EatChickenItemConfig.json).</summary>
[ConfigTable("EatChickenItemConfig")]
public readonly partial record struct EatChickenItemConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSkill")] public partial long Skill { get; }
    [ConfigField("_kAttribute")] public partial long[][] Attribute { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_iSubType")] public partial long SubType { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_iStackLimit")] public partial long StackLimit { get; }
    [ConfigField("_iMaxQuantity")] public partial long MaxQuantity { get; }
    [ConfigField("_iCd")] public partial long Cd { get; }
    [ConfigField("_iUpgradeItem")] public partial long UpgradeItem { get; }
    [ConfigField("_iUpdateCount")] public partial long UpdateCount { get; }
}
