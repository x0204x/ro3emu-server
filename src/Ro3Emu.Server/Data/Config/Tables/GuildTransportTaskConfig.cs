using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildTransportTaskConfig (data/config/GuildTransportTaskConfig.json).</summary>
[ConfigTable("GuildTransportTaskConfig")]
public readonly partial record struct GuildTransportTaskConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iNum")] public partial long Num { get; }
    [ConfigField("_kAward")] public partial long[] Award { get; }
}
