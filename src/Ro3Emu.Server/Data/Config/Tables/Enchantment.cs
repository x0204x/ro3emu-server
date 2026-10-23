using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's Enchantment (data/config/Enchantment.json).</summary>
[ConfigTable("Enchantment")]
public readonly partial record struct Enchantment(JsonElement Row)
{
    [ConfigField("_iEnchantIDs")] public partial long[] EnchantIDs { get; }
    [ConfigField("_iEquipGroup")] public partial long EquipGroup { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIfUnique")] public partial long IfUnique { get; }
    [ConfigField("_iItem")] public partial long[] Item { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
    [ConfigField("_iSkills")] public partial long[] Skills { get; }
    [ConfigField("_iStars")] public partial long Stars { get; }
    [ConfigField("_kEnchanCost")] public partial long[] EnchanCost { get; }
    [ConfigField("_kEquipType")] public partial long[] EquipType { get; }
}
