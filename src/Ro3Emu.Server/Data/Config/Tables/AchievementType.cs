using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AchievementType (data/config/AchievementType.json).</summary>
[ConfigTable("AchievementType")]
public readonly partial record struct AchievementType(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iPoint")] public partial long Point { get; }
    [ConfigField("_iTypeID")] public partial long TypeId { get; }
}
