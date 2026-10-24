using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronExpAddConfig (data/config/PatronExpAddConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronExpAddConfig")]
public readonly partial record struct PatronExpAddConfig(JsonElement Row)
{
    [ConfigField("_iBaseLvGap")] public partial long BaseLvGap { get; }
    [ConfigField("_iPursueExpAdditionPercent")] public partial long PursueExpAdditionPercent { get; }
}
