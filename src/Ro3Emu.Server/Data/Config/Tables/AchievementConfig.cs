using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AchievementConfig (data/config/AchievementConfig.json).</summary>
[ConfigTable("AchievementConfig")]
public readonly partial record struct AchievementConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPoint")] public partial long Point { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kProgressTarget")] public partial long[][] ProgressTarget { get; }
}
