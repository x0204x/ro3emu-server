using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipCraftQualityConfig (data/config/EquipCraftQualityConfig.json).</summary>
[ConfigTable("EquipCraftQualityConfig")]
public readonly partial record struct EquipCraftQualityConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
