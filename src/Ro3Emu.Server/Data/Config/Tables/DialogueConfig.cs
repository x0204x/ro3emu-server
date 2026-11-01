using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DialogueConfig (data/config/DialogueConfig.json): a dialogue id and its lines and options.</summary>
[ConfigTable("DialogueConfig", Grouped = true, Optional = true)]
public readonly partial record struct DialogueConfig(JsonElement Row)
{
    [ConfigField("_iID", Optional = true)] public partial long Id { get; }
    [ConfigField("_iEXECAction", Optional = true)] public partial JsonElement? EXECAction { get; }
    [ConfigField("_iText", Optional = true)] public partial JsonElement? Text { get; }
}
