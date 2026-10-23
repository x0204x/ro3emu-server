using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MountsSpecialConfig (data/config/MountsSpecialConfig.json).</summary>
[ConfigTable("MountsSpecialConfig")]
public readonly partial record struct MountsSpecialConfig(JsonElement Row)
{
    [ConfigField("_iSLev")] public partial long SLev { get; }
    [ConfigField("_iSValue")] public partial long SValue { get; }
    [ConfigField("_kAward")] public partial long[][] Award { get; }
}
