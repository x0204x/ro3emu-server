using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's TitleBuffConfig (data/config/TitleBuffConfig.json, registered in Lua_DBManager but read by no client Lua):
/// the player-against-player damage term of a title level, Formula_ATitleDamageAdd.
/// </summary>
[ConfigTable("TitleBuffConfig")]
public readonly partial record struct TitleBuffConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    /// <summary>The title level, TitleConfig._kTitleLev (1..5).</summary>
    [ConfigField("_kIndex")] public partial long Index { get; }
    /// <summary>The level's damage term per 10000 (400..2000); the client's column is named _kTitleName.</summary>
    [ConfigField("_kTitleName")] public partial long DamageAdd { get; }
}
