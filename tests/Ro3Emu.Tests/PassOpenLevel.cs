using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>
/// The Novice Pass (NewActivityConfig 9201) opens at the base level of its open condition, LC_ConditionList 920199
/// [1002, level]. The client data decides it: level 10 until the 23 Oct 2026 Genesis client, level 30 from it.
/// </summary>
internal static class PassOpenLevel
{
    public static long Novice(GameData data)
    {
        var activity = data.Row("NewActivityConfig", 9201) ?? throw new InvalidOperationException("No Novice Pass row.");
        var condition = data.Row("LC_ConditionList", GameData.Int(activity, "_kOpenCondition"))
            ?? throw new InvalidOperationException("No Novice Pass open condition.");
        foreach (var sub in condition.GetProperty("m_kSubConditionList").EnumerateArray())
            if (sub.GetProperty("m_kData") is { ValueKind: JsonValueKind.Array } args && args.GetArrayLength() >= 2
                && GameData.Number(args[0]) == 1002)
                return (long)(GameData.Number(args[1]) ?? 0);
        throw new InvalidOperationException("The Novice Pass open condition has no base level.");
    }
}
