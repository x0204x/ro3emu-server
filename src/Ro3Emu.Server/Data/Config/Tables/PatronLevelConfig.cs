using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronLevelConfig (data/config/PatronLevelConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronLevelConfig")]
public readonly partial record struct PatronLevelConfig(JsonElement Row)
{
    [ConfigField("_iPatronExp")] public partial long PatronExp { get; }
    [ConfigField("_iPatronLv")] public partial long PatronLv { get; }
    [ConfigField("_iStatusPoint")] public partial long StatusPoint { get; }
}
