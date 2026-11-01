using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>角色詠唱使用配裝與所有仍有效的增益所提供之 C-SPD、固定及變動詠唱修正；
    /// 普通技能、卡片被動及寵物增益共用屬性計算，不因來源不同而排除。</summary>
    internal int ChantMs(GameSession s, JsonElement row) => ChantMilliseconds(_data, row, _character.ChantStats(s));

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
    /// A negative factor cannot schedule a hit before acceptance. Absent formula inputs preserve the base timing.</summary>
    internal static int ChantMilliseconds(GameData data, JsonElement row, StatSheet stats)
    {
        double reduction = VariableChantReduction(data, stats[AttributeId.CastSpeed]);
        double variable = Math.Max(0, GameData.Int(row, "_iChantVar") + stats[AttributeId.VarChant])
            * Math.Max(0, 1 + stats[AttributeId.PerVarChant] / 10000.0 - reduction);
        double fixedTime = Math.Max(0, GameData.Int(row, "_iChantFix") + stats[AttributeId.FixChant])
            * Math.Max(0, 1 + stats[AttributeId.PerFixChant] / 10000.0);
        return (int)Math.Clamp(Math.Floor(variable + fixedTime), 0, int.MaxValue);
    }
}
