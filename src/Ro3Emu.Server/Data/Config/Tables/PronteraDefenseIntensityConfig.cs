using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PronteraDefenseIntensityConfig (data/config/PronteraDefenseIntensityConfig.json).</summary>
[ConfigTable("PronteraDefenseIntensityConfig")]
public readonly partial record struct PronteraDefenseIntensityConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iTime")] public partial long[] Time { get; }
    [ConfigField("_kIntensity")] public partial long[][] Intensity { get; }
}
