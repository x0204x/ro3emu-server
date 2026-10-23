using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's OnePieceMapConfig (data/config/OnePieceMapConfig.json).</summary>
[ConfigTable("OnePieceMapConfig")]
public readonly partial record struct OnePieceMapConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMapWeight")] public partial long MapWeight { get; }
    [ConfigField("_kMonsterPoint")] public partial long[] MonsterPoint { get; }
    [ConfigField("_kStarWeight")] public partial long[][] StarWeight { get; }
}
