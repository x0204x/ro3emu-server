using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Buff tables the client ships but no client Lua interprets (the server's half of the buff rules): BuffTagConfig,
/// SpecialStatusDecay, StateExclusionConfig, HaloConfig and the periodic damage/heal fields of BuffConfig. Built once
/// per <see cref="GameData"/> into int-keyed lookups, so the hot paths (buff application, aura and periodic ticks)
/// never read JSON. Everything a table only names and does not spell out is marked (inferred) where it is decided.
/// </summary>
internal sealed partial class BuffRuleTables
{
    /// <summary>SpecialStatusDecay._iDecayType of every map whose DungeonConfig/WorldConfig says 0 (inferred: the
    /// 161 ordinary dungeons and all 72 world maps carry 0 and no map names 10000, so 10000 is the default set).</summary>
    public const int DefaultDecayType = 10000;

    /// <summary>A BuffTagConfig row: the attacker's attribute (enhance) against the target's (resistance). Formula 1
    /// scales the chance to inflict, formula 2 the duration (AttributeConfig._kTips of every pair: "Affects your
    /// Chance to inflict Curse", "Affects the duration of Poisoned you inflict").</summary>
    public readonly record struct TagRule(int Tag, int AttackAttr, int DefenseAttr, int Formula);

    /// <summary>A SpecialStatusDecay row: within <see cref="WindowMs"/> of the last landing, each further control of
    /// these BuffConfig._kBuffCategory values lasts <see cref="RatioBp"/> of the previous one; the
    /// <see cref="Detections"/>-th landing makes the target immune for <see cref="ImmunityMs"/>.</summary>
    public sealed record DecayRule(int Id, int DecayType, int RatioBp, int ImmunityMs, int Detections, int WindowMs, int[] Categories);

    /// <summary>A HaloConfig row joined to its BuffConfig row (same id): the buffs the aura puts on its targets
    /// (the aura buff's _kLogicData entries that are BuffConfig rows), radius in m, tick, target cap and side.</summary>
    public sealed record HaloRule(int Id, float Radius, int TickMs, int TargetMax, int TargetType, bool Hostile,
        bool IncludeSelf, int[] Applied);

    public enum PeriodicKind { Damage, Heal }

    /// <summary>A BuffConfig row that hurts or heals its bearer every <see cref="IntervalMs"/>. Damage scales the
    /// applier's ATK (_iDmgType 1) or MATK (2) by <see cref="RatioBp"/> (_kData1[0], "loses HP equal to 60% of the
    /// applier's Fire Magic Damage" = 6000) through the hit formula, element <see cref="Element"/> (_iEleType);
    /// <see cref="Row"/> is that as a skill row for CombatService.BaseDamage. A heal restores
    /// <see cref="HealFlat"/> + the bearer's <see cref="HealAttr"/> x <see cref="HealRatioBp"/> / 10000.</summary>
    public sealed record PeriodicRule(int ConfigId, PeriodicKind Kind, int IntervalMs, bool Magic, int Element, long RatioBp,
        int HealAttr, long HealRatioBp, long HealFlat, JsonElement Row);

    private static readonly ConditionalWeakTable<GameData, BuffRuleTables> Cache = new();

    public static BuffRuleTables For(GameData data) => Cache.GetValue(data, d => new BuffRuleTables(d));

    private readonly GameData _data;
    private readonly ConcurrentDictionary<long, int> _sceneDecay = new();

    public IReadOnlyDictionary<int, TagRule> Tags { get; }
    /// <summary>BuffConfig id -> the tag rules its _kBuffCategory names (only buffs with at least one).</summary>
    public IReadOnlyDictionary<int, TagRule[]> TagsByBuff { get; }
    public IReadOnlyList<DecayRule> Decays { get; }
    /// <summary>BuffConfig id -> the decay rows its categories fall under (any decay type).</summary>
    public IReadOnlyDictionary<int, DecayRule[]> DecaysByBuff { get; }
    /// <summary>StateExclusionConfig._iSpecialStatusID values: ObjectState 13 SMIdle, 14 SMEmojiPlay,
    /// 17 SMEmojiPlayPrepare, the behaviour state machine's states.</summary>
    public IReadOnlyList<int> ExclusiveStates { get; }
    public IReadOnlyDictionary<int, HaloRule> Halos { get; }
    public IReadOnlyDictionary<int, PeriodicRule> Periodic { get; }

    private BuffRuleTables(GameData data)
    {
        this._data = data;
        var tags = new Dictionary<int, TagRule>();
        foreach (var row in data.Table("BuffTagConfig").Values)
        {
            int formula = (int)GameData.Int(row, "_iFormula"), atk = (int)GameData.Int(row, "_iAtkAttrID"),
                def = (int)GameData.Int(row, "_iDefAttrID");
            // 1007 "Damage Sharing" has no attributes and _iOnly 1: a classification tag, not a status pair.
            if (formula is 1 or 2 && atk > 0 && def > 0)
                tags[(int)GameData.Int(row, "_iID")] = new((int)GameData.Int(row, "_iID"), atk, def, formula);
        }
        Tags = tags;
        Decays = data.Table("SpecialStatusDecay").Values.Select(r => new DecayRule((int)GameData.Int(r, "_iID"),
                (int)GameData.Int(r, "_iDecayType"), (int)GameData.Int(r, "_iDecayRatio"), (int)GameData.Int(r, "_iImmunityTime"),
                (int)GameData.Int(r, "_iImmunodetection"), (int)GameData.Int(r, "_iIntervalDetection"), Ints(r, "_kBuffCategory")))
            .Where(r => r.Categories.Length > 0).OrderBy(r => r.Id).ToArray();
        ExclusiveStates = data.Table("StateExclusionConfig").Values
            .Select(r => (int)GameData.Int(r, "_iSpecialStatusID")).Where(s => s > 0).Distinct().Order().ToArray();

        var tagsByBuff = new Dictionary<int, TagRule[]>();
        var decaysByBuff = new Dictionary<int, DecayRule[]>();
        var periodic = new Dictionary<int, PeriodicRule>();
        foreach (var row in data.Table("BuffConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID");
            var categories = Ints(row, "_kBuffCategory");
            if (categories.Length > 0)
            {
                var matched = categories.Distinct().Where(tags.ContainsKey).Select(c => tags[c]).ToArray();
                if (matched.Length > 0) tagsByBuff[id] = matched;
                var decays = Decays.Where(d => d.Categories.Intersect(categories).Any()).ToArray();
                if (decays.Length > 0) decaysByBuff[id] = decays;
            }
            if (PeriodicOf(row) is { } rule) periodic[id] = rule;
        }
        TagsByBuff = tagsByBuff;
        DecaysByBuff = decaysByBuff;
        Periodic = periodic;

        var halos = new Dictionary<int, HaloRule>();
        foreach (var row in data.Table("HaloConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID");
            // 1161801, 1161901 and 11451401 have no BuffConfig row (passive skill ids): nothing carries them.
            if (data.Row("BuffConfig", id) is not { } buff) continue;
            // The aura's own row is no target buff (2328101 lists itself first), nor is another aura (no chains).
            var applied = Ints(buff, "_kLogicData").Where(b => b != id && !data.Table("HaloConfig").ContainsKey(b.ToString())
                && data.Row("BuffConfig", b) is { } r && GameData.Int(r, "_iBuffID") > 0).Distinct().ToArray();
            int type = (int)GameData.Int(row, "_iTargetType");
            // (inferred) 1 allies, 2 and 3 enemies (Desolation/Frozen Orbit "nearby enemies" are 3, King's Grace Enemy
            // Orbit and Battle Chant's ATK-down are 2); 0 follows what it applies: a debuff (BuffConfig._iType 2,
            // Battle Chant Lv2's ATK-down) goes to enemies, anything else (King's Grace, Holy Light) to allies.
            bool hostile = type is 2 or 3 || type == 0 && applied.Length > 0
                && applied.All(b => GameData.Int(data.Row("BuffConfig", b)!.Value, "_iType") == 2);
            halos[id] = new HaloRule(id, GameData.Int(row, "_iRadius") / 100f, (int)Math.Max(50, GameData.Int(row, "_iTickTime")),
                (int)Math.Max(1, GameData.Int(row, "_iTargetMax")), type, hostile,
                // (inferred) _iExtraTarget 1 adds the carrier ("Increases your and your allies' DEF" rows carry [1]);
                // 2 has no recovered meaning and is not used.
                !hostile && Ints(row, "_iExtraTarget").Contains(1), applied);
        }
        Halos = halos;
    }

    /// <summary>Chance or duration factor of one tag pair: 1 + (enhance - resistance) / 10000, never negative (inferred:
    /// both attributes are basis points, AttributeConfig._kRange "-9900|" keeps one of them above -99%).</summary>
    public static double TagFactor(long attack, long defense) => Math.Max(0, 1 + (attack - defense) / 10000.0);

    /// <summary>One target's history under one decay row.</summary>
    public sealed class DecayTrack
    {
        public int Count;
        public long LastMs;
        public long ImmuneUntilMs;
    }

    /// <summary>
    /// One control landing under a decay row at <paramref name="now"/>: false when it does not land (immune), else the
    /// duration factor. (inferred, from the four fields) A landing within _iIntervalDetection of the previous one counts
    /// on; the n-th lasts _iDecayRatio^(n-1) (100 %, 50 %); the _iImmunodetection-th (3rd) does not land and makes the
    /// target immune for _iImmunityTime; after the immunity, or a quiet window, the count starts again.
    /// </summary>
    /// <summary>Whether <see cref="Land"/> would refuse the next application, without recording it.</summary>
    public static bool WouldRefuse(DecayRule rule, DecayTrack track, long now)
    {
        if (now < track.ImmuneUntilMs) return true;
        int count = track.ImmuneUntilMs > 0 || now - track.LastMs > rule.WindowMs ? 1 : track.Count + 1;
        return rule.Detections > 0 && count >= rule.Detections;
    }

    public static bool Land(DecayRule rule, DecayTrack track, long now, out double factor)
    {
        factor = 1;
        if (now < track.ImmuneUntilMs) return false;
        if (track.ImmuneUntilMs > 0 || now - track.LastMs > rule.WindowMs)
        {
            track.Count = 0;
            track.ImmuneUntilMs = 0;
        }
        track.Count++;
        track.LastMs = now;
        if (rule.Detections > 0 && track.Count >= rule.Detections)
        {
            track.ImmuneUntilMs = now + rule.ImmunityMs;
            return false;
        }
        factor = Math.Pow(rule.RatioBp / 10000.0, track.Count - 1);
        return true;
    }

    /// <summary>The decay type of a map: DungeonConfig._iDecayType, else WorldConfig's, 0 meaning the default set.</summary>
    public int DecayTypeOf(long sceneEntry) => _sceneDecay.GetOrAdd(sceneEntry, scene =>
    {
        long type = _data.Row("DungeonConfig", scene) is { } dungeon ? GameData.Int(dungeon, "_iDecayType")
            : _data.Row("WorldConfig", scene) is { } world ? GameData.Int(world, "_iDecayType") : 0;
        return type > 0 ? (int)type : DefaultDecayType;
    });

    // "Every @{1} sec, loses HP...", "...every @{1} seconds": the description's first parameter is the interval.
    [GeneratedRegex(@"every @\{1\} sec", RegexOptions.IgnoreCase)]
    private static partial Regex EveryFirstParameter();

    [GeneratedRegex(@"\bHP\b|damage", RegexOptions.IgnoreCase)]
    private static partial Regex HurtsText();

    [GeneratedRegex(@"periodically|over time", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodicText();

    /// <summary>
    /// Which buffs tick, and how often. BuffEffect.json only lists ids, so the fields that define a tick are these:
    /// <list type="bullet">
    /// <item>Damage: a debuff (_iType 2) with _iDmgType 1 physical / 2 magic and a ratio _kData1[0] whose description
    /// says it hurts periodically. The interval is the description's first parameter where the text reads "every
    /// @{1} sec" (Burn/Bleeding/Poisoned 0.5 s, Continual Burn 0.5 s), _kDamageParamExtra[0][0] seconds for the field
    /// curses' T_Xplus1_Xplus2_Xisbuff (Divine Retribution/Dark Curse/Acid Corrosion [2,group,10] = "every 2
    /// seconds", the same 2 the description shows), and 1 s for "Periodically takes damage"/"Takes Damage over Time"
    /// (inferred). Shields with a damage type (Kyrie, Radiant Holy Light) and one-shot blasts never match.</item>
    /// <item>Heal: T_AXaddBadd3_etc [source, coefficient, attribute, flat] every second: Breath Medicine [0,0,1,30]
    /// "Restores 30 HP to self per second", Tension Relax [1,0,101,350] "a total of 1750 Life over 5 seconds"
    /// (350 x 5). The bearer's attribute stands for the coefficient's base (every exported row has coefficient 0).
    /// T_AXadd0_etc (shields, Meditate, the Survivor Battle drain) is not a tick here: its owners apply it.</item>
    /// </list>
    /// </summary>
    private PeriodicRule? PeriodicOf(JsonElement row)
    {
        int id = (int)GameData.Int(row, "_iID");
        string formula = row.TryGetProperty("_kDamageFormulaExtra", out var f) && f.ValueKind == JsonValueKind.Array
            && f.GetArrayLength() > 0 && f[0].ValueKind == JsonValueKind.String ? f[0].GetString()! : "";
        var extra = row.TryGetProperty("_kDamageParamExtra", out var e) && e.ValueKind == JsonValueKind.Array
            && e.GetArrayLength() > 0 && e[0].ValueKind == JsonValueKind.Array
            ? e[0].EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];
        if (formula == "T_AXaddBadd3_etc" && extra.Length == 4)
            return new PeriodicRule(id, PeriodicKind.Heal, 1000, false, 0, 0, (int)extra[2], (long)extra[1], (long)extra[3], default);

        int dmgType = (int)GameData.Int(row, "_iDmgType");
        long ratio = Ints(row, "_kData1").FirstOrDefault();
        if (GameData.Int(row, "_iType") != 2 || dmgType is not (1 or 2) || ratio <= 0) return null;
        int interval = 0;
        if (formula == "T_Xplus1_Xplus2_Xisbuff" && extra.Length > 0) interval = (int)(extra[0] * 1000);
        else if (row.TryGetProperty("_iDescription", out var desc) && desc.ValueKind == JsonValueKind.Array
                 && desc.GetArrayLength() > 0 && desc[0].TryGetInt64(out long textId))
        {
            string text = _data.Text(textId);
            if (!HurtsText().IsMatch(text)) return null;
            if (EveryFirstParameter().IsMatch(text) && desc.GetArrayLength() > 1 && GameData.Number(desc[1]) is { } seconds)
                interval = (int)Math.Round(seconds * 1000);
            else if (PeriodicText().IsMatch(text)) interval = 1000;
        }
        if (interval <= 0) return null;
        int element = (int)Math.Max(1, GameData.Int(row, "_iEleType"));
        var skill = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["_iDmgType"] = dmgType, ["_kDamageParam1"] = new[] { ratio }, ["_kDamageParam2"] = new[] { 0 }, ["_iEleType"] = element,
        });
        return new PeriodicRule(id, PeriodicKind.Damage, interval, dmgType == 2, element, ratio, 0, 0, 0, skill);
    }

    private static int[] Ints(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => GameData.Number(v)).Where(v => v != null).Select(v => (int)v!.Value).ToArray()
            : [];
}
