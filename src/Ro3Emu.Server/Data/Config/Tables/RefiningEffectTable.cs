using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RefiningEffectTable (data/config/RefiningEffectTable.json).</summary>
[ConfigTable("RefiningEffectTable")]
public readonly partial record struct RefiningEffectTable(JsonElement Row)
{
    [ConfigField("_iEffectLev")] public partial long EffectLev { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_kEffect")] public partial long[][] Effect { get; }
}
