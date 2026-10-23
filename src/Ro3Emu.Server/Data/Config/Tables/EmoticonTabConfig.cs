using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EmoticonTabConfig (data/config/EmoticonTabConfig.json).</summary>
[ConfigTable("EmoticonTabConfig")]
public readonly partial record struct EmoticonTabConfig(JsonElement Row)
{
    [ConfigField("_iIndex")] public partial long[] Index { get; }
}
