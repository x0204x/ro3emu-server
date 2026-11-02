using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Per-instance gear rolls. Lua_CFG_EquipConfig.GetExtraAttribute joins _iEntries to EquipAttriGroupConfig._iGroup;
/// GetSpecialAttribute joins _kSpecial to EquipSpecialGroupConfig. LC_ItemProtoParse.ParseEquipInfo reads attribute
/// IDs/values at 100/140 and SpecialEffectConfig IDs at 80. See docs/systems/equipment-affixes.md for inference boundaries.
/// </summary>
internal static class EquipmentAffixes
{
    // Stage G: a roll's pools are fixed per (table, quality or group). Scanning the whole table with by-name JSON
    // lookups for every rolled item was a third of a new role's first login (the starter kit). Tables load once.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameData, Dictionary<(string Table, long Key), JsonElement[]>> Pools = [];
    private static readonly Dictionary<string, (string Field, Func<JsonElement, bool> Fixed)> PoolRules = new()
    {
        ["EquipAttriRandom"] = ("_iQuality", r => GameData.Int(r, "_iWeight") > 0),
        ["EquipEntryRandom"] = ("_iQuality", r => GameData.Int(r, "_iWeight") > 0),
        ["EquipAttriGroupConfig"] = ("_iGroup", r => GameData.Int(r, "_iWeight") > 0 && GameData.Int(r, "_iAttriID") > 0
            && GameData.Int(r, "_iMax") >= GameData.Int(r, "_iMin")),
        ["EquipSpecialGroupConfig"] = ("_iGroup", r => GameData.Int(r, "_iWeight") > 0),
    };

    /// <summary>The table's rows for the key passing the table's fixed rule, in table order; a fresh list per call.</summary>
    private static List<JsonElement> Pool(GameData data, string table, long key)
    {
        var cache = Pools.GetValue(data, _ => []);
        JsonElement[]? rows;
        lock (cache)
            if (!cache.TryGetValue((table, key), out rows))
            {
                var (field, rule) = PoolRules[table];
                cache[(table, key)] = rows = [.. data.Table(table).Values.Where(r => GameData.Int(r, field) == key && rule(r))];
            }
        return [.. rows];
    }

    internal const int EntryKey = 100, EntryValue = 140, EntrySlots = 20, SpecialKey = 80, SpecialSlots = 10;

    /// <summary>
    /// (inferred) Roll base stats on creation, preserving the existing quality-weighted ratio policy. For gear that
    /// needs appraisal, defer entries and stunts until 31401; otherwise finish the roll before persisting the grant.
    /// </summary>
    internal static Dictionary<int, long>? Create(int entry, GameData data)
    {
        if (Config(entry, data) is not { } equip || data.Row("ItemConfig", entry) is not { } item) return null;
        var attrs = new Dictionary<int, long> { [3] = GameData.Int(equip, "_iIfAppraise") == 1 ? 0 : 1 };
        RollBase(attrs, item, equip, data);
        if (attrs[3] == 1) RollAffixes(attrs, item, equip, data);
        return attrs;
    }

    /// <summary>Called only for a validated detached appraisal candidate; preserve base rolls and unrelated state.</summary>
    internal static void Appraise(InvItem item, GameData data)
    {
        if (Config(item.Entry, data) is not { } equip || data.Row("ItemConfig", item.Entry) is not { } config)
            throw new InvalidDataException($"Equipment {item.Entry} has no config");
        item.Attrs ??= [];
        if (!item.Attrs.ContainsKey(30)) RollBase(item.Attrs, config, equip, data);
        RollAffixes(item.Attrs, config, equip, data);
        item.Attrs[3] = 1;
    }

    /// <summary>
    /// (inferred) Repair pre-affix emulator saves only when an appraised item lacks a guaranteed entry. A completed
    /// roll, including a valid zero-stunt outcome, is never rerolled on login. Existing base stats remain unchanged.
    /// </summary>
    internal static bool NeedsLegacyEntries(InvItem item, GameData data)
    {
        if (item.Attrs?.GetValueOrDefault(3) != 1 || Entries(item).Any() || Config(item.Entry, data) is not { } equip)
            return false;
        if (Rows(equip, "_kFixedEntries").Any()) return true;
        if (GameData.Int(equip, "_iEntries") == 0 || data.Row("ItemConfig", item.Entry) is not { } config) return false;
        var counts = Pool(data, "EquipEntryRandom", GameData.Int(config, "_iQuality"));
        return counts.Count > 0 && counts.All(r => GameData.Int(r, "_iNum") > 0);
    }

    internal static JsonElement? Config(int entry, GameData data) =>
        data.Row("ItemConfig", entry) is { } item && GameData.Int(item, "_iType") == 5
            ? data.Row("EquipConfig", GameData.Int(item, "_iEquipId")) : null;

    private static void RollBase(Dictionary<int, long> attrs, JsonElement item, JsonElement equip, GameData data)
    {
        var ratios = Pool(data, "EquipAttriRandom", GameData.Int(item, "_iQuality"));
        int slot = 0;
        foreach (var row in Rows(equip, "_kBasicAttribute"))
        {
            if (row.Length < 2 || slot >= 20) continue;
            long min = row[1], max = row.Length > 2 ? row[2] : min;
            long ratio = ratios.Count == 0 ? 5000 : GameData.Int(Draw(ratios), "_iRatio");
            attrs[30 + slot] = row[0];
            attrs[60 + slot++] = min + (max - min) * ratio / 10000;
        }
    }

    /// <summary>
    /// Pool/ranges and fixed entries are client-proven. Weighted selection without replacement, inclusive uniform
    /// entry values, independent _kSpecial [group,chance/10000] draws and conflict filtering are (inferred).
    /// EquipEntryExclusion / EquipSpecialExclusion have only empty rows in both client exports: no hidden cross-ID
    /// rule is invented. Exclude duplicate attributes/effects and nonzero SpecialEffectConfig families.
    /// </summary>
    private static void RollAffixes(Dictionary<int, long> attrs, JsonElement item, JsonElement equip, GameData data)
    {
        var entryIds = new HashSet<long>();
        int entrySlot = 0;
        for (int i = 0; i < EntrySlots; i++)
        {
            attrs.Remove(EntryKey + i);
            attrs.Remove(EntryValue + i);
        }
        foreach (var fixedEntry in Rows(equip, "_kFixedEntries"))
            if (fixedEntry.Length >= 2 && fixedEntry[0] > 0 && entryIds.Add(fixedEntry[0]) && entrySlot < EntrySlots)
            {
                attrs[EntryKey + entrySlot] = fixedEntry[0];
                attrs[EntryValue + entrySlot++] = fixedEntry[1];
            }
        long group = GameData.Int(equip, "_iEntries");
        if (group > 0)
        {
            var counts = Pool(data, "EquipEntryRandom", GameData.Int(item, "_iQuality"));
            int count = counts.Count == 0 ? 0 : (int)GameData.Int(Draw(counts), "_iNum");
            var pool = Pool(data, "EquipAttriGroupConfig", group);
            pool.RemoveAll(r => entryIds.Contains(GameData.Int(r, "_iAttriID")));
            for (int n = 0; n < count && entrySlot < EntrySlots && pool.Count > 0; n++)
            {
                var picked = Draw(pool);
                long id = GameData.Int(picked, "_iAttriID"), min = GameData.Int(picked, "_iMin"), max = GameData.Int(picked, "_iMax");
                attrs[EntryKey + entrySlot] = id;
                attrs[EntryValue + entrySlot++] = min == max ? min : Random.Shared.NextInt64(min, max + 1);
                entryIds.Add(id);
                pool.RemoveAll(r => entryIds.Contains(GameData.Int(r, "_iAttriID")));
            }
        }

        // A legacy entry repair must preserve any already persisted stunt roll, including its order.
        if (attrs.ContainsKey(SpecialKey)) return;
        // Respect the parser's ten slots; never spill into the adjacent itemInfos namespaces (debug configs have 11).
        for (int i = 0; i < SpecialSlots; i++) attrs.Remove(SpecialKey + i);
        var specials = new HashSet<long>();
        var families = new HashSet<long>();
        int specialSlot = 0;
        bool Allowed(long id) => id > 0 && !specials.Contains(id) && data.Row("SpecialEffectConfig", id) is { } effect
            && (GameData.Int(effect, "_iGroupID") == 0 || !families.Contains(GameData.Int(effect, "_iGroupID")));
        void AddSpecial(long id)
        {
            if (specialSlot >= SpecialSlots || !Allowed(id)) return;
            attrs[SpecialKey + specialSlot++] = id;
            specials.Add(id);
            long family = GameData.Int(data.Row("SpecialEffectConfig", id)!.Value, "_iGroupID");
            if (family != 0) families.Add(family);
        }
        if (equip.TryGetProperty("_kFixedSpecialAttribute", out var fixedSpecials) && fixedSpecials.ValueKind == JsonValueKind.Array)
            foreach (var id in fixedSpecials.EnumerateArray()) AddSpecial((long)(GameData.Number(id) ?? 0));
        foreach (var directive in Rows(equip, "_kSpecial"))
        {
            if (directive.Length < 2 || specialSlot >= SpecialSlots || Random.Shared.Next(10000) >= directive[1]) continue;
            var pool = Pool(data, "EquipSpecialGroupConfig", directive[0]);
            pool.RemoveAll(r => GameData.Int(r, "_iSpecialID") != 0 && !Allowed(GameData.Int(r, "_iSpecialID")));
            if (pool.Count > 0) AddSpecial(GameData.Int(Draw(pool), "_iSpecialID"));
        }
    }

    private static JsonElement Draw(List<JsonElement> rows)
    {
        long roll = Random.Shared.NextInt64(rows.Sum(r => GameData.Int(r, "_iWeight")));
        foreach (var row in rows)
        {
            roll -= GameData.Int(row, "_iWeight");
            if (roll < 0) return row;
        }
        return rows[^1];
    }

    internal static IEnumerable<long[]> Rows(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) yield break;
        if (list[0].ValueKind != JsonValueKind.Array)
        {
            yield return list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            yield break;
        }
        foreach (var tuple in list.EnumerateArray())
            if (tuple.ValueKind == JsonValueKind.Array)
                yield return tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
    }

    internal static IEnumerable<(long Id, double Value)> Entries(InvItem item)
    {
        for (int i = 0; i < EntrySlots && item.Attrs != null && item.Attrs.TryGetValue(EntryKey + i, out long id)
            && item.Attrs.TryGetValue(EntryValue + i, out long value); i++) yield return (id, value);
    }

    internal static IEnumerable<long> Specials(InvItem item)
    {
        for (int i = 0; i < SpecialSlots && item.Attrs != null && item.Attrs.TryGetValue(SpecialKey + i, out long id); i++)
            if (id > 0) yield return id;
    }
}
