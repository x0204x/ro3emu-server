using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GMCombinationConfig (data/config/GMCombinationConfig.json).</summary>
[ConfigTable("GMCombinationConfig")]
public readonly partial record struct GMCombinationConfig(JsonElement Row)
{
    [ConfigField("_kCombinationScript")] public partial JsonElement? CombinationScript { get; }
}
