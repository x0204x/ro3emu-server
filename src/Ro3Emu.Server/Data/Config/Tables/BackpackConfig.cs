using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BackpackConfig (data/config/BackpackConfig.json).</summary>
[ConfigTable("BackpackConfig")]
public readonly partial record struct BackpackConfig(JsonElement Row)
{
    [ConfigField("_iTabCapacity")] public partial long TabCapacity { get; }
}
