using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's skill show (data/skills/skill_*.json, GameData.SkillTemplate): its timeline.</summary>
internal static class SkillShow
{
    /// <summary>The show's m_kAtkPoints in seconds on its timeline, in table order.</summary>
    public static double[] AttackPoints(JsonElement? show) =>
        show is { } row && row.TryGetProperty("m_kAtkPoints", out var points) && points.ValueKind == JsonValueKind.Array
            ? [.. points.EnumerateArray().Select(point => GameData.Number(point) ?? 0)] : [];

    /// <summary>
    /// Milliseconds after a cast's start from which it can be interrupted, or null if never before its end: m_fInterruptTime on
    /// the client's timeline (LC_Skill_RunTimeContext.calcDynamicTimeLine). m_kState.Time holds the chant's end and the show's
    /// end; a time inside the chant counts as the chant's end.
    /// </summary>
    public static int? InterruptOffset(JsonElement? show, int chantMs, int actionMs) =>
        TimelineOffset(show, "m_fInterruptTime", chantMs, actionMs);

    /// <summary>The same offset for m_fInterruptTime_move, from when a move may end the show (LC_Skill_RunTimeContext).</summary>
    public static int? MoveInterruptOffset(JsonElement? show, int chantMs, int actionMs) =>
        TimelineOffset(show, "m_fInterruptTime_move", chantMs, actionMs);

    private static int? TimelineOffset(JsonElement? show, string field, int chantMs, int actionMs)
    {
        if (show is not { } row || !row.TryGetProperty(field, out var value) || GameData.Number(value) is not { } time || time <= 0
            || !row.TryGetProperty("m_kState", out var state) || !state.TryGetProperty("Time", out var times)
            || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() < 3) return null;
        double chant = GameData.Number(times[1]) ?? 0, end = GameData.Number(times[2]) ?? 0;
        double action = Math.Max(0, (time - chant) / Math.Max(0.001, end - chant));
        return (int)Math.Min(int.MaxValue, Math.Round(chantMs + action * actionMs));
    }
}
