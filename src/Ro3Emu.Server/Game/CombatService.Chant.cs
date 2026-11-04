using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>角色詠唱使用配裝與所有仍有效的增益所提供之 C-SPD、固定及變動詠唱修正；
    /// 普通技能、卡片被動及寵物增益共用屬性計算，不因來源不同而排除。</summary>
    internal int ChantMs(GameSession s, JsonElement row) => ChantMilliseconds(_data, row, _character.ChantStats(s));

    private const int PhenGroup = 20240;

    /// <summary>Card: Phen, 2024001..4: "Control effects cannot interrupt casting." See <see cref="BreakForbiddenCastAsync"/>.</summary>
    private static bool ControlCannotInterrupt(GameSession s) => s.Buffs.Any(b => b.ConfigId / 100 == PhenGroup && b.ExpireMs > Now);

    /// <summary>A*cspd/(B+C*cspd) from PublicParamConfig, cast speed clamped to FinalAtkSpdThdAndCastSpdThd[1].</summary>
    private static double VariableChantReduction(GameData data, double castSpeed)
    {
        var parameters = data.Table("PublicParamConfig");
        double Parameter(string key) => parameters.TryGetValue(key, out var value) ? GameData.Number(value) ?? 0 : 0;
        double speed = Math.Max(0, castSpeed);
        if (parameters.TryGetValue("FinalAtkSpdThdAndCastSpdThd", out var limits) && limits.ValueKind == JsonValueKind.Array
            && limits.GetArrayLength() > 1) speed = Math.Min(speed, GameData.Number(limits[1]) ?? speed);
        double denominator = Parameter("VariableChantPerParamB") + Parameter("VariableChantPerParamC") * speed;
        return denominator > 0 ? Parameter("VariableChantPerParamA") * speed / denominator : 0;
    }

    /// <summary>LC_DAL_PlayerInfo.GetRealPropertyValue(PreVarChant): per_varchant - A*cspd/(B+C*cspd).
    /// AttributeConfig 201..204 names additive/percentage variable/fixed CT; PublicParamConfig supplies A/B/C.
    /// (inferred) Apply the displayed variable factor to the configured variable time, fixed separately, floor ms.
    /// A negative factor cannot schedule a hit before acceptance. Absent formula inputs preserve the base timing.
    /// (inferred) Card: Phen's "Variable CT +25%" is 1335 buff_per_varchant, of calculation type 4 in group 202: the group's formula
    /// multiplies a base of 0 and gives it nothing, so it adds here as 202 does. Only Phen's four rows carry 1335.</summary>
    internal static int ChantMilliseconds(GameData data, JsonElement row, StatSheet stats)
    {
        double reduction = VariableChantReduction(data, stats[AttributeId.CastSpeed]);
        double variable = Math.Max(0, new SkillConfig(row).ChantVar + stats[AttributeId.VarChant])
            * Math.Max(0, 1 + (stats[AttributeId.PerVarChant] + stats[AttributeId.BuffPerVarChant]) / 10000.0 - reduction);
        double fixedTime = Math.Max(0, new SkillConfig(row).ChantFix + stats[AttributeId.FixChant])
            * Math.Max(0, 1 + stats[AttributeId.PerFixChant] / 10000.0);
        return (int)Math.Clamp(Math.Floor(variable + fixedTime), 0, int.MaxValue);
    }
}
