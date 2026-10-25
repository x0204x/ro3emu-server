using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AdventureEventConfig (data/config/AdventureEventConfig.json); Survivor Battle encounters, TimedBattleService.ChickenAdventure.</summary>
[ConfigTable("AdventureEventConfig")]
public readonly partial record struct AdventureEventConfig(JsonElement Row)
{
    [ConfigField("_iFunctionId")] public partial long FunctionId { get; }
}
