using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildEventConfig (data/config/GuildEventConfig.json).</summary>
[ConfigTable("GuildEventConfig")]
public readonly partial record struct GuildEventConfig(JsonElement Row)
{
    [ConfigField("_iType")] public partial long Type { get; }
}
