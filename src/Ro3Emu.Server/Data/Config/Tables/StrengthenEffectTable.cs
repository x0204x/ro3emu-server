using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's StrengthenEffectTable (data/config/StrengthenEffectTable.json).</summary>
[ConfigTable("StrengthenEffectTable")]
public readonly partial record struct StrengthenEffectTable(JsonElement Row)
{
    [ConfigField("_iEffectLev")] public partial long EffectLev { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_kEffect")] public partial long[][] Effect { get; }
}
