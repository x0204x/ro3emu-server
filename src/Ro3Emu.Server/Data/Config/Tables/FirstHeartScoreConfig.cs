using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FirstHeartScoreConfig (data/config/FirstHeartScoreConfig.json).</summary>
[ConfigTable("FirstHeartScoreConfig")]
public readonly partial record struct FirstHeartScoreConfig(JsonElement Row)
{
    [ConfigField("_iActivityType")] public partial long ActivityType { get; }
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iScore")] public partial long Score { get; }
}
