using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipCraftConfig (data/config/EquipCraftConfig.json).</summary>
[ConfigTable("EquipCraftConfig")]
public readonly partial record struct EquipCraftConfig(JsonElement Row)
{
    [ConfigField("_iEquipType")] public partial long EquipType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iOrangeGuarantee")] public partial long OrangeGuarantee { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
    [ConfigField("_iRedGuarantee")] public partial long RedGuarantee { get; }
    [ConfigField("_kCost")] public partial long[][] Cost { get; }
    [ConfigField("_kQualityEquipID")] public partial long[][] QualityEquipId { get; }
}
