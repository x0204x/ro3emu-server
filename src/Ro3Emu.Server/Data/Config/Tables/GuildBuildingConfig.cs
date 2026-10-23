using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildBuildingConfig (data/config/GuildBuildingConfig.json).</summary>
[ConfigTable("GuildBuildingConfig")]
public readonly partial record struct GuildBuildingConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLv")] public partial long Lv { get; }
    [ConfigField("_iNewCostMoney")] public partial long[] NewCostMoney { get; }
    [ConfigField("_iPeople")] public partial long People { get; }
    [ConfigField("_iTime")] public partial long Time { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kLimit")] public partial long[][] Limit { get; }
}
