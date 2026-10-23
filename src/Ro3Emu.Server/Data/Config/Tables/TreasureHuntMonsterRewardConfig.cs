using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TreasureHuntMonsterRewardConfig (data/config/TreasureHuntMonsterRewardConfig.json).</summary>
[ConfigTable("TreasureHuntMonsterRewardConfig")]
public readonly partial record struct TreasureHuntMonsterRewardConfig(JsonElement Row)
{
    [ConfigField("_iAwardId")] public partial long AwardId { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMonsterArray")] public partial long[][] MonsterArray { get; }
}
