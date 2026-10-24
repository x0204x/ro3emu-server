using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ChatConfig (data/config/ChatConfig.json).</summary>
[ConfigTable("ChatConfig")]
public readonly partial record struct ChatConfig(JsonElement Row)
{
    [ConfigField("_iContent")] public partial JsonElement? Content { get; }
    [ConfigField("_iTime")] public partial long Time { get; }
}
