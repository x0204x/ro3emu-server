using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronRuneConfig (data/config/PatronRuneConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronRuneConfig")]
public readonly partial record struct PatronRuneConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
    [ConfigField("_kAttrs")] public partial long[][] Attrs { get; }
}
