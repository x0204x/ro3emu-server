using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's OnHookMonsterRecommendConfig (data/config/OnHookMonsterRecommendConfig.json): the level range the
/// client recommends for hunting a monster on its maps.</summary>
[ConfigTable("OnHookMonsterRecommendConfig", Optional = true)]
public readonly partial record struct OnHookMonsterRecommendConfig(JsonElement Row)
{
    [ConfigField("_iMonsterID")] public partial long MonsterId { get; }
    [ConfigField("_iMapID")] public partial long[] MapId { get; }
    [ConfigField("_iRecommendationLevel")] public partial long[] RecommendationLevel { get; }
}
