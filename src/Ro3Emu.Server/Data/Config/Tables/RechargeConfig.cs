using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's RechargeConfig (data/config/RechargeConfig.json): the top-up packages of UI_Sys_TopUpPanelCtrl, one set
/// per platform (1 Android, 2 iOS, 3 PC), each a currency [item, count] with a first-purchase or an extra bonus.
/// </summary>
[ConfigTable("RechargeConfig")]
public readonly partial record struct RechargeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPlatform")] public partial long Platform { get; }
    [ConfigField("_kAward")] public partial long[] Award { get; }
    [ConfigField("_kAwardBonus")] public partial long[] AwardBonus { get; }
    [ConfigField("_kFirstChargeAward")] public partial long[] FirstChargeAward { get; }
}
