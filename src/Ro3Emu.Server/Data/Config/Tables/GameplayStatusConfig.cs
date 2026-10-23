using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GameplayStatusConfig (data/config/GameplayStatusConfig.json).</summary>
[ConfigTable("GameplayStatusConfig")]
public readonly partial record struct GameplayStatusConfig(JsonElement Row)
{
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iStatusType")] public partial long StatusType { get; }
}
