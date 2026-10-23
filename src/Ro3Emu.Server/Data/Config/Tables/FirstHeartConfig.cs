using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FirstHeartConfig (data/config/FirstHeartConfig.json).</summary>
[ConfigTable("FirstHeartConfig")]
public readonly partial record struct FirstHeartConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPoint")] public partial long Point { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kProgressTarget")] public partial long[][] ProgressTarget { get; }
}
