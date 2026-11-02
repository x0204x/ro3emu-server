using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's KoiBetRewardConfig (data/config/KoiBetRewardConfig.json): the Festival koi bet payout by the number of
/// players who bet right (UI_Sys_ActivityEvening_RainbowKoiWidgetCtrl._getRewardPreviewList: "${1}~ ${2} players placed
/// the correct bet", a max of -1 has no upper bound).
/// </summary>
[ConfigTable("KoiBetRewardConfig")]
public readonly partial record struct KoiBetRewardConfig(JsonElement Row)
{
    [ConfigField("_iAwardId")] public partial long AwardId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iWinnerMax")] public partial long WinnerMax { get; }
    [ConfigField("_iWinnerMin")] public partial long WinnerMin { get; }
}
