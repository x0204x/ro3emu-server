using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BaseLvConfig (data/config/BaseLvConfig.json).</summary>
[ConfigTable("BaseLvConfig")]
public readonly partial record struct BaseLvConfig(JsonElement Row)
{
    [ConfigField("_iBaseExp")] public partial long BaseExp { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_iStatusPoint")] public partial long StatusPoint { get; }
}
