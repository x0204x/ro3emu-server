using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's OnePieceMonsterConfig (data/config/OnePieceMonsterConfig.json).</summary>
[ConfigTable("OnePieceMonsterConfig")]
public readonly partial record struct OnePieceMonsterConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iExtraAward")] public partial long ExtraAward { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iRefreshCount")] public partial long RefreshCount { get; }
    [ConfigField("_iStarId")] public partial long StarId { get; }
    [ConfigField("_kDungeonList")] public partial long[][] DungeonList { get; }
}
