using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AwardRestricConfig (data/config/AwardRestricConfig.json).</summary>
[ConfigTable("AwardRestricConfig")]
public readonly partial record struct AwardRestricConfig(JsonElement Row)
{
    [ConfigField("_iItemID")] public partial long ItemId { get; }
    [ConfigField("_iReplaceAward")] public partial long ReplaceAward { get; }
    [ConfigField("_iRestricid")] public partial long Restricid { get; }
    [ConfigField("_iRewardID")] public partial long RewardId { get; }
}
