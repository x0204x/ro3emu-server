using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ErrorCodeConfig (data/config/ErrorCodeConfig.json): the text id of each result code.</summary>
[ConfigTable("ErrorCodeConfig")]
public readonly partial record struct ErrorCodeConfig(JsonElement Row)
{
    [ConfigField("_iMessage")] public partial long[] Message { get; }
}
