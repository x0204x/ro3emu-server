using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's skill show (data/skills/skill_*.json, GameData.SkillTemplate): its timeline.</summary>
internal static class SkillShow
{
    /// <summary>
    /// Milliseconds after a cast's start from which it can be interrupted, or null if never before its end: m_fInterruptTime on
    /// the client's timeline (LC_Skill_RunTimeContext.calcDynamicTimeLine). m_kState.Time holds the chant's end and the show's
    /// end; a time inside the chant counts as the chant's end.
    /// </summary>
    public static int? InterruptOffset(JsonElement? show, int chantMs, int actionMs)
    {
        if (show is not { } row || !row.TryGetProperty("m_fInterruptTime", out var value) || GameData.Number(value) is not { } time || time <= 0
            || !row.TryGetProperty("m_kState", out var state) || !state.TryGetProperty("Time", out var times)
            || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() < 3) return null;
        double chant = GameData.Number(times[1]) ?? 0, end = GameData.Number(times[2]) ?? 0;
        double action = Math.Max(0, (time - chant) / Math.Max(0.001, end - chant));
        return (int)Math.Min(int.MaxValue, Math.Round(chantMs + action * actionMs));
    }
}
