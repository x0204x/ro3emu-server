using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildPositionConfig (data/config/GuildPositionConfig.json).</summary>
[ConfigTable("GuildPositionConfig")]
public readonly partial record struct GuildPositionConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPositionNum")] public partial long PositionNum { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
