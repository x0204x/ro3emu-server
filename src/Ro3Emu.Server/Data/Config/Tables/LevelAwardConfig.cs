using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LevelAwardConfig (data/config/LevelAwardConfig.json).</summary>
[ConfigTable("LevelAwardConfig")]
public readonly partial record struct LevelAwardConfig(JsonElement Row)
{
    [ConfigField("_iAwardID")] public partial long AwardId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
}
