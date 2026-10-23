using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PendantConfig (data/config/PendantConfig.json).</summary>
[ConfigTable("PendantConfig")]
public readonly partial record struct PendantConfig(JsonElement Row)
{
    [ConfigField("_iType")] public partial long Type { get; }
}
