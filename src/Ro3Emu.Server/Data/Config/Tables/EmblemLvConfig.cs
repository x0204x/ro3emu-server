using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EmblemLvConfig (data/config/EmblemLvConfig.json).</summary>
[ConfigTable("EmblemLvConfig")]
public readonly partial record struct EmblemLvConfig(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLv")] public partial long Lv { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
}
