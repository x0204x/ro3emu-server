using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipReturnConfig (data/config/EquipReturnConfig.json).</summary>
[ConfigTable("EquipReturnConfig")]
public readonly partial record struct EquipReturnConfig(JsonElement Row)
{
    [ConfigField("_iEntriesNum")] public partial long EntriesNum { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
    [ConfigField("_iSpecialNum")] public partial long SpecialNum { get; }
    [ConfigField("_kReturn")] public partial long[][] Return { get; }
}
