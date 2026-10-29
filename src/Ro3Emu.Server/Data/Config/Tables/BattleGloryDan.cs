using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattleGloryDan (data/config/BattleGloryDan.json).</summary>
[ConfigTable("BattleGloryDan")]
public readonly partial record struct BattleGloryDan(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iGloryCondition")] public partial long GloryCondition { get; }
    [ConfigField("_iNormalRewardGroup")] public partial long NormalRewardGroup { get; }
    [ConfigField("_iRankCondition")] public partial long RankCondition { get; }
}
