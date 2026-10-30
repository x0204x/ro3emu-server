using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AIConfig (data/config/AIConfig.json).</summary>
[ConfigTable("AIConfig")]
public readonly partial record struct AIConfig(JsonElement Row)
{
    [ConfigField("_iAttackTickTime")] public partial long AttackTickTime { get; }
    [ConfigField("_iBattleCallDistance")] public partial long BattleCallDistance { get; }
    [ConfigField("_iBattleCallResponse")] public partial long BattleCallResponse { get; }
    [ConfigField("_iBattleCallType")] public partial long BattleCallType { get; }
    [ConfigField("_iCanInBattle")] public partial long CanInBattle { get; }
    [ConfigField("_iChaseDistance")] public partial long ChaseDistance { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsSurround")] public partial long IsSurround { get; }
    [ConfigField("_iRoamDis")] public partial long RoamDis { get; }
    [ConfigField("_iRoamTime")] public partial long RoamTime { get; }
    [ConfigField("_iSearchTime")] public partial long SearchTime { get; }
    [ConfigField("_iStayInPlace")] public partial long StayInPlace { get; }
    [ConfigField("_iSurroundDistance")] public partial long SurroundDistance { get; }
    [ConfigField("_iUnFightBUFF")] public partial long[] UnFightBUFF { get; }
    [ConfigField("_iUnFightParam")] public partial long[] UnFightParam { get; }
    [ConfigField("_iUnFightType")] public partial long[] UnFightType { get; }
    [ConfigField("_iViewRange")] public partial long ViewRange { get; }
    [ConfigField("_kSkills")] public partial long[] Skills { get; }
}
