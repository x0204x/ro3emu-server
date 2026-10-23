using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's WarzoneConfig (data/config/WarzoneConfig.json).</summary>
[ConfigTable("WarzoneConfig")]
public readonly partial record struct WarzoneConfig(JsonElement Row)
{
    [ConfigField("_iType")] public partial long Type { get; }
}
