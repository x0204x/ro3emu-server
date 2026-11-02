using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TypewriterConfig (data/config/TypewriterConfig.json): the full-screen story texts and what ends them.</summary>
[ConfigTable("TypewriterConfig", Optional = true)]
public readonly partial record struct TypewriterConfig(JsonElement Row)
{
    [ConfigField("_kAction", Optional = true)] public partial JsonElement? Action { get; }
}
