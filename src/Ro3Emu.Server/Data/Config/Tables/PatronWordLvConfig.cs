using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronWordLvConfig (data/config/PatronWordLvConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronWordLvConfig")]
public readonly partial record struct PatronWordLvConfig(JsonElement Row)
{
    [ConfigField("_iDay")] public partial long Day { get; }
    [ConfigField("_iServerPatronLv")] public partial long ServerPatronLv { get; }
}
