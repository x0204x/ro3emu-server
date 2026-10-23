using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FirstHeartGroupConfig (data/config/FirstHeartGroupConfig.json).</summary>
[ConfigTable("FirstHeartGroupConfig")]
public readonly partial record struct FirstHeartGroupConfig(JsonElement Row)
{
    [ConfigField("_iActivityID")] public partial long ActivityId { get; }
    [ConfigField("_iActivityType")] public partial long ActivityType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kStage")] public partial long[] Stage { get; }
    [ConfigField("_kTasklist")] public partial long[] Tasklist { get; }
}
