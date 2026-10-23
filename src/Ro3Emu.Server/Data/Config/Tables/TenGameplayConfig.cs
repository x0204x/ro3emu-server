using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TenGameplayConfig (data/config/TenGameplayConfig.json).</summary>
[ConfigTable("TenGameplayConfig")]
public readonly partial record struct TenGameplayConfig(JsonElement Row)
{
    [ConfigField("_iDifficulty")] public partial long Difficulty { get; }
    [ConfigField("_iDungeonType")] public partial long DungeonType { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kBoss")] public partial long[][] Boss { get; }
    [ConfigField("_kLimit")] public partial long[] Limit { get; }
}
