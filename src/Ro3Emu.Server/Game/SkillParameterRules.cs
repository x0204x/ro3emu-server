using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The scalar part of BuffConfig._kSkillAttributeList. The client export carries
/// [scope, selector, SkillAffectValueType, operation, value, optional index]. Scope 0 names a skill group
/// (SkillConfig._iSkillID). Scope 1 names skill tags: "-1" every skill, "3012" one SkillConfig._kSkillCategory value,
/// "3012or3015" any of them, "1000and1001" all of them (477 exported tuples: 13 all, 226 single, 133 or, 105 and;
/// the client compares the same _kSkillCategory values in LC_Skill_Function.SkillInSelectWhitelist). The Wind-Pressure
/// Blade buffs 1130301-1130305 are [1,-1,29,1,1500..3500]: +15..35 damage-ratio points on every skill.
/// The operation arithmetic (1 add, 2 set, 5 basis-point multiplier) is inferred from the exported buff pairs, not
/// recovered original-server code. Only the fields <see cref="Field"/> names are applied, and the list-valued types 33
/// (SkillBuff1) and 34 (SkillBuff2): [.., value, row, column] changes column `column` of the _kBuff1/_kBuff2 tuple at
/// `row` (-1: every row), whose columns are [buff, chance, duration]. Hilt Binding's [16105, 33, add, 60000, 0, 2] is
/// "increases the duration of Adrenaline Rush ... by ${3} sec." and Dark Stalker's [15229, 34, add, 2000, -1, 1] "The
/// Stun and Blind Chance of [Meteor Assault] is increased by ${1}%" (inferred from those descriptions). Types 35-38
/// (SkillCost, SkillRange, SkillDamageParamExtra, SkillStageStates) are skipped, as are 5/9/22/28/31/32/39; type 10
/// is a CRIT chance (<see cref="CritChance"/>).
/// Talent plans add the same tuples (SeasonTalentService.Effects).
/// </summary>
internal static class SkillParameterRules
{
    /// <summary>One tuple. Scope 0: <paramref name="Group"/> is the skill group. Scope 1: <paramref name="Group"/> is 0
    /// and the tags select (null = every skill; <paramref name="AllTags"/> requires every tag, else any).</summary>
    internal readonly record struct Modifier(int Group, int Type, int Operation, long Value, int Index,
        int Scope = 0, int[]? Tags = null, bool AllTags = false, int Column = 0);

    private const int Buff1Type = 33, Buff2Type = 34;

    private static readonly ConditionalWeakTable<GameData, Dictionary<int, Modifier[]>> Parsed = new();
    private static readonly ConditionalWeakTable<GameData, Dictionary<int, int[]>> Categories = new();
    private static readonly ConditionalWeakTable<GameSession, SessionCache> Sessions = new();

    /// <summary>The session's modifiers and effective rows, kept while its buff set is unchanged: auto-attack swings
    /// and casts read them several times each, and the rows are re-serialized JSON.</summary>
    private sealed class SessionCache
    {
        public long Signature = long.MinValue;
        public GameData? Data;
        public IReadOnlyList<Modifier> Modifiers = [];
        public readonly Dictionary<int, JsonElement> Rows = new();
    }

    /// <summary>The parsed tuples of one BuffConfig row (empty when it has none), once per game data.</summary>
    internal static Modifier[] BuffModifiers(GameData data, int configId)
    {
        var table = Parsed.GetValue(data, _ => new Dictionary<int, Modifier[]>());
        lock (table)
        {
            if (table.TryGetValue(configId, out var hit)) return hit;
            var result = new List<Modifier>();
            if (data.Row("BuffConfig", configId) is { } row && row.TryGetProperty("_kSkillAttributeList", out var list)
                && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (Parse(item) is { } mod) result.Add(mod);
            return table[configId] = result.ToArray();
        }
    }

    internal static Modifier? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 5
            || GameData.Number(item[0]) is not { } scope || GameData.Number(item[2]) is not { } type
            || GameData.Number(item[3]) is not { } op || GameData.Number(item[4]) is not { } value) return null;
        int index = item.GetArrayLength() > 5 && GameData.Number(item[5]) is { } n ? (int)n : 0;
        int column = item.GetArrayLength() > 6 && GameData.Number(item[6]) is { } c ? (int)c : 0;
        if (scope == 0)
            return GameData.Number(item[1]) is { } group && group > 0
                ? new Modifier((int)group, (int)type, (int)op, (long)value, index, Column: column) : null;
        if (scope != 1) return null;
        string selector = item[1].ValueKind == JsonValueKind.String ? item[1].GetString()! : item[1].GetRawText();
        if (selector.Trim() == "-1") return new Modifier(0, (int)type, (int)op, (long)value, index, 1, Column: column);
        bool all = selector.Contains("and", StringComparison.Ordinal);
        var parts = selector.Split(all ? "and" : "or", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tags = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out tags[i])) return null;
        return tags.Length == 0 ? null : new Modifier(0, (int)type, (int)op, (long)value, index, 1, tags, all, column);
    }

    /// <summary>The live modifiers of a session: the talent plan's (whose weapon is held and whose buff is live), then
    /// once per buff pile, in buff order. Talents first is (inferred).</summary>
    internal static IReadOnlyList<Modifier> Active(GameSession session, GameData data) => Cache(session, data).Modifiers;

    /// <summary>Is an item of this ItemConfig._iSubType (1 Dagger, 2 Sword, 3 Two-Handed Sword, 4 Spear ...) in the
    /// main-hand or off-hand grid (enum_EquipGridType 1, 2).</summary>
    internal static bool Wields(GameSession session, GameData data, long subType) =>
        session.Items.Any(i => i.Grid is 1 or 2 && ItemConfig.Get(data, i.Entry) is { } item && item.SubType == subType);

    /// <summary>Does the session carry a live buff of this BuffConfig._iBuffID group.</summary>
    internal static bool HasBuffGroup(GameSession session, GameData data, long group)
    {
        long now = MonsterService.NowMs;
        foreach (var buff in session.Buffs)
            if (buff.ExpireMs > now && BuffConfig.Get(data, buff.ConfigId) is { } row && row.BuffId == group) return true;
        return false;
    }

    private static SessionCache Cache(GameSession session, GameData data)
    {
        var cache = Sessions.GetOrCreateValue(session);
        long now = MonsterService.NowMs;
        // A cheap fingerprint of what Active depends on: which buffs, their piles and whether each is still live, the
        // talent modifiers and, when one needs a weapon, what the hands hold.
        long signature = 17;
        foreach (var buff in session.Buffs)
            signature = unchecked(((signature * 31 + buff.Guid) * 31 + buff.ConfigId) * 31 + buff.PileCount * 2 + (buff.ExpireMs > now ? 1 : 0));
        var talents = session.TalentSkillModifiers;
        signature = unchecked(((signature * 31 + RuntimeHelpers.GetHashCode(talents)) * 31
            + RuntimeHelpers.GetHashCode(session.PassiveSkillModifiers)) * 31 + RuntimeHelpers.GetHashCode(session.TalentChargeModifiers));
        if (talents.Any(t => t.Weapon != 0) || session.PassiveSkillModifiers.Any(p => p.Weapons.Length > 0))
            foreach (var item in session.Items)
                if (item.Grid is 1 or 2) signature = unchecked((signature * 31 + item.Entry) * 31 + item.Grid);
        if (cache.Signature == signature && ReferenceEquals(cache.Data, data)) return cache;
        var result = new List<Modifier>();
        foreach (var (weapons, mod) in session.PassiveSkillModifiers)
            if (weapons.Length == 0 || weapons.Any(w => Wields(session, data, w))) result.Add(mod);
        foreach (var (weapon, buffGroup, mod) in talents)
            if ((weapon == 0 || Wields(session, data, weapon)) && (buffGroup == 0 || HasBuffGroup(session, data, buffGroup))) result.Add(mod);
        result.AddRange(session.TalentChargeModifiers);
        foreach (var buff in session.Buffs)
        {
            if (buff.ExpireMs <= now) continue;
            var mods = BuffModifiers(data, buff.ConfigId);
            for (int i = 0; i < buff.PileCount; i++) result.AddRange(mods);
        }
        cache.Signature = signature;
        cache.Data = data;
        cache.Modifiers = result;
        cache.Rows.Clear();
        return cache;
    }

    /// <summary>SkillConfig._kSkillCategory of a row (the tags scope-1 selectors name), once per game data.</summary>
    internal static int[] CategoriesOf(GameData data, JsonElement row)
    {
        int id = (int)GameData.Int(row, "_iID");
        var table = Categories.GetValue(data, _ => new Dictionary<int, int[]>());
        lock (table)
        {
            if (table.TryGetValue(id, out var hit)) return hit;
            int[] tags = row.TryGetProperty("_kSkillCategory", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(v => GameData.Number(v)).Where(v => v != null).Select(v => (int)v!.Value).ToArray() : [];
            return table[id] = tags;
        }
    }

    /// <summary>Does the modifier reach a skill of this group whose row carries these tags.</summary>
    internal static bool Matches(Modifier mod, int group, int[] tags)
    {
        if (mod.Scope == 0) return mod.Group == group;
        if (mod.Tags == null) return true;
        if (mod.AllTags)
        {
            foreach (int tag in mod.Tags) if (Array.IndexOf(tags, tag) < 0) return false;
            return true;
        }
        foreach (int tag in mod.Tags) if (Array.IndexOf(tags, tag) >= 0) return true;
        return false;
    }

    internal static long Apply(long value, Modifier modifier) => modifier.Operation switch
    {
        1 => value + modifier.Value,
        2 => modifier.Value,
        5 => (long)Math.Round(value * (1 + modifier.Value / 10000.0), MidpointRounding.AwayFromZero),
        _ => value,
    };

    internal static int Mode(int group, int[] tags, IReadOnlyList<Modifier> modifiers)
    {
        long mode = 0;
        foreach (var mod in modifiers)
            if (mod.Type == 15 && Matches(mod, group, tags)) mode = Apply(mode, mod);
        return (int)Math.Clamp(mode, 0, 99);
    }

    /// <summary>
    /// Type 10 tuples add a CRIT chance in basis points to their skills' hits: Advanced Katar Mastery's "[Assassin Normal
    /// Attack] CRIT Chance increases by ${5}%" is +4000, and Spiral Pierce Mastery's "your next [Spiral Pierce] is
    /// guaranteed to CRIT" +10000 (inferred from those descriptions; the client has no type 10).
    /// </summary>
    internal static double CritChance(GameSession session, GameData data, JsonElement row)
    {
        var mods = Active(session, data);
        if (mods.Count == 0) return 0;
        int group = (int)new SkillConfig(row).SkillId;
        int[]? tags = null;
        long sum = 0;
        foreach (var mod in mods)
            if (mod.Type == CritType && mod.Operation == 1 && Matches(mod, group, tags ??= CategoriesOf(data, row))) sum += mod.Value;
        return sum / 10000.0;
    }

    private const int CritType = 10;

    // The supported scalar/list fields are shared by server validation and client 31132.
    internal static string? Field(int type) => type switch
    {
        0 => "_iCD",
        21 => "_iActionTime",
        23 => "_iChantFix",
        24 => "_iChantVar",
        25 => "_iDistanceMin",
        26 => "_iDistanceMax",
        27 => "_iTargetMax",
        29 => "_kDamageParam1",
        30 => "_kDamageParam2",
        _ => null,
    };

    /// <summary><see cref="Effective(JsonElement, IReadOnlyList{Modifier}, int[])"/> for a session's live buffs,
    /// cached per row until the buff set changes.</summary>
    internal static JsonElement Effective(GameSession session, GameData data, JsonElement row)
    {
        var cache = Cache(session, data);
        if (cache.Modifiers.Count == 0) return row;
        int id = (int)GameData.Int(row, "_iID");
        if (cache.Rows.TryGetValue(id, out var hit)) return hit;
        return cache.Rows[id] = Effective(row, cache.Modifiers, CategoriesOf(data, row));
    }

    internal static JsonElement Effective(JsonElement row, IReadOnlyList<Modifier> modifiers, int[] tags)
    {
        int group = (int)GameData.Int(row, "_iSkillID");
        var relevant = modifiers.Where(m => Matches(m, group, tags) && (Field(m.Type) != null || m.Type is Buff1Type or Buff2Type)
            && m.Operation is 1 or 2 or 5).ToList();
        if (relevant.Count == 0) return row;
        var json = JsonNode.Parse(row.GetRawText())!.AsObject();
        foreach (var mod in relevant)
        {
            if (mod.Type is Buff1Type or Buff2Type)
            {
                ApplyToBuffTuples(json[mod.Type == Buff1Type ? "_kBuff1" : "_kBuff2"], mod);
                continue;
            }
            string field = Field(mod.Type)!;
            if (mod.Type is 29 or 30)
            {
                if (json[field] is not JsonArray arr) continue;
                // Index -1 in exported damage modifiers targets the full vector (inferred).
                int first = mod.Index < 0 ? 0 : mod.Index;
                int end = mod.Index < 0 ? arr.Count : Math.Min(arr.Count, first + 1);
                for (int i = first; i < end; i++)
                {
                    if (!long.TryParse(arr[i]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long old)) continue;
                    arr[i] = Apply(old, mod);
                }
            }
            else
            {
                if (!long.TryParse(json[field]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long old)) continue;
                json[field] = Apply(old, mod);
            }
        }
        return JsonSerializer.SerializeToElement(json);
    }

    /// <summary>A type 33/34 tuple on a _kBuff1/_kBuff2 list: column <see cref="Modifier.Column"/> of the tuple at
    /// <see cref="Modifier.Index"/> (-1 every tuple).</summary>
    private static void ApplyToBuffTuples(JsonNode? list, Modifier mod)
    {
        if (list is not JsonArray tuples) return;
        for (int i = 0; i < tuples.Count; i++)
        {
            if (mod.Index >= 0 && i != mod.Index || tuples[i] is not JsonArray tuple || mod.Column < 0 || mod.Column >= tuple.Count
                || !long.TryParse(tuple[mod.Column]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long old)) continue;
            tuple[mod.Column] = Apply(old, mod);
        }
    }
}
