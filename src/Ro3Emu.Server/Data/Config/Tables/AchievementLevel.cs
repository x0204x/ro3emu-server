using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AchievementLevel (data/config/AchievementLevel.json).</summary>
[ConfigTable("AchievementLevel")]
public readonly partial record struct AchievementLevel(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iPoint")] public partial long Point { get; }
}
