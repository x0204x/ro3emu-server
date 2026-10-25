using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AdventureCardConfig (data/config/AdventureCardConfig.json); Survivor Battle encounters, TimedBattleService.ChickenAdventure.</summary>
[ConfigTable("AdventureCardConfig")]
public readonly partial record struct AdventureCardConfig(JsonElement Row)
{
    [ConfigField("_iCardId")] public partial long CardId { get; }
    [ConfigField("_iPoolId")] public partial long PoolId { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_kEffect")] public partial long[][] Effect { get; }
}
