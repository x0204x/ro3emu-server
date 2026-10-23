using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildSalaryConfig (data/config/GuildSalaryConfig.json).</summary>
[ConfigTable("GuildSalaryConfig")]
public readonly partial record struct GuildSalaryConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iServerRate")] public partial long ServerRate { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kActive")] public partial long[] Active { get; }
}
