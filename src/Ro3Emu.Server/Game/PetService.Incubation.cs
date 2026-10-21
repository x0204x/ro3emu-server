using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class PetService
{
    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();
    private JsonElement? Room(DBPetInfo state) => _data.Row("PetIncubationRoomConfig", state.IncubationLv);

    /// <summary>
    /// Physical slot unlocks accumulate by room level. Each round keeps its own egg per slot: DBSlotInfo maps
    /// round -> egg and the client reads LC_PetHelper slot data by round, showing a slot free in one pool while
    /// the other pool's egg sits there.
    /// </summary>
    private bool EggSlotOpen(DBPetInfo state, long slot) => slot > 0 && _data.Table("PetIncubationRoomConfig").Values
        .Any(r => GameData.Int(r, "_iLv") <= state.IncubationLv && Flat(r, "_iUnlockSlot").Contains(slot));

    /// <summary>LC_PetHelper.GetSlotRemainingTime: room duration minus elapsed + acceleration, all in seconds.</summary>
    internal long Remaining(DBPetInfo state, DBIncubateInfo egg) => Room(state) is { } room
        ? Math.Max(0, GameData.Int(room, "_iIncubateTime") - Math.Max(0, Now - egg.IncubateTime) - egg.AccTime)
        : long.MaxValue;

    private DBIncubateInfo? Egg(DBPetInfo state, long round, long slot) =>
        state.IncubationSlot.TryGetValue(slot, out var s) ? s.IncubateInfo.GetValueOrDefault(round) : null;

    /// <summary>Removes one round's egg; the slot entry goes only when no other round's egg remains in it.</summary>
    private static void RemoveEgg(DBPetInfo state, long round, long slot)
    {
        if (!state.IncubationSlot.TryGetValue(slot, out var s)) return;
        s.IncubateInfo.Remove(round);
        if (s.IncubateInfo.Count == 0) state.IncubationSlot.Remove(slot);
    }

    private long PetParam(string name, long fallback) => _data.Table("PublicParamConfig").TryGetValue(name, out var v)
        ? (long)(GameData.Number(v) ?? fallback) : fallback;

    /// <summary>
    /// A round takes new eggs (<paramref name="place"/>) only while the client offers it: LC_PetHelper.GetActivePoolTypeInfo
    /// shows, per _iSlotType, a round whose PetIncubationSlotConfig._iActivity is open (LC_ActivityHelper.IsActivityOpen),
    /// which is this session's NewActivity projection. Those activities follow TimeLimitConfig 1065-1073: server-open
    /// days 0-7, 7-14, 14-21, 21-28 and 28-170 for the Permanent rounds 10001/03/05/07/09, days 10-14, 14-21, 21-28
    /// and 28-170 for the Limited-Time rounds 10002/04/06/08. OpenContent makes them perpetual, so the client then
    /// shows 10009 and 10008; OpenContent:ClosedActivities closes a pool. An egg already placed stays acceleratable
    /// and collectable after its round closes.
    /// </summary>
    private int RoundGate(GameSession s, long round, bool place = false)
    {
        int code = Gate(s, incubation: true);
        if (code != 0) return code;
        if (_data.Row("PetIncubationSlotConfig", round) is not { } row) return ErrorCodes.Parameter;
        return !place || RoundOpen(s, row) ? 0 : ErrorCodes.ActivityClosed;
    }

    private bool RoundOpen(GameSession s, JsonElement round)
    {
        long now = Now;
        return s.NewActivities?.ActivityInfo.TryGetValue(GameData.Int(round, "_iActivity"), out var activity) == true
            && !activity.IsShielded && activity.State != "_close" && now >= activity.CurStartTime && now < activity.CurEndTime;
    }

    /// <summary>
    /// 33316: the client has no 5017 field for incubationLvUpTime. Completing the affordable
    /// upgrade immediately is an inferred, representable policy; level/EXP/cost are client config.
    /// </summary>
    public async Task RoomLevelAsync(GameSession s)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s, incubation: true);
        var step = code == 0 ? RoomStep(s.Pets!) : null;
        if (code == 0 && step == null) code = ErrorCodes.PetOperation;
        if (code == 0)
        {
            var (next, cost) = step!.Value;
            if (!await ExchangeAsync(s, "room-level", next, cost)) code = _character.ShortageOf(s, cost);
            else
            {
                await ChangedAsync(s, items: cost.Count > 0);
                if (cost.Count == 0) foreach (var handler in _character.StateChanged) handler(s); // 306004
            }
        }
        await s.SendGameAsync(RoomLevelUp, new PetIncubationLvUpRes { Result = code });
    }

    /// <summary>One room level: the current row's _iExp and _kCost buy the next row. Null while the bar is not full.</summary>
    private (DBPetInfo Next, List<(int, long)> Cost)? RoomStep(DBPetInfo state)
    {
        if (Room(state) is not { } room || _data.Row("PetIncubationRoomConfig", state.IncubationLv + 1) == null
            || state.IncubationExp < GameData.Int(room, "_iExp")) return null;
        var next = state.Clone();
        next.IncubationExp -= GameData.Int(room, "_iExp");
        next.IncubationLv++;
        return (next, Cost(room, "_kCost"));
    }

    /// <summary>
    /// The client never sends 33316 (no UI caller of LC_DAL_Pet.SendIncubationLvUp), so a full
    /// proficiency bar levels the room after a collect, as in the reference video. Each affordable
    /// step is its own exchange; an unaffordable cost leaves the bar full (inferred policy).
    /// </summary>
    private async Task<bool> AutoRoomLevelAsync(GameSession s)
    {
        bool levelled = false;
        while (RoomStep(s.Pets!) is { } step && await ExchangeAsync(s, "room-level", step.Next, step.Cost)) levelled = true;
        // A new room level: quest targets read it (growth-manual 306004).
        if (levelled) foreach (var handler in _character.StateChanged) handler(s);
        return levelled;
    }

    /// <summary>
    /// 33318: material 1250709 from _kMaterial, seconds clock, one persisted outcome before any reply (§3).
    /// Uses DrawConfig/DrawPoolConfig, not preview lists. Original pity/room-quality mixing is unverified.
    /// </summary>
    public async Task GenerateAsync(GameSession s, PetGenEggReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = RoundGate(s, req.RoundId, place: true);
        if (code == 0 && (!EggSlotOpen(s.Pets!, req.SlotId) || Egg(s.Pets!, req.RoundId, req.SlotId) != null)) code = ErrorCodes.PetOperation;
        if (code == 0)
        {
            var config = _data.Row("PetIncubationSlotConfig", req.RoundId)!.Value;
            var lottery = s.Lottery?.Clone();
            var result = Draw(s.Pets!, config, lottery);
            var cost = Cost(config, "_kMaterial");
            if (result == null || cost.Count == 0) code = ErrorCodes.FeatureUnavailable;
            else
            {
                var next = s.Pets!.Clone();
                if (!next.IncubationSlot.TryGetValue(req.SlotId, out var slot))
                    next.IncubationSlot[req.SlotId] = slot = new DBSlotInfo { SlotId = req.SlotId };
                slot.IncubateInfo[req.RoundId] = result;
                if (!await ExchangeAsync(s, "generate-egg", next, cost, lottery: lottery)) code = _character.ShortageOf(s, cost);
                else await ChangedAsync(s, items: true);
            }
        }
        await s.SendGameAsync(GenerateEgg, new PetGenEggRes { Result = code });
    }

    /// <summary>
    /// Draw index = min(roomLv+1, length), as UI_Sys_PetGachaWidgetCtrl:613–650 (Lua 1-based).
    /// Use DrawConfig._kSubBanner groups and DrawPoolConfig._iWeight to select one row, then one _kItem tuple uniformly
    /// (inferred allocation). Do not reinterpret draw IDs as DropPool IDs.
    /// The S-class guarantee follows UI_Sys_PetGachaWidgetCtrl.RefreshPetHit: remaining = DrawConfig._iGuaranteedCnt minus
    /// the AppearanceLotteryPoolPityInfo pity count keyed by DrawConfig._iType for the Normal (_iSlotType 1) pool and by the
    /// draw id otherwise. Like LotteryService, the guaranteed draw rolls only _kGuaranteedBanner groups and any hit in them
    /// resets the count; counting at egg placement (when the outcome is fixed) is inferred.
    /// </summary>
    private DBIncubateInfo? Draw(DBPetInfo state, JsonElement slot, DBLotteryData? lottery)
    {
        var draws = Flat(slot, "_kDrawIds");
        if (draws.Length == 0) return null;
        long id = draws[(int)Math.Clamp(state.IncubationLv, 0, draws.Length - 1)];
        if (_data.Row("DrawConfig", id) is not { } draw) return null;
        var groups = Flat(draw, "_kSubBanner").ToHashSet();
        var guaranteed = Flat(draw, "_kGuaranteedBanner").ToHashSet();
        long guarantee = GameData.Int(draw, "_iGuaranteedCnt");
        LotteryPityRecord? pity = null;
        if (lottery != null && guarantee > 0 && guaranteed.Count > 0)
        {
            int key = checked((int)(GameData.Int(slot, "_iSlotType") == 1 ? GameData.Int(draw, "_iType") : id));
            if (!lottery.PityRecords.TryGetValue(key, out pity)) lottery.PityRecords[key] = pity = new LotteryPityRecord();
        }
        bool forced = pity != null && pity.PityCount + 1 >= guarantee;
        var picked = Roll(forced ? guaranteed : groups) ?? (forced ? Roll(groups) : null);
        if (picked is not { } outcome) return null;
        var (group, type, item, count) = outcome;
        if (pity != null)
        {
            bool hit = guaranteed.Contains(group);
            pity.TotalCount++;
            pity.PityCount = hit ? 0 : pity.PityCount + 1;
            pity.MustCount = checked((int)guarantee);
            if (forced && hit) pity.HitPityCount++;
        }
        return new DBIncubateInfo { IncubateTime = Now, AwardType = type, AwardId = item, AwardCount = count };
    }

    private (long Group, int Type, int Item, int Count)? Roll(IReadOnlySet<long> groups)
    {
        var rows = _data.Table("DrawPoolConfig").Values.Where(r => groups.Contains(GameData.Int(r, "_iGroupID"))
            && GameData.Int(r, "_iWeight") > 0 && Outcomes(r).Count > 0).ToList();
        long total = rows.Sum(r => GameData.Int(r, "_iWeight"));
        if (total <= 0) return null;
        long roll = Random.Shared.NextInt64(total);
        foreach (var row in rows)
        {
            roll -= GameData.Int(row, "_iWeight");
            if (roll >= 0) continue;
            var candidates = Outcomes(row);
            var (type, item, count) = candidates[Random.Shared.Next(candidates.Count)];
            return (GameData.Int(row, "_iGroupID"), type, item, count);
        }
        return null;
    }

    private List<(int Type, int Item, int Count)> Outcomes(JsonElement row)
    {
        var result = new List<(int, int, int)>();
        if (!row.TryGetProperty("_kItem", out var items) || items.ValueKind != JsonValueKind.Array) return result;
        foreach (var tuple in items.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() != 3) continue;
            int type = (int)(GameData.Number(tuple[0]) ?? 0), item = (int)(GameData.Number(tuple[1]) ?? 0), count = (int)(GameData.Number(tuple[2]) ?? 0);
            if (count <= 0 || _data.Row("PetIncubationResultConfig", item) == null) continue;
            if (type == 1 && count == 1 && _data.Row("PetBaseConfig", item) != null && _stars.ContainsKey((item, 1, 0))
                || type == 2 && _data.Row("ItemConfig", item) != null) result.Add((type, item, count));
        }
        return result;
    }

    /// <summary>
    /// 33322: UI_Sys_Gacha_IncubatorWidgetCtrl:956–1093 confirms finishing with owned 300-second items, purchasing
    /// missing units for token 3 x100. Debit the whole finish atomically (inferred server policy); ready retries cost 0.
    /// </summary>
    public async Task AccelerateAsync(GameSession s, PetEggAccReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = RoundGate(s, req.RoundId);
        var egg = s.Pets == null ? null : Egg(s.Pets, req.RoundId, req.SlotId);
        if (code == 0 && egg == null) code = ErrorCodes.PetOperation;
        if (code == 0 && Remaining(s.Pets!, egg!) is var remaining and > 0)
        {
            long need = (remaining + 299) / 300, have = Math.Min(need, _character.CountOf(s, 1250724));
            var cost = new List<(int, long)>();
            if (have > 0) cost.Add((1250724, have));
            if (need > have) cost.Add((3, checked((need - have) * 100)));
            var next = s.Pets!.Clone();
            Egg(next, req.RoundId, req.SlotId)!.AccTime += need * 300;
            if (!await ExchangeAsync(s, "accelerate-egg", next, cost)) code = _character.ShortageOf(s, cost);
            else await ChangedAsync(s, items: true);
        }
        await s.SendGameAsync(AccelerateEgg, new PetEggAccRes { Result = code });
    }

    /// <summary>
    /// 33319: validate every requested slot first, then one atomic grant/removal. Duplicates and early/empty slots refuse.
    /// Duplicate pets convert using PetBaseConfig._kDecompose; result EXP once per egg and history at collection are
    /// inferred. New pets travel only by 5017 (no duplicate 33309 append).
    /// </summary>
    public async Task CollectAsync(GameSession s, PetEggRewardReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = RoundGate(s, req.RoundId);
        if (code == 0 && (req.SlotIds.Count == 0 || req.SlotIds.Count > 5 || req.SlotIds.Distinct().Count() != req.SlotIds.Count)) code = ErrorCodes.Parameter;
        if (code == 0 && req.SlotIds.Any(slot => !EggSlotOpen(s.Pets!, slot) || Egg(s.Pets!, req.RoundId, slot) is not { } egg
            || Remaining(s.Pets!, egg) > 0)) code = ErrorCodes.PetOperation;
        var reply = new PetEggRewardRes();
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            var goods = new List<(int, int)>();
            foreach (long slot in req.SlotIds)
            {
                var egg = Egg(next, req.RoundId, slot)!;
                int item = checked((int)egg.AwardId), count = checked((int)egg.AwardCount);
                if (count <= 0 || _data.Row("PetIncubationResultConfig", item) is not { } result) { code = ErrorCodes.PetNoConfig; break; }
                var reward = new PetEggRewardItem { ItemId = item, Count = count };
                if (egg.AwardType == 1 && count == 1 && _data.Row("PetBaseConfig", item) is { } pet && _stars.ContainsKey((item, 1, 0)))
                {
                    if (next.ListPet.ContainsKey(item))
                    {
                        var broken = Flat(pet, "_kDecompose");
                        if (broken.Length != 2 || broken[1] <= 0 || broken[1] > int.MaxValue || _data.Row("ItemConfig", broken[0]) == null) { code = ErrorCodes.PetNoConfig; break; }
                        goods.Add(((int)broken[0], (int)broken[1]));
                        // UI_Sys_Pet_CongratulationPanelCtrl reads brokenItems.itemId as the PET id and shows its _kDecompose item.
                        reply.BrokenItems.Add(new PetEggRewardItem { ItemId = item, Count = broken[1] });
                    }
                    else { next.ListPet[item] = NewPet(item); reply.PetItems.Add(reward); }
                }
                else if (egg.AwardType == 2 && _data.Row("ItemConfig", item) != null) { goods.Add((item, count)); reply.NormalItems.Add(reward); }
                else { code = ErrorCodes.PetNoConfig; break; }
                RemoveEgg(next, req.RoundId, slot);
                next.IncubationCount++;
                next.IncubationExp += GameData.Int(result, "_iExp");
                next.IncubationHistory.Add(new DBIncubateHistory { RoundId = req.RoundId, ItemId = item, Type = egg.AwardType, Time = Now });
            }
            if (code == 0 && !await ExchangeAsync(s, "egg-reward", next, [], goods)) code = ErrorCodes.ItemNoConfig;
            if (code == 0)
            {
                await AutoRoomLevelAsync(s);
                await ChangedAsync(s, items: true);
            }
        }
        if (code != 0) reply = new PetEggRewardRes();
        reply.Result = code;
        await s.SendGameAsync(EggReward, reply);
    }

    /// <summary>
    /// 33325 resolves every unlocked, usable slot in the selected round. The UI computes
    /// material cost for empty slots and one 300-second accelerator per remaining interval
    /// (UI_Sys_PetGachaWidgetCtrl:1467-1597). One atomic exchange grants all results.
    /// Slot order, independent Draw calls and immediate resolution are inferred server policies.
    /// </summary>
    public async Task FastAsync(GameSession s, FastIncubateReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = RoundGate(s, req.RoundId, place: true);
        if (code == 0 && s.Pets!.IncubationLv < PetParam("UnlockFastIncubationLv", 2))
            code = ErrorCodes.PetOperation;
        var config = code == 0 ? _data.Row("PetIncubationSlotConfig", req.RoundId) : null;
        var next = s.Pets?.Clone();
        var lottery = s.Lottery?.Clone();
        var costs = new Dictionary<int, long>();
        var goods = new List<(int, int)>();
        var reply = new FastIncubateRes();
        int selected = 0;
        if (code == 0)
        {
            for (long slot = 1; slot <= 5; slot++)
            {
                if (next == null || !EggSlotOpen(next, slot)) continue;
                var egg = Egg(next, req.RoundId, slot);
                long seconds;
                if (egg == null)
                {
                    egg = Draw(next, config!.Value, lottery);
                    if (egg == null) { code = ErrorCodes.PetNoConfig; break; }
                    foreach (var (item, count) in Cost(config.Value, "_kMaterial"))
                        costs[item] = checked(costs.GetValueOrDefault(item) + count);
                    seconds = Math.Max(0, GameData.Int(Room(next)!.Value, "_iIncubateTime"));
                }
                else seconds = Remaining(next, egg);
                long interval = PetParam("PetIncubationAccelerate", 300);
                if (interval <= 0) { code = ErrorCodes.PetNoConfig; break; }
                long need = (seconds + interval - 1) / interval;
                if (need > 0)
                {
                    int item = checked((int)PetParam("PetIncubationAccelerateItemId", 1250724));
                    costs[item] = checked(costs.GetValueOrDefault(item) + need);
                }
                selected++;
                int itemId = checked((int)egg.AwardId), countAward = checked((int)egg.AwardCount);
                if (countAward <= 0 || _data.Row("PetIncubationResultConfig", itemId) is not { } result)
                { code = ErrorCodes.PetNoConfig; break; }
                var reward = new PetEggRewardItem { ItemId = itemId, Count = countAward };
                if (egg.AwardType == 1 && countAward == 1 && _data.Row("PetBaseConfig", itemId) is { } pet
                    && _stars.ContainsKey((itemId, 1, 0)))
                {
                    if (next.ListPet.ContainsKey(itemId))
                    {
                        var broken = Flat(pet, "_kDecompose");
                        if (broken.Length != 2 || broken[1] <= 0 || broken[1] > int.MaxValue
                            || _data.Row("ItemConfig", broken[0]) == null) { code = ErrorCodes.PetNoConfig; break; }
                        goods.Add(((int)broken[0], (int)broken[1]));
                        reply.BrokenItems.Add(new PetEggRewardItem { ItemId = itemId, Count = broken[1] });
                    }
                    else { next.ListPet[itemId] = NewPet(itemId); reply.PetItems.Add(reward); }
                }
                else if (egg.AwardType == 2 && _data.Row("ItemConfig", itemId) != null)
                { goods.Add((itemId, countAward)); reply.NormalItems.Add(reward); }
                else { code = ErrorCodes.PetNoConfig; break; }
                RemoveEgg(next, req.RoundId, slot);
                next.IncubationCount++;
                next.IncubationExp += GameData.Int(result, "_iExp");
                next.IncubationHistory.Add(new DBIncubateHistory
                { RoundId = req.RoundId, ItemId = itemId, Type = egg.AwardType, Time = Now });
            }
            if (selected == 0) code = ErrorCodes.PetOperation;
        }
        if (code == 0)
        {
            var cost = costs.Select(x => (x.Key, x.Value)).ToList();
            if (!await ExchangeAsync(s, "fast-egg", next!, cost, goods, lottery)) code = _character.ShortageOf(s, cost);
            else
            {
                await AutoRoomLevelAsync(s);
                await ChangedAsync(s, items: true);
            }
        }
        if (code != 0) reply = new FastIncubateRes();
        reply.Result = code;
        await s.SendGameAsync(FastEgg, reply);
    }

    /// <summary>33317/33320 persist display preferences without changing the egg or its deadline.</summary>
    public async Task PreferenceAsync(GameSession s, long? skip)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s, incubation: true);
        if (code == 0 && skip != null && skip is not (0 or 1)) code = ErrorCodes.Parameter;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            if (skip is { } status) next.SkipAnim = status == 1;
            else next.OnOpenUIRoomLv = next.IncubationLv;
            await SaveAsync(s, next);
            await SyncAsync(s);
        }
        if (skip != null) await s.SendGameAsync(SetSkipAnim, new PetSetSkipAnimRes { Result = code });
        else await s.SendGameAsync(OpenRoom, new NullArg()); // This protocol has no result field; a closed gate is a no-op.
    }

    /// <summary>
    /// Explicit refusals where client evidence is incomplete (§§1,2,8): no break-stage costs
    /// or active legacy GUID/lottery path. Never fabricate those successes.
    /// </summary>
    public async Task UnsupportedAsync(GameSession s, uint id, int petId = 0)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0) code = ErrorCodes.FeatureUnavailable;
        switch (id)
        {
            case Break: await s.SendGameAsync(id, new PetBreakRes { Result = code }); break;
            case 33305: await s.SendGameAsync(id, new PetBrokenDecomposeResp { Result = code }); break;
            case 33307: await s.SendGameAsync(id, new LotteryResp { Result = code }); break;
        }
    }
}
