using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipEntryRandom (data/config/EquipEntryRandom.json).</summary>
[ConfigTable("EquipEntryRandom")]
public readonly partial record struct EquipEntryRandom(JsonElement Row)
{
    [ConfigField("_iNum")] public partial long Num { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
