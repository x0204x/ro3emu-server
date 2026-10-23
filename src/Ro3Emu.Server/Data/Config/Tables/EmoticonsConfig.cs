using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EmoticonsConfig (data/config/EmoticonsConfig.json).</summary>
[ConfigTable("EmoticonsConfig")]
public readonly partial record struct EmoticonsConfig(JsonElement Row)
{
    [ConfigField("_iType")] public partial long Type { get; }
}
