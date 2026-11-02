using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's ItemExchangeConfig (data/config/ItemExchangeConfig.json): the shop's Currency Redemption. A row's id is
/// the item paid (UI_Sys_CoinExchangeWidgetCtrl keys m_kValues by m_iCostItemId).
/// </summary>
[ConfigTable("ItemExchangeConfig")]
public readonly partial record struct ItemExchangeConfig(JsonElement Row)
{
    [ConfigField("_iFunction")] public partial long Function { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRate")] public partial long Rate { get; }
    [ConfigField("_iSingleLimit")] public partial long SingleLimit { get; }
    [ConfigField("_iTargetID")] public partial long TargetId { get; }
}
