using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's BattlePassPrivilegeConfig (data/config/BattlePassPrivilegeConfig.json): the advanced pass privileges
/// that BattlePassConfig._iPrivilegeIds name and UI_Sys_Season_PassBuyPrivilegeListWidgetCtrl lists, typed by
/// EBattlePassPrivilegeType (MG_NewActivity.proto).
/// </summary>
[ConfigTable("BattlePassPrivilegeConfig")]
public readonly partial record struct BattlePassPrivilegeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPrivilegeType")] public partial long PrivilegeType { get; }
    [ConfigField("_kParams")] public partial long[] Params { get; }
}
