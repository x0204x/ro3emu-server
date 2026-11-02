using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's PronteraDefenseGuildPrepConfig (data/config/PronteraDefenseGuildPrepConfig.json): the Battle for
/// Prontera guild preparation levels (Lua_CFG_PronteraDefenseGuildPrepConfig: GetExp, GetBuffId, GetMaxLevel = the
/// largest id).
/// </summary>
[ConfigTable("PronteraDefenseGuildPrepConfig")]
public readonly partial record struct PronteraDefenseGuildPrepConfig(JsonElement Row)
{
    /// <summary>The preparation level.</summary>
    [ConfigField("_iId")] public partial long Id { get; }
    /// <summary>The exp that fills the level's bar (UI_Sys_Season_DefenceWarWidgetCtrl: exp / GetExp(level)).</summary>
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iBuffId")] public partial long BuffId { get; }
}
