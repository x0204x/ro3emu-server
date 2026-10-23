using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PowerSuppressConfig (data/config/PowerSuppressConfig.json).</summary>
[ConfigTable("PowerSuppressConfig")]
public readonly partial record struct PowerSuppressConfig(JsonElement Row)
{
    [ConfigField("_kApplicableBuff")] public partial long[] ApplicableBuff { get; }
    [ConfigField("_kApplicableLevel")] public partial long[] ApplicableLevel { get; }
}
