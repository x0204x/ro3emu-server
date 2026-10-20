using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ordinary equipment attribute enchant: 31415 identifies owned gear and a particular scroll stack.
/// Enchantment._iItem maps the scroll to the installed outer-map id; EnchantIDConfig supplies [stat, min, max].
/// Bag gear uses 5009, worn gear 5007; only worn enchantments contribute to 31008/31048 character properties.
/// </summary>
public sealed class EnchantService(GameData data, CharacterService character, FunctionService functions, ItemStore items, ILogger<EnchantService> log)
{
    private readonly ItemStore _items = items;
    public const uint Enchant = 31415;

    /// <summary>
    /// function 7/83, appraisal, slots, scroll type, equipment position and star/level checks.
    /// (inferred) All validation, payment and the mutation share StateLock, including GUID-specific consumption.
    /// Refusal-code selection follows ErrorCodeConfig's meanings; the original server mapping is unverified.
    /// </summary>
    public async Task EnchantAsync(GameSession session, EnchantReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var equip = session.Items.FirstOrDefault(i => i.Guid == req.EquipGuid);
        var scroll = session.Items.FirstOrDefault(i => i.Guid == req.ItemGuid && i.Grid == 0);
        var gearRow = equip == null ? null : data.Row("ItemConfig", equip.Entry);
        var scrollRow = scroll == null ? null : data.Row("ItemConfig", scroll.Entry);
        var enchantRow = scroll == null ? default : data.Table("Enchantment").Values.FirstOrDefault(r => Flat(r, "_iItem").Contains(scroll.Entry));
        int slots = gearRow is { } gear ? (int)GameData.Int(gear, "_iEnchantSlot") : 0;
        int result = !functions.IsOpen(session, 7) || !functions.IsOpen(session, 83) ? ErrorCodes.FeatureUnavailable
            : equip == null || scroll == null ? ErrorCodes.ItemMissing
            : gearRow is not { } g || GameData.Int(g, "_iType") != 5 ? ErrorCodes.NotEquipment
            : equip.Grid < 0 || equip.Stack != 1 ? ErrorCodes.EnchantParameter
            : !CharacterService.IsAppraised(equip) ? ErrorCodes.EquipNotAppraised
            : slots <= 0 || equip.Enchantments.Count > slots ? ErrorCodes.EnchantParameter
            : scroll.Stack < 1 ? ErrorCodes.EnchantMaterials
            : scrollRow is not { } s || GameData.Int(s, "_iType") != 9 || GameData.Int(s, "_iSubType") != 931 ? ErrorCodes.EnchantParameter
            : enchantRow.ValueKind != JsonValueKind.Object ? ErrorCodes.EnchantParameter
            : !Flat(enchantRow, "_kEquipType").Contains(GameData.Int(g, "_iEquipPart")) ? ErrorCodes.EnchantParameter
            : StarLevel(GameData.Int(enchantRow, "_iStars")) is not > 0 || GameData.Int(g, "_iLevelNeed") > StarLevel(GameData.Int(enchantRow, "_iStars")) ? ErrorCodes.EnchantParameter
            // The current export also has 28 skill-only rows, more than the documented summary listed.
            // Their special effects are not implemented; an empty attr map is unsafe for LC_ItemProtoParse:303-317.
            : Flat(enchantRow, "_iEnchantIDs").Length == 0 && Flat(enchantRow, "_iSkills").Length > 0 ? ErrorCodes.FeatureUnavailable
            : 0;

        int enchantId = 0;
        if (result == 0)
        {
            var price = Flat(enchantRow, "_kEnchanCost");
            var rolled = Roll(enchantRow);
            var replacement = Replacement(equip!, enchantRow, slots);
            if (price.Length != 2 || price[0] <= 0 || price[0] > int.MaxValue || price[1] < 0
                || data.Row("ItemConfig", price[0]) == null || rolled == null || replacement == null)
                result = ErrorCodes.EnchantParameter;
            else
            {
                var cost = new[] { ((int)price[0], price[1]) };
                var candidate = equip!.Clone();
                int installed = (int)GameData.Int(enchantRow, "_iID");
                // (inferred) Monotonic Unix seconds preserve a definite display order even for rapid rerolls.
                rolled.GetTime = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), candidate.Enchantments.Values.Select(e => e.GetTime).DefaultIfEmpty(0).Max() + 1);
                if (replacement.Value != 0) candidate.Enchantments.Remove(replacement.Value);
                candidate.Enchantments[installed] = rolled;
                // No sync inside the exchange: the paid bag and updated item are published together below.
                try
                {
                    if (await character.ExchangeLockedAsync(session, cost, [], new Dictionary<long, int> { [scroll!.Guid] = 1 }, popup: false, sync: false,
                        persist: transaction =>
                        {
                            transaction.WithEconomy(character.FamilyEconomy(session, "cd13a/enchant-apply",
                                cost.Concat([(scroll.Entry, 1L)]).ToArray(), [],
                                new
                                {
                                    Equip = equip.Guid, Scroll = scroll.Guid, Installed = installed,
                                    Replaced = replacement.Value, Attr = rolled.Attr, Time = rolled.GetTime
                                }));
                        }, stageItems: staged => staged.Single(i => i.Guid == equip.Guid).Enchantments = candidate.Enchantments) == null)
                        result = ErrorCodes.EnchantMaterials;
                }
                catch (SqliteException e)
                {
                    log.LogError(e, "Enchant persistence failed for {Name}", session.Role!.Name);
                    result = ErrorCodes.Unknown;
                }
                if (result == 0)
                {
                    enchantId = installed;
                    await character.ItemsChangedAsync(session, stats: false);
                    if (equip.Grid > 0) await character.EquipmentChangedAsync(session);
                }
            }
        }

        log.LogInformation("{Name} enchants {Equip} with {Scroll}: {Result}", session.Role!.Name, req.EquipGuid, req.ItemGuid, result);
        // (inferred) enchantId names the installed Enchantment row; type = 1 on success has no proven client meaning.
        await session.SendGameAsync(Enchant, new EnchantResp { Result = result, EnchantId = enchantId, Type = result == 0 ? 1 : 0 });
        if (result == 0 && equip!.Grid > 0) await character.RefreshStatsAsync(session);
    }

    /// <summary>
    /// (inferred) Replace the same group (or an existing unique for a unique scroll); otherwise append, or uniformly
    /// evict one entry if full. UI intent: UI_Sys_Strengthen_EnchantmentsWidgetCtrl.lua:1113-1174,1281-1348,1454-1558.
    /// A contradictory saved map or unknown row is refused; 0 means a free slot, null an invalid map.
    /// </summary>
    private int? Replacement(InvItem item, JsonElement row, int slots)
    {
        long group = GameData.Int(row, "_iEquipGroup");
        bool unique = GameData.Int(row, "_iIfUnique") == 1;
        var conflicts = new List<int>();
        foreach (int id in item.Enchantments.Keys)
        {
            if (data.Row("Enchantment", id) is not { } old) return null;
            if (GameData.Int(old, "_iEquipGroup") == group || unique && GameData.Int(old, "_iIfUnique") == 1) conflicts.Add(id);
        }
        if (conflicts.Count > 1) return null;
        if (conflicts.Count == 1) return conflicts[0];
        return item.Enchantments.Count < slots ? 0 : item.Enchantments.Keys.Order().ElementAt(Random.Shared.Next(item.Enchantments.Count));
    }

    /// <summary>
    /// EnchantIDConfig._kAttrs is one [attribute, min, max] triple (Lua_CFG_EnchantIDConfig.lua:9-41).
    /// (inferred) Choose a template uniformly (current rows have exactly one), then an inclusive uniform integer.
    /// Reject malformed templates before payment so the client never sees a present enchant with an empty attr map.
    /// </summary>
    private ItemEnchantment? Roll(JsonElement row)
    {
        var templates = Flat(row, "_iEnchantIDs");
        if (templates.Length == 0) return null;
        var ranges = new List<long[]>();
        foreach (long id in templates)
        {
            if (data.Row("EnchantIDConfig", id) is not { } template) return null;
            var values = Flat(template, "_kAttrs");
            if (values.Length != 3 || values[0] <= 0 || values[0] > int.MaxValue
                || values[1] < int.MinValue || values[2] > int.MaxValue || values[1] > values[2]) return null;
            ranges.Add(values);
        }
        var range = ranges[Random.Shared.Next(ranges.Count)];
        return new ItemEnchantment { Attr = { [(int)range[0]] = (int)Random.Shared.NextInt64(range[1], range[2] + 1) } };
    }

    /// <summary>PublicParamConfig.EnchantStarsLevel (Config/ComplexConfig/PublicParamConfig.lua:5951-5995).</summary>
    private long StarLevel(long stars)
    {
        if (data.Table("PublicParamConfig").TryGetValue("EnchantStarsLevel", out var levels) && levels.ValueKind == JsonValueKind.Array)
            foreach (var pair in levels.EnumerateArray())
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 && GameData.Number(pair[0]) == stars)
                    return (long)(GameData.Number(pair[1]) ?? 0);
        return 0;
    }

    private static long[] Flat(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}
