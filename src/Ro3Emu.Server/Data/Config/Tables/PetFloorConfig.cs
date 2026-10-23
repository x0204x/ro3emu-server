using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetFloorConfig (data/config/PetFloorConfig.json).</summary>
[ConfigTable("PetFloorConfig")]
public readonly partial record struct PetFloorConfig(JsonElement Row)
{
    [ConfigField("_iFirstReward")] public partial long FirstReward { get; }
    [ConfigField("_iFloor")] public partial long Floor { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevelLimit")] public partial long LevelLimit { get; }
    [ConfigField("_iMapID")] public partial long MapId { get; }
    [ConfigField("_iPetOnBattle")] public partial long PetOnBattle { get; }
    [ConfigField("_iServerFirstReward")] public partial long ServerFirstReward { get; }
    [ConfigField("_iTimeLimit")] public partial long TimeLimit { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kDynamicDifficulty")] public partial long[][] DynamicDifficulty { get; }
    [ConfigField("_kMonsterGroup")] public partial long[][] MonsterGroup { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
