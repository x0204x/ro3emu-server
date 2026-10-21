using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Echo Resonance effects of the active Soul Echo plan. Lua_CFG_EmblemMarkConfig.GetAttrWithMarkAndNum (:66-111) collects
/// every stage of a mark whose _iMarkNum the lit count meets, so stages are cumulative: 10 marks hold stages 1, 2 and 3.
/// Each stage's _kBasicAttribute pairs and _kSpecialAttribute SpecialEffectConfig rows apply. In this export the
/// stage-1 rows are Attr1CountAddAttr2ForJob (15) and stages 2-3 are SkillInUse (6); this file interprets only those
/// (and plain Attr, 1), separately from the equipment-owned operations in EquipmentStunts.
/// </summary>
public sealed partial class EmblemService
{
    private const int OpAttr = 1, OpSkillInUse = 6, OpAttrForJob = 15;

    /// <summary>The EmblemMarkConfig rows reached by the given echoes, every stage up to the highest (cumulative).</summary>
    private static IEnumerable<JsonElement> ReachedStages(IEnumerable<InvItem> worn, GameData data)
    {
        var marks = MarkCounts(worn, data);
        if (marks.Count == 0) yield break;
        foreach (var row in data.Table("EmblemMarkConfig").Values)
            if (GameData.Int(row, "_iMarkStage") > 0 && GameData.Int(row, "_iEmblemMarkID") is var mark and > 0
                && marks.TryGetValue(mark, out int lit) && lit >= GameData.Int(row, "_iMarkNum"))
                yield return row;
    }

    /// <summary>
    /// Reached stages' EmblemMarkConfig._iSeasonPower, added to the plan's season power. No client code reads it: the
    /// config loader keeps it as kMarkStage.iPower (Lua_CFG_EmblemMarkConfig.lua:57-61) and nothing consumes that, while
    /// the item score (LC_EmblemHelper.GetEmblemScore) has no stage term. A power column only the server reads is taken
    /// as the plan's stage bonus (inferred).
    /// </summary>
    private static long StagePower(IEnumerable<InvItem> worn, GameData data) =>
        ReachedStages(worn, data).Sum(row => GameData.Int(row, "_iSeasonPower"));

    private static IEnumerable<long[]> ResonanceOps(GameSession session, GameData data)
    {
        foreach (var stage in ReachedStages(Worn(session), data))
        {
            if (!stage.TryGetProperty("_kSpecialAttribute", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
            foreach (var id in ids.EnumerateArray())
                if (data.Row("SpecialEffectConfig", (long)(GameData.Number(id) ?? 0)) is { } effect)
                    foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect")) yield return op;
        }
    }

    /// <summary>
    /// SkillInUse (6) of the reached stages: the SkillConfig rows whose _iJob is 0 or in the role's job line, the filter
    /// LC_SpecialEffectHelper (:50-88) applies before naming one. These are season profession-skill variants (e.g.
    /// 11130101 "Dragon's Descent") whose described behaviour (cast-triggered multipliers, stuns, skill swaps) has no
    /// config operation and no server-side profession-skill system to attach to. What the combat model supports for a
    /// held passive row is applied: its _kAttri, like CombatService.PassiveAttributes (empty in every current row).
    /// Holding the row exactly while the stage is reached is inferred timing.
    /// </summary>
    internal IEnumerable<int> ResonanceSkills(GameSession session)
    {
        if (session.Progress == null) yield break;
        var line = new HashSet<long>();
        for (long c = session.Progress.Career; c > 0 && line.Count < 8 && line.Add(c);)
            c = _data.Row("JobConfig", c) is { } job ? GameData.Int(job, "_iParentId") : 0;
        foreach (var op in ResonanceOps(session, _data).Where(op => op.Length >= 2 && op[0] == OpSkillInUse).DistinctBy(op => op[1]))
            if (_data.Row("SkillConfig", op[1]) is { } row && GameData.Int(row, "_iJob") is var job && (job == 0 || line.Contains(job)))
                yield return (int)op[1];
    }

    /// <summary>Raw contributions of the resonance: stage _kBasicAttribute, Attr (1) and held SkillInUse rows' _kAttri.</summary>
    private IEnumerable<(long Id, double Value)> ResonanceAttributes(GameSession session)
    {
        foreach (var stage in ReachedStages(Worn(session), _data))
            foreach (var attr in GameData.Pairs(stage, "_kBasicAttribute")) yield return attr;
        foreach (var op in ResonanceOps(session, _data))
            if (op.Length == 3 && op[0] == OpAttr) yield return (op[1], op[2]);
        foreach (int skill in ResonanceSkills(session))
            if (_data.Row("SkillConfig", skill) is { } row && GameData.Int(row, "_iBasicType") == 0)
                foreach (var attr in GameData.Pairs(row, "_kAttri")) yield return attr;
    }

    /// <summary>
    /// Attr1CountAddAttr2ForJob (15) [15, career, attr1, step, attr2, perStep, cap]: only for the exact career, then
    /// min(floor(attr1 / step) * perStep, cap) of attr2, as LC_SpecialEffectHelper._generateAttr1CountAddAttr2ForJobDesc
    /// (:2674-2744) previews it from TryGetAttrValue(attr1, true, true), the final value. (inferred) It reads the sheet
    /// after the equipment conversions, once, and enters the raw contributions like EquipmentStunts' type 8.
    /// </summary>
    internal static StatSheet ApplyResonanceConversions(GameSession session, Dictionary<int, double> raw, StatSheet sheet, GameData data)
    {
        if (session.Progress == null) return sheet;
        bool changed = false;
        foreach (var op in ResonanceOps(session, data))
        {
            if (op.Length != 7 || op[0] != OpAttrForJob || op[1] != session.Progress.Career) continue;
            long step = op[3];
            double value = step <= 0 ? 0 : Math.Min(Math.Floor((double)sheet[(int)op[2]] / step) * op[5], op[6]);
            if (value == 0) continue;
            raw[(int)op[4]] = raw.GetValueOrDefault((int)op[4]) + value;
            changed = true;
        }
        return changed ? StatCalculator.Compute(raw, data) : sheet;
    }
}
