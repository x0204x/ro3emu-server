using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Reading Spellbook: "While active, casting [the listed skills] triggers the corresponding skill effect one Extra time and consumes the corresponding
/// Floors of [Book Pages]. The effect cannot be triggered without enough [Book Pages]". The pages are EnergyConfig 240001, which refills by its own row.
/// Each listed group has a copy group at the same offset, whose rows are the extra cast; _kSkillHighParam names the cost and only the client reads it,
/// "0:1231001:1&amp;2:240001:3". The copies' marker buffs last about 500 ms, so the extra cast comes that long after the first (inferred).
/// </summary>
public sealed partial class CombatService
{
    internal const int ReadingBuff = 1231001, BookPages = 240001;
    private const int ReadingExtraDelayMs = 500;

    private static readonly Dictionary<long, long> ReadingCopies = new()
    {
        [12302] = 12328, // Soul Expansion
        [12308] = 12321, // Jack Frost
        [12310] = 12322, // Earth Strain
        [12312] = 12323, // Crimson Rock
        [12314] = 12324, // Thunderclap
        [12315] = 12325, // Comet
        [12316] = 12326, // Tetra Vortex
        [12342] = 12347, // Gravitational Impact
    };

    /// <summary>The last number of the row's first _kSkillHighParam condition: the pages "1231001:1&amp;1233501:5" or "0:1231001:1&amp;2:240001:3" asks.
    /// (inferred) Soul Expansion and Gravitational Impact name buff 1233501 there, while their text says Book Page, so they pay in pages too.</summary>
    internal static int ReadingCost(JsonElement row)
    {
        if (new SkillConfig(row).SkillHighParam is not { ValueKind: JsonValueKind.Array } conditions) return 0;
        foreach (var condition in conditions.EnumerateArray())
            foreach (var part in condition.ValueKind == JsonValueKind.Array ? condition.EnumerateArray() : Enumerable.Empty<JsonElement>())
                if (part.ValueKind == JsonValueKind.String && part.GetString() is { } text && text.Contains('&', StringComparison.Ordinal)
                    && int.TryParse(text[(text.LastIndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages))
                    return pages;
        return 0;
    }

    private async Task ReadingExtraCastAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        var skill = new SkillConfig(action.Row);
        if (!s.Buffs.Any(b => b.ConfigId == ReadingBuff && b.ExpireMs > Now) || !ReadingCopies.TryGetValue(skill.SkillId, out long copyGroup)
            || ReadingCost(action.Row) is not (> 0 and var cost)
            || SkillConfig.Get(_data, copyGroup * 100 + skill.Id % 100) is not { } copy
            || !await _buffs.TrySpendEnergyAsync(s, BookPages, cost)) return;
        var row = new SkillConfig(copy.WithoutBuff1());
        var show = _data.SkillTemplate(row.ScriptShow);
        var hits = ChannelHits(row.Row, show);
        var request = action.Request.Clone();
        request.IsExtraSkill = true;
        var at = request.TargetPos?.Clone() ?? action.Origin.Clone();
        foreach (var (point, strike) in hits.Length > 0 ? hits : [(0d, row.Row)])
        {
            int due = ReadingExtraDelayMs + (hits.Length > 0 ? ImpactOffset(show, point, 0, action.ActionMs) : 0);
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(due), () =>
                s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene ? Task.CompletedTask : ApplyEffectsAsync(s, new ActiveSkillCast
                {
                    Id = ++_sessionIds, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = s.SceneGeneration,
                    Attachment = s.Attachment, Request = request, Row = strike, Origin = action.Origin.Clone(), StartMs = Now, ChantMs = 0, ActionMs = 1,
                }, at));
        }
    }

    /// <summary>The mode row, script "book": "Ends the current [Reading] state."</summary>
    private async Task EndReadingAsync(GameSession s, ActiveSkillCast action)
    {
        if (Script(action.Row) != "book") return;
        foreach (var reading in s.Buffs.Where(b => b.ConfigId == ReadingBuff).ToArray()) await _buffs.RemoveAsync(s, reading);
    }
}
