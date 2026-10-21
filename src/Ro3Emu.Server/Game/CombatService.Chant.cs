using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>A player's chant: the configured fixed plus variable time, as before guardians. (inferred) A guardian's cast
    /// speed shortens (or a negative one lengthens) the variable part by the change it makes to the client's displayed
    /// variable-chant reduction; nothing else of the player's cast speed is applied yet.</summary>
    internal int ChantMs(GameSession s, JsonElement row)
    {
        long fixedTime = GameData.Int(row, "_iChantFix"), variable = GameData.Int(row, "_iChantVar");
        long guardianSpeed = _buffs.GuardianAttributes(s)[AttributeId.CastSpeed];
        if (guardianSpeed == 0) return (int)Math.Clamp(fixedTime + variable, 0, int.MaxValue);
        long speed = _character.Stats(s)[AttributeId.CastSpeed];
        double change = VariableChantReduction(_data, speed) - VariableChantReduction(_data, speed - guardianSpeed);
        return (int)Math.Clamp(Math.Floor(Math.Max(0, fixedTime) + Math.Max(0, variable) * Math.Max(0, 1 - change)),
            0, int.MaxValue);
    }

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
