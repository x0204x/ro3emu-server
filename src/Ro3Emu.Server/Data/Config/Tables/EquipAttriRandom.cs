using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipAttriRandom (data/config/EquipAttriRandom.json).</summary>
[ConfigTable("EquipAttriRandom")]
public readonly partial record struct EquipAttriRandom(JsonElement Row)
{
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iRatio")] public partial long Ratio { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
