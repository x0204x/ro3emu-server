using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Equipment work: 31404 strengthen and 31405 refine of an equipment grid (levels belong to the
/// grid, CharacterService.EquipLevels), 31409 craft from EquipCraftConfig, 31326 combine and 31327 dismantle.
/// Payments and resolved results commit together before their reply or dataset publication.
/// What the client data does not prove is marked inferred: the refine failure policy, craft quality pity.
/// </summary>
public sealed partial class EquipmentService
{
    public const uint ItemCombine = 31326, ItemDecompose = 31327;
    public const uint Strengthen = 31404, Refine = 31405, Craft = 31409;

    private const int MaxStrengthenBatch = 5; // UI_Sys_StrengthenWidgetCtrl batches up to five levels
    private const int FunctionCraft = 43;
    private const int ItemTypeEquip = 5;

    /// <summary>Committed crafts and paid refinement attempts, for task targets 103024/103006/103005.</summary>
    public List<Func<GameSession, int, int, int, Task>> Crafted { get; } = new();
    public List<Func<GameSession, Task>> Refined { get; } = new();

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly ItemStore _items;
    private readonly RoleStore _roles;
    private readonly ILogger<EquipmentService> _log;
    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> RefineRoll { get; set; } = bound => Random.Shared.Next(bound);
    // Likewise for the craft quality draw (a roll in [0, total weight)).
    internal Func<long, long> CraftRoll { get; set; } = bound => Random.Shared.NextInt64(bound);

    public EquipmentService(GameData data, CharacterService character, FunctionService functions, ItemStore items, RoleStore roles, ILogger<EquipmentService> log)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._items = items;
        this._roles = roles;
        this._log = log;
        character.AutoDecomposeReturns = AutoReturns;
    }

    // ---- strengthen / refine ----

    /// <summary>31404: +strengthenNum levels of a grid, paying every level's StrengthenTable._kDeplete; it cannot fail.</summary>
    public async Task StrengthenAsync(GameSession session, StrengthenEquipmentReq req)
    {
        using var gate = await CharacterService.Lock(session);
        int grid = (int)req.EquipPos, steps = Math.Clamp(req.StrengthenNum, 1, MaxStrengthenBatch);
        var levels = session.EquipLevels;
        int level = levels.Strength.GetValueOrDefault(grid);
        var cost = new List<(int, long)>();
        Fail? error = null;
        for (int next = level + 1; next <= level + steps && error == null; next++)
        {
            if (_character.StrengthenRow(grid, next) is not { } row) error = new(ErrorCodes.StrengthenParameter, $"no level {next}");
            else if (GameData.Int(row, "_iLev") > session.Progress!.Level) error = new(ErrorCodes.StrengthenParameter, $"level {next} needs base {GameData.Int(row, "_iLev")}");
            else cost.AddRange(Pairs(row, "_kDeplete"));
        }
        if (error == null)
        {
            var next = CopyLevels(levels);
            next.Strength[grid] = level + steps;
            if (await _character.ExchangeLockedAsync(session, cost, [], popup: false, sync: false,
                persist: tx =>
                {
                    tx.RoleBlob(session.Role!.Id, "equip_levels", JsonSerializer.SerializeToUtf8Bytes(next));
                    tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-strengthen", cost, [],
                        new { Grid = grid, Before = level, After = level + steps, Steps = steps }));
                }, committed: () => session.EquipLevels = next) == null)
                error = new(ErrorCodes.StrengthenMaterials, "payment short");
        }
        _log.LogInformation("{Name} strengthens grid {Grid} +{Steps} from {Level}: {Result}", session.Role!.Name, grid, steps, level, error?.Why ?? "ok");
        if (error == null) level += steps;
        // The reply before 5007: the client keeps the level the reply carries and judges success against the old one.
        await session.SendGameAsync(Strengthen, new StrengthenEquipmentRsp
        {
            // The level travels on failure too: the handler's nil fallback reads a wrong table (spec §3).
            Result = error?.Code ?? 0, EquipPos = req.EquipPos, StrengthenLv = level,
            StrengthenEffectLv = _character.Resonance(session, "StrengthenEffectTable"),
        });
        await AfterUpgradeAsync(session, error == null);
    }

    /// <summary>
    /// 31405: one refine attempt to the next level. Chance = RefiningTable._iSuccess + the grid's luck + the
    /// _kBlessingDeplete02 item's third value (LC_EquipmentHelper.GetRefineLuckyItemAddNum); _kBlessingDeplete01 adds
    /// nothing to the chance. Failure outside a safe level grants luck and may drop a level (rules below); success
    /// clears luck.
    /// </summary>
    public async Task RefineAsync(GameSession session, RefineEquipmentReq req)
    {
        using var owner = session.Owners is { } refineOwners
            ? await refineOwners.EnterAsync("equipment/refine", Ownership.ReservationKey.Role(session.Role!.Id)) : null;
        bool paid;
        using (await CharacterService.Lock(session))
        {
            int grid = (int)req.EquipPos;
            var levels = session.EquipLevels;
            int level = levels.Refine.GetValueOrDefault(grid), luck = levels.Luck.GetValueOrDefault(grid);
            var row = _character.RefineRow(grid, level + 1);
            Fail? error = row == null ? new(ErrorCodes.RefineParameter, "max level") : GameData.Int(row.Value, "_iLev") > session.Progress!.Level ? new(ErrorCodes.RefineParameter, "base level") : null;
            bool protect = false;
            long bonus = 0;
            var cost = new List<(int, long)>();
            if (error == null)
            {
                cost.AddRange(Pairs(row!.Value, "_kDeplete"));
                var guard = Flat(row.Value, "_kBlessingDeplete01");
                var lucky = Flat(row.Value, "_kBlessingDeplete02");
                foreach (var extra in req.ExtraItems)
                {
                    if (guard.Length >= 2 && extra.ItemEntry == guard[0] && extra.ItemCount == guard[1]) protect = true;
                    else if (lucky.Length >= 3 && extra.ItemEntry == lucky[0] && extra.ItemCount == lucky[1]) (bonus, protect) = (lucky[2], true); // Legend Ore: chance and no drop
                    else { error = new(ErrorCodes.RefineParameter, $"blessing {extra.ItemEntry}x{extra.ItemCount} not for this level"); break; }
                    cost.Add((extra.ItemEntry, extra.ItemCount));
                }
            }
            bool success = false;
            int successRoll = -1, demoteRoll = -1;
            var nextLevels = CopyLevels(levels);
            if (error == null)
            {
                long chance = GameData.Int(row!.Value, "_iSuccess") + luck + bonus;
                successRoll = RefineRoll(10000);
                success = successRoll < chance;
                if (success)
                {
                    nextLevels.Refine[grid] = level + 1;
                    nextLevels.Luck.Remove(grid);
                }
                else if (!(level == 0 || _character.RefineRow(grid, level) is { } current && GameData.Int(current, "_iSafeLev") != 0))
                {
                    // The client's own rules (Localization 精炼规则): failure may drop one level and grants luck; at a safe
                    // level (the current level's row has _iSafeLev) it does neither; Blessed / Legend Ore prevent the drop.
                    // The luck amount (_iSuccessNum) and drop chance (_iDemote) per attempt are inferred from the fields.
                    nextLevels.Luck[grid] = luck + (int)GameData.Int(row.Value, "_iSuccessNum");
                    if (!protect)
                    {
                        demoteRoll = RefineRoll(10000);
                        if (demoteRoll < GameData.Int(row.Value, "_iDemote")) nextLevels.Refine[grid] = level - 1;
                    }
                }
                if (await _character.ExchangeLockedAsync(session, cost, [], popup: false, sync: false,
                    persist: tx =>
                    {
                        tx.RoleBlob(session.Role!.Id, "equip_levels", JsonSerializer.SerializeToUtf8Bytes(nextLevels));
                        tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-refine", cost, [],
                            new
                            {
                                Grid = grid, Before = level, BeforeLuck = luck, After = nextLevels.Refine.GetValueOrDefault(grid),
                                AfterLuck = nextLevels.Luck.GetValueOrDefault(grid), Success = success, SuccessRoll = successRoll,
                                DemoteRoll = demoteRoll, Protect = protect, Bonus = bonus
                            }));
                    }, committed: () => session.EquipLevels = nextLevels) == null)
                    error = new(ErrorCodes.RefineMaterials, "payment short");
            }
            levels = session.EquipLevels;
            _log.LogInformation("{Name} refines grid {Grid} +{Level}: {Result}", session.Role!.Name, grid, level, error?.Why ?? (success ? "success" : $"failed, now +{levels.Refine.GetValueOrDefault(grid)}"));
            await session.SendGameAsync(Refine, new RefineEquipmentRsp
            {
                Result = error?.Code ?? 0, EquipPos = req.EquipPos, RefineLv = levels.Refine.GetValueOrDefault(grid),
                RefineEffectLv = _character.Resonance(session, "RefiningEffectTable"), RefineLuckyAdd = levels.Luck.GetValueOrDefault(grid),
            });
            await AfterUpgradeAsync(session, error == null);
            paid = error == null;
        }
        // A paid failed roll counts as an operation (inferred); refused/unpaid requests never do.
        // Refined handlers may reenter CharacterService.Lock (for example season refinement).
        if (paid) foreach (var handler in Refined) await handler(session);
    }

    /// <summary>
    /// After a strengthen / refine reply: the paid bag (5009), then the grid levels (5007) and properties. Both after
    /// the reply: LC_DAL_Equipment plays success only for a level above the stored one (an earlier 5007 would have
    /// raised it), and the refine panel redraws on the currency change (UI_Sys_StrengthenRefineInfoWidgetCtrl binds
    /// m_kCurrency to _refreshView), which must therefore come after the reply stored the new level.
    /// </summary>
    private async Task AfterUpgradeAsync(GameSession session, bool paid)
    {
        if (!paid) return;
        await _character.ItemsChangedAsync(session, stats: false);
        await _character.EquipLevelsChangedAsync(session, persist: false);
    }

    private static EquipLevels CopyLevels(EquipLevels source) => new()
    {
        Strength = new(source.Strength), Refine = new(source.Refine), Luck = new(source.Luck),
    };

    // ---- craft ----

    private const int QualityOrange = 5, QualityRed = 6;

    /// <summary>
    /// 31409: EquipCraftConfig[rank, subtype] pays _kCost and makes one of _kQualityEquipID, drawn by
    /// EquipCraftQualityConfig._iWeight of the rank. World-level availability (_iCondition) is not gated: no world level
    /// yet. Quality pity: every row carries _iOrangeGuarantee (11) and _iRedGuarantee (300), and DBEquipInfo keeps the
    /// counters (craftInfo: DBEquipCraftInfo rank, equipType, orangeGuarantee, redGuarantee, craftCount; CraftRankCount)
    /// in 5007; no client Lua reads them. (inferred) craftInfo is keyed by the EquipCraftConfig row id and
    /// CraftRankCount by rank; a counter counts the crafts since its guarantee last applied, and the craft that
    /// reaches the threshold is drawn only from the recipe's outputs of at least that quality (orange 5, red 6),
    /// by their weights, or evenly when they all weigh 0 at the rank. Any result of at least orange resets the orange
    /// counter and any red result resets the red one, whether drawn naturally or forced (the usual pity semantics; a red
    /// result is also orange-or-better). A recipe without such an output does not count toward it.
    /// </summary>
    public async Task CraftAsync(GameSession session, EquipCraftReq req)
    {
        using var owner = session.Owners is { } craftOwners
            ? await craftOwners.EnterAsync("equipment/craft", Ownership.ReservationKey.Role(session.Role!.Id)) : null;
        InvItem? made;
        using (await CharacterService.Lock(session))
        {
            var recipe = _data.Table("EquipCraftConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iRank") == req.Rank && GameData.Int(r, "_iEquipType") == req.Subtype);
            Fail? error = recipe.ValueKind != JsonValueKind.Object ? new(ErrorCodes.CraftParameter, "no recipe")
                : !_functions.IsOpen(session, FunctionCraft) ? new(ErrorCodes.FeatureUnavailable, "craft closed") : null;
            made = null;
            if (error == null)
            {
                var outputs = Pairs(recipe, "_kQualityEquipID") // [quality, item]
                    .Select(o => (Quality: o.Entry, Entry: (int)o.Count, Weight: WeightOf(req.Rank, o.Entry))).ToList();
                int recipeId = (int)GameData.Int(recipe, "_iID");
                var counter = session.EquipmentPlans.CraftInfo.TryGetValue(recipeId, out var held) ? held.Clone()
                    : new DBEquipCraftInfo { Rank = req.Rank, EquipType = req.Subtype };
                long orangeAt = GameData.Int(recipe, "_iOrangeGuarantee"), redAt = GameData.Int(recipe, "_iRedGuarantee");
                bool canOrange = orangeAt > 0 && outputs.Any(o => o.Quality >= QualityOrange);
                bool canRed = redAt > 0 && outputs.Any(o => o.Quality >= QualityRed);
                bool redDue = canRed && counter.RedGuarantee + 1 >= redAt;
                bool orangeDue = canOrange && counter.OrangeGuarantee + 1 >= orangeAt;
                int floor = redDue ? QualityRed : orangeDue ? QualityOrange : 0;
                var pool = outputs.Where(o => o.Quality >= floor).ToList();
                var weighted = pool.Where(o => o.Weight > 0).Select(o => (o.Entry, o.Weight)).ToList();
                if (weighted.Count == 0 && floor > 0) weighted = pool.Select(o => (o.Entry, Weight: 1L)).ToList();
                int entry = Draw(weighted);
                long quality = outputs.FirstOrDefault(o => o.Entry == entry).Quality;
                counter.CraftCount++;
                if (canRed) counter.RedGuarantee = quality >= QualityRed ? 0 : counter.RedGuarantee + 1;
                if (canOrange) counter.OrangeGuarantee = quality >= QualityOrange ? 0 : counter.OrangeGuarantee + 1;
                var next = session.EquipmentPlans.Clone();
                ApplyCraftCounter(next, recipeId, counter);
                if (entry == 0) error = new(ErrorCodes.CraftParameter, "no output");
                else if (await _character.ExchangeLockedAsync(session, Pairs(recipe, "_kCost"), [(entry, 1)], popup: false,
                    persist: tx =>
                    {
                        tx.RoleBlob(session.Role!.Id, "equip_plans", next.ToByteArray());
                        tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-craft",
                            Pairs(recipe, "_kCost"), [(entry, 1)], new { req.Rank, req.Subtype, Entry = entry }));
                    },
                    // A same-commit prune of stale plan GUIDs (never caused by craft materials) keeps its plans;
                    // the counters apply on top of whatever plans the commit published.
                    committed: () =>
                    {
                        var current = session.EquipmentPlans.Clone();
                        ApplyCraftCounter(current, recipeId, counter);
                        session.EquipmentPlans = current;
                    })
                    is not { Count: > 0 } items) error = new(ErrorCodes.CraftMaterialsShort, "payment short");
                else made = items[0];
            }
            _log.LogInformation("{Name} crafts rank {Rank} subtype {Subtype}: {Result}", session.Role!.Name, req.Rank, req.Subtype, error?.Why ?? $"item {made!.Entry}");
            var resp = new EquipCraftResp { Result = error?.Code ?? 0 };
            if (made != null) resp.Item = _character.ItemInfoOf(made);
            await session.SendGameAsync(Craft, resp);
            if (made != null) await _character.SyncEquipmentAsync(session);
        }
        if (made != null) foreach (var handler in Crafted) await handler(session, made.Entry, req.Rank, req.Subtype);
    }

    private static void ApplyCraftCounter(DBEquipInfo info, int recipeId, DBEquipCraftInfo counter)
    {
        long previous = info.CraftInfo.TryGetValue(recipeId, out var old) ? old.CraftCount : 0;
        info.CraftInfo[recipeId] = counter.Clone();
        info.CraftRankCount[counter.Rank] = info.CraftRankCount.GetValueOrDefault(counter.Rank) + counter.CraftCount - previous;
    }

    private long WeightOf(int rank, long quality) =>
        _data.Table("EquipCraftQualityConfig").Values.Where(r => GameData.Int(r, "_iRank") == rank && GameData.Int(r, "_iQuality") == quality)
            .Select(r => GameData.Int(r, "_iWeight")).FirstOrDefault();

    private int Draw(List<(int Entry, long Weight)> outputs)
    {
        long total = outputs.Sum(o => o.Weight);
        if (total <= 0) return 0;
        long roll = CraftRoll(total);
        foreach (var (entry, weight) in outputs)
        {
            if (roll < weight) return entry;
            roll -= weight;
        }
        return outputs[^1].Entry;
    }

    // ---- combine / dismantle ----

    /// <summary>31326: itemCount of ItemConfig[itemId], paying _kCombine per unit; the client's costItem must agree.</summary>
    public async Task CombineAsync(GameSession session, ItemCombineReq req)
    {
        using var gate = await CharacterService.Lock(session);
        var row = _data.Row("ItemConfig", req.ItemId);
        var cost = row is { } r ? Pairs(r, "_kCombine").Select(c => (c.Entry, c.Count * (long)req.ItemCount)).ToList() : [];
        Fail? error = row == null ? new(ErrorCodes.ItemNoConfig, "no item") : cost.Count == 0 ? new(ErrorCodes.Parameter, "not combinable")
            : req.ItemCount < 1 ? new(ErrorCodes.Parameter, "bad count")
            : req.CostItem.Count > 0 && !req.CostItem.OrderBy(c => c.Key).Select(c => ((int)c.Key, (long)c.Value)).SequenceEqual(cost.OrderBy(c => c.Entry)) ? new(ErrorCodes.Parameter, "cost differs")
            : null;
        if (error == null && await _character.ExchangeLockedAsync(session, cost, [(req.ItemId, req.ItemCount)], popup: false,
            persist: tx => tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-combine", cost,
                [(req.ItemId, req.ItemCount)], new { req.ItemId, req.ItemCount }))) == null)
            error = new(ErrorCodes.ItemsShort, "materials short");
        _log.LogInformation("{Name} combines {Count}x {Item}: {Result}", session.Role!.Name, req.ItemCount, req.ItemId, error?.Why ?? "ok");
        await session.SendGameAsync(ItemCombine, new ItemCombineResp { Result = error?.Code ?? 0 });
    }

    /// <summary>
    /// 31327: bag instances (guid -> count) into their returns: EquipReturnConfig by rank / quality / entries / specials
    /// for gear, otherwise ItemConfig._kDecompose (Lua_CFG_EquipReturnConfig falls back
    /// to it too). An item without returns cannot be dismantled.
    /// </summary>
    public async Task DecomposeAsync(GameSession session, ItemDecomposeReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var gains = new Dictionary<int, long>();
        var instances = new Dictionary<long, int>();
        // (inferred) Enforce the client function-2602 gate at the authoritative operation too.
        // The client supplies isLapseDecompose, but item season/playBook are not
        // represented in InvItem yet. A lapse return must not silently pay the ordinary _kDecompose yield.
        Fail? error = req.IsLapseDecompose ? new(ErrorCodes.FeatureUnavailable, "season-lapse return is not qualified")
            : !_functions.IsOpen(session, FunctionDecompose) ? new(ErrorCodes.FeatureUnavailable, "dismantle closed")
            : req.DecomposeItem.Count == 0 ? new(ErrorCodes.NothingToDismantle, "nothing") : null;
        foreach (var (guid, count) in req.DecomposeItem)
        {
            if (error != null) break;
            if (session.Items.FirstOrDefault(i => i.Guid == guid) is not { } item) { error = new(ErrorCodes.ItemMissing, $"no item {guid}"); break; }
            if (item.IsLocked) { error = new(ErrorCodes.ItemLocked, $"{guid} is protected"); break; }
            if (item.Grid != 0) { error = new(ErrorCodes.CannotDismantleEquipped, $"{guid} is worn or socketed"); break; }
            if (_data.Row("ItemConfig", item.Entry) is { } seasonRow && GameData.Int(seasonRow, "_iSeasonResetType") != 0)
            { error = new(ErrorCodes.FeatureUnavailable, $"{item.Entry} requires authoritative season state"); break; }
            var returns = Returns(item);
            if (returns.Count == 0) { error = new(ErrorCodes.Parameter, $"{item.Entry} has no returns"); break; }
            if (count < 1 || count > item.Stack) { error = new(count < 1 ? ErrorCodes.Parameter : ErrorCodes.ItemsShort, $"bad count {count}"); break; }
            instances[guid] = count;
            foreach (var (entry, amount) in returns)
            {
                long total = gains.GetValueOrDefault(entry) + amount * count;
                if (total is < 1 or > int.MaxValue) { error = new(ErrorCodes.Parameter, "return count overflow"); break; }
                gains[entry] = total;
            }
        }
        var goods = gains.Select(g => (g.Key, (int)g.Value)).ToList();
        var inputs = error == null
            ? instances.Select(g => (session.Items.Single(i => i.Guid == g.Key).Entry, (long)g.Value)).ToList()
            : [];
        if (error == null && await _character.ExchangeLockedAsync(session, [], goods, instances, popup: false,
            persist: tx => tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-decompose", inputs, goods,
                new { Inputs = instances.OrderBy(p => p.Key).ToArray(), Goods = goods.OrderBy(g => g.Key).ToArray() }))) == null)
            error = new(ErrorCodes.ItemsShort, "bad count");
        _log.LogInformation("{Name} dismantles {Items}: {Result}", session.Role!.Name, string.Join(",", req.DecomposeItem.Keys), error?.Why ?? "ok");
        await session.SendGameAsync(ItemDecompose, new ItemDecomposeResp { Result = error?.Code ?? 0 });
    }

    private List<(int Entry, long Count)> Returns(InvItem instance)
    {
        if (_data.Row("ItemConfig", instance.Entry) is not { } item) return [];
        if (GameData.Int(item, "_iType") == ItemTypeEquip && GameData.Int(item, "_iEquipId") is var equipId and > 0 && _data.Row("EquipConfig", equipId) is { } equip)
        {
            long rank = GameData.Int(equip, "_iRank"), quality = GameData.Int(item, "_iQuality");
            var row = _data.Table("EquipReturnConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iRank") == rank && GameData.Int(r, "_iQuality") == quality
                && GameData.Int(r, "_iEntriesNum") == EntryCount(instance) && GameData.Int(r, "_iSpecialNum") == SpecialCount(instance));
            if (row.ValueKind == JsonValueKind.Object && Pairs(row, "_kReturn") is { Count: > 0 } back) return back;
        }
        return Pairs(item, "_kDecompose");
    }

    /// <summary>A refusal: the ErrorCodes result for the client and the reason for the log.</summary>
    private readonly record struct Fail(int Code, string Why);

    // ---- config helpers ----

    private static List<(int Entry, long Count)> Pairs(JsonElement row, string field) =>
        GameData.Pairs(row, field).Select(p => ((int)p.Item1, (long)p.Item2)).ToList();

    /// <summary>A flat number array field ([item, count, bonus]); empty for {} / absent.</summary>
    private static long[] Flat(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => (long)(GameData.Number(e) ?? 0)).ToArray() : [];
}
