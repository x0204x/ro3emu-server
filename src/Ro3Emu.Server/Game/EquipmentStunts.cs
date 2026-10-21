using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Equipment-owned SpecialEffectConfig operations. The gear pools contain Attr(1), EffectCountAddAttr(4),
/// RefineCountAddAttr(5), Attr1CountAddAttr2(8), InstantPotionEnhance(13), TwoAttrAddOneAttr(14); socketed cards add
/// their CardConfig._kSkillAttribute / Flash _kFlashAttribute operations Attr(1) and Attr1CountAddAttr2(8).
/// Formulas follow LC_SpecialEffectHelper's contextual previews.
/// Applying these previews to authoritative stats is (inferred). Card Buff(2) operations are CardEffects' passives.
/// </summary>
internal static class EquipmentStunts
{
    /// <summary>
    /// LC_DAL_Equipment.RefreshSpecailEffectNum and the equipment tooltip exclude a main-hand weapon carried in the
    /// off-hand grid; each equipped GUID contributes once. Bag items and inactive plans contribute nothing.
    /// </summary>
    private static IEnumerable<InvItem> Active(GameSession session, GameData data) => session.Items
        .Where(i => i.Grid > 0 && CharacterService.IsAppraised(i)
            && !(i.Grid == 2 && data.Row("ItemConfig", i.Entry) is { } config && GameData.Int(config, "_iEquipPart") == 2))
        .DistinctBy(i => i.Guid);

    private static IEnumerable<(InvItem Item, long[] Op)> Operations(GameSession session, GameData data)
    {
        foreach (var item in Active(session, data))
            foreach (long id in EquipmentAffixes.Specials(item))
                if (data.Row("SpecialEffectConfig", id) is { } effect)
                    foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect")) yield return (item, op);
    }

    private static readonly ConditionalWeakTable<GameData, Dictionary<(long Card, long Tier), JsonElement>> CardRows = new();

    /// <summary>CardConfig row of a card at a tier (star), indexed once per data set.</summary>
    internal static JsonElement? CardRow(GameData data, int entry, long tier) => CardRows.GetValue(data, d => d.Table("CardConfig").Values
            .GroupBy(r => (GameData.Int(r, "_iCardID"), GameData.Int(r, "_iTier"))).ToDictionary(g => g.Key, g => g.First()))
        .TryGetValue((entry, tier), out var row) ? row : null;

    /// <summary>A CardConfig id list field ([id] or {} in the export).</summary>
    internal static IEnumerable<long> Ids(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).Where(id => id > 0) : [];

    /// <summary>
    /// Card-owned SpecialEffectConfig ids of a socketed copy at its star: CardConfig._kSkillAttribute always, and
    /// _kFlashAttribute for a Flash copy (item attr 530 = enum_CardType.Flash). LC_CardHelper.GetCardAttributes and
    /// GetFlashCardAttributes (lua:1057-1206) list them beside _kBasicAttribute; the Flash list only when the type is
    /// Flash. Bag copies contribute nothing, like _kBasicAttribute in CharacterService.AddOwnAttributes.
    /// </summary>
    internal static IEnumerable<(InvItem Card, long Effect)> CardEffects(GameSession session, GameData data)
    {
        foreach (var card in session.Items.Where(i => i.Grid < 0))
        {
            if (CardRow(data, card.Entry, card.Attrs?.GetValueOrDefault(502) ?? 0) is not { } row) continue;
            foreach (long id in Ids(row, "_kSkillAttribute")) yield return (card, id);
            if ((card.Attrs?.GetValueOrDefault(530) ?? 0) == 1)
                foreach (long id in Ids(row, "_kFlashAttribute")) yield return (card, id);
        }
    }

    private static IEnumerable<(InvItem Card, long[] Op)> CardOperations(GameSession session, GameData data)
    {
        foreach (var (card, id) in CardEffects(session, data))
            if (data.Row("SpecialEffectConfig", id) is { } effect)
                foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect")) yield return (card, op);
    }

    /// <summary>Flat/stat-count/refine bonuses enter before STR..LUK conversion, like ordinary affixes. Socketed cards add
    /// their Attr(1) operations (all 424 Flash rows are opcode 1) the same way (inferred, as for gear).</summary>
    internal static IEnumerable<(long Id, double Value)> FlatAttributes(GameSession session, GameData data)
    {
        int count = Active(session, data).Sum(i => EquipmentAffixes.Specials(i).Count());
        foreach (var (item, op) in Operations(session, data))
        {
            if (op.Length == 3 && op[0] == 1) yield return (op[1], op[2]);
            else if (op.Length == 5 && op[0] == 4 && op[1] > 0)
                yield return (op[3], Math.Min(count, op[2]) / op[1] * op[4]);
            else if (op.Length == 4 && op[0] == 5 && op[1] > 0)
                yield return (op[2], session.EquipLevels.Refine.GetValueOrDefault(item.Grid) / op[1] * op[3]);
        }
        foreach (var (_, op) in CardOperations(session, data))
            if (op.Length == 3 && op[0] == 1) yield return (op[1], op[2]);
    }

    /// <summary>
    /// LC_SpecialEffectHelper:2099-2150,2612-2672 uses floor before multiplying, then caps the bonus.
    /// (inferred) All conversions read one pre-conversion stat snapshot; reciprocal PATK/MATK conversions cannot
    /// amplify themselves or depend on inventory iteration order. Modifiers enter their own AttributeConfig roots.
    /// Socketed cards' Attr1CountAddAttr2 (8) operations (CardConfig._kSkillAttribute, e.g. 11000062
    /// [8, 109, 1, 1304, 1, 650]) convert the same way.
    /// </summary>
    internal static StatSheet ApplyConversions(GameSession session, Dictionary<int, double> raw, GameData data)
    {
        var before = StatCalculator.Compute(raw, data);
        bool changed = false;
        foreach (var (_, op) in Operations(session, data).Concat(CardOperations(session, data)))
        {
            long source, step, target, perStep, cap;
            if (op.Length == 6 && op[0] == 8)
                (source, step, target, perStep, cap) = (before[(int)op[1]], op[2], op[3], op[4], op[5]);
            else if (op.Length == 7 && op[0] == 14)
                (source, step, target, perStep, cap) = (before[(int)op[1]] + before[(int)op[2]], op[3], op[4], op[5], op[6]);
            else continue;
            double value = step <= 0 ? 0 : Math.Min(Math.Floor((double)source / step) * perStep, cap);
            raw[(int)target] = raw.GetValueOrDefault((int)target) + value;
            changed = true;
        }
        return changed ? StatCalculator.Compute(raw, data) : before;
    }

    /// <summary>
    /// Opcode 13's text describes chance/10000 and recovery bonus/10000. (inferred) Each consumed instant-recovery
    /// item rolls each active stunt independently; bonuses add on that item's HP or SP recovery, with the batch
    /// rounded down once as in the existing item-use path.
    /// Other item actions, periodic buffs and skills do not acquire this bonus.
    /// </summary>
    internal static long PotionRecovery(GameSession session, double perItem, int count, GameData data)
    {
        var effects = Operations(session, data).Select(x => x.Op).Where(op => op.Length == 3 && op[0] == 13).ToArray();
        double total = perItem * count;
        for (int i = 0; i < count; i++)
        {
            long bonus = 0;
            foreach (var op in effects)
                if (Random.Shared.Next(10000) < op[1]) bonus += op[2];
            total += perItem * bonus / 10000;
        }
        return (long)total;
    }
}
