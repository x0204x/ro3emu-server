using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AreaConcurMonsterConfig (data/config/AreaConcurMonsterConfig.json).</summary>
[ConfigTable("AreaConcurMonsterConfig")]
public readonly partial record struct AreaConcurMonsterConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMap")] public partial long Map { get; }
    [ConfigField("_iNpc")] public partial long Npc { get; }
    [ConfigField("_iScoreLimit")] public partial long ScoreLimit { get; }
    [ConfigField("_kMonsterAward")] public partial long[][] MonsterAward { get; }
}
