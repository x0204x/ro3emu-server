using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>One match item: EatChickenItemConfig entry and stack; the owner attributes mark an ID tag.</summary>
internal sealed class ChickenItem(long guid, int entry, int stack)
{
    public long Guid { get; } = guid;
    public int Entry { get; } = entry;
    public int Stack { get; set; } = stack;
    public long OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
}

/// <summary>
/// A Survivor Battle chest NPC (NPCConfig _iNPCType 2, _iNPCSubType 10): its contents travel in the AOI entity as
/// EntityBase.chicken_chest_info (MG_Entity.proto:160-172) and change through 31049 with notifyType ChickenChestInfo 11
/// (LC_Scene_Sync.lua:5679-5690, LC_Unit_NPC.lua:1539-1570); the pickup list reads them
/// (LC_ChickenHelper.lua:1400-1449). The client never reads the drop tables: everything is rolled here.
/// </summary>
internal sealed class ChickenChestUnit : WorldUnit
{
    public required int NpcId { get; init; }
    public required string Name { get; init; }
    public int Quality { get; init; }
    public bool Opened { get; set; }
    public Dictionary<long, ChickenItem> Items { get; } = new();
    public required Func<ChickenItem, ItemBaseInfo> Wire { get; init; }

    private ChickenChestInfo Info()
    {
        var info = new ChickenChestInfo { IsOpened = Opened, Quality = Quality };
        foreach (var item in Items.Values) info.Items[item.Guid] = Wire(item);
        return info;
    }

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, Name = Name, EntityCamp = 3, Face = Face,
        State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All, ChickenChestInfo = Info(),
        MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone },
    };

    public EntityBase Change() => new()
    { Uuid = (ulong)Uid, ChickenChestInfo = Info(), NotifyType = EntityBase.Types.NotifyType.ChickenChestInfo };
}

public sealed partial class TimedBattleService
{
    public const uint InsertItem = 82625, UseItem = 82626, DiscardItem = 82627, PickupItem = 82631, PickupAll = 82632,
        PickupNotify = 82642, ItemUpgrade = 82678, ItemUpgradeAll = 82679;
    /// <summary>Chest units: their own UID namespace below 2^53 (see DungeonService.SceneActorUidBase).</summary>
    private const long ChestUidBase = 1L << 48;
    private long _chestSerial;
    /// <summary>(inferred) Bar slots: the client's InitChickenBar drops key 0, so slots start at 1.</summary>
    private const int MaxBarSlot = 10;
    private const int ItemTypeItem = 1, ItemTypeSkill = 2, ItemTypeEquip = 3, ItemTypeCard = 5;

    private int ItemType(int entry) => ItemRow(entry) is { } row ? (int)GameData.Int(row, "_iType") : 0;

    private static long Weight(ChickenPlayer me, Func<int, long> weightOf) => me.Pack.Values.Sum(i => weightOf(i.Entry) * i.Stack);
    private long Weight(ChickenPlayer me) => Weight(me, ItemWeight);
    private long ItemWeight(int entry) => ItemRow(entry) is { } row ? GameData.Int(row, "_iWeight") : 0;

    /// <summary>How many of <paramref name="entry"/> fit: ChickenBagLimit weight and EatChickenItemConfig._iMaxQuantity
    /// (0 = no cap).</summary>
    private int Fits(ChickenPlayer me, int entry, int wanted)
    {
        if (ItemRow(entry) is not { } row || wanted <= 0) return 0;
        long fit = wanted;
        long weight = GameData.Int(row, "_iWeight");
        if (weight > 0) fit = Math.Min(fit, Math.Max(0, (BagLimit() - Weight(me)) / weight));
        long cap = GameData.Int(row, "_iMaxQuantity");
        if (cap > 0) fit = Math.Min(fit, Math.Max(0, cap - me.Pack.Values.Where(i => i.Entry == entry).Sum(i => (long)i.Stack)));
        return (int)Math.Max(0, fit);
    }

    /// <summary>Adds to the pack, filling stacks of the same entry (and owner) up to _iStackLimit first.</summary>
    private List<ChickenItem> AddToPack(ChickenRun run, ChickenPlayer me, int entry, int amount, long ownerId = 0, string ownerName = "")
    {
        var touched = new List<ChickenItem>();
        int limit = ItemRow(entry) is { } row ? (int)Math.Max(1, GameData.Int(row, "_iStackLimit")) : 1;
        foreach (var stack in me.Pack.Values.Where(i => i.Entry == entry && i.OwnerId == ownerId && i.Stack < limit).ToArray())
        {
            if (amount <= 0) break;
            int add = Math.Min(amount, limit - stack.Stack);
            stack.Stack += add; amount -= add; touched.Add(stack);
        }
        while (amount > 0)
        {
            int add = Math.Min(amount, limit);
            var item = new ChickenItem(run.NextGuid++, entry, add) { OwnerId = ownerId, OwnerName = ownerName };
            me.Pack[item.Guid] = item; amount -= add; touched.Add(item);
        }
        return touched;
    }

    /// <summary>Removes a pack item (or part of its stack) and anything that pointed at it: bars, carried ID tags.</summary>
    private bool RemoveFromPack(ChickenPlayer me, long guid, int count)
    {
        if (!me.Pack.TryGetValue(guid, out var item) || count <= 0 || count > item.Stack) return false;
        item.Stack -= count;
        if (item.Stack > 0) return true;
        me.Pack.Remove(guid);
        foreach (var slot in me.SkillBar.Where(s => s.Value == guid).Select(s => s.Key).ToArray()) me.SkillBar.Remove(slot);
        foreach (var slot in me.EquipBar.Where(s => s.Value == guid).Select(s => s.Key).ToArray()) me.EquipBar.Remove(slot);
        if (me.Pack.Values.All(i => i.Entry != item.Entry))
            foreach (var slot in me.ItemBar.Where(s => s.Value == item.Entry).Select(s => s.Key).ToArray()) me.ItemBar.Remove(slot);
        foreach (var owner in me.IdentityCards.Where(c => c.Value == guid).Select(c => c.Key).ToArray()) me.IdentityCards.Remove(owner);
        return true;
    }

    /// <summary>In the battle stage and alive (the client refuses the same while dead, LC_DunGeon_Chicken.lua:2930-3255).</summary>
    private static int CanAct(ChickenRun run, ChickenPlayer me, GameSession session) =>
        run.Stage != StageStartGame ? ErrorCodes.FeatureUnavailable
        : session.Dead || me.State != RoundRoleStatus.InWar ? ErrorCodes.UseWhileDead : 0;

    private ChickenChestUnit? ChestInReach(ChickenRun run, GameSession session, long uid, out int error)
    {
        var chest = run.Chests.FirstOrDefault(c => c.Uid == uid);
        error = chest == null ? ErrorCodes.NpcMissing
            : MonsterService.Distance(session.Position.X, session.Position.Z, chest.X, chest.Z) > PickupReach ? ErrorCodes.NpcTooFar : 0;
        return error == 0 ? chest : null;
    }

    // ---- chests ----

    /// <summary>
    /// DL:11-64 Chicken_Box_Drop -> EatChickenDropPoolConfig: each _iDropGroup_n = [group, chance/10000, rolls, count]
    /// (rolls and count are (inferred)); a roll draws one weighted EatChickenDropGroupConfig row (defaults from
    /// Config/DataConfig/EatChickenDropGroupConfig.lua:2-8: item 10006, 1..1) and grants count x [_iMinNumber, _iMaxNumber].
    /// </summary>
    private List<(int Entry, int Amount)> RollPool(Random rng, int pool)
    {
        var drops = new List<(int, int)>();
        if (_data.Row("EatChickenDropPoolConfig", pool) is not { } row) return drops;
        for (int n = 1; n <= 9; n++)
        {
            if (!row.TryGetProperty($"_iDropGroup_{n}", out var spec) || spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() < 2)
                continue;
            int group = (int)(GameData.Number(spec[0]) ?? 0), chance = (int)(GameData.Number(spec[1]) ?? 0);
            int rolls = spec.GetArrayLength() > 2 ? (int)(GameData.Number(spec[2]) ?? 1) : 1;
            int count = spec.GetArrayLength() > 3 ? (int)(GameData.Number(spec[3]) ?? 1) : 1;
            if (rng.Next(10000) >= chance || _data.Row("EatChickenDropGroupConfig", group) is not { } rows
                || rows.ValueKind != JsonValueKind.Array) continue;
            var choices = rows.EnumerateArray().Select(r => (Row: r, Weight: Math.Max(0, GameData.Int(r, "_iWeights")))).ToArray();
            long total = choices.Sum(c => c.Weight);
            if (total <= 0) continue;
            for (int roll = 0; roll < Math.Max(1, rolls); roll++)
            {
                long pick = rng.NextInt64(total);
                var chosen = choices.First(c => (pick -= c.Weight) < 0).Row;
                int entry = chosen.TryGetProperty("_iDropItem", out _) ? (int)GameData.Int(chosen, "_iDropItem") : CurrencyItem;
                int min = chosen.TryGetProperty("_iMinNumber", out _) ? (int)GameData.Int(chosen, "_iMinNumber") : 1;
                int max = chosen.TryGetProperty("_iMaxNumber", out _) ? (int)GameData.Int(chosen, "_iMaxNumber") : 1;
                int amount = Math.Max(1, count) * (max > min ? rng.Next(min, max + 1) : Math.Max(1, min));
                if (ItemRow(entry) != null) drops.Add((entry, amount));
            }
        }
        return drops;
    }

    private void AddToChest(ChickenRun run, ChickenChestUnit chest, int entry, int amount, long ownerId = 0, string ownerName = "")
    {
        int limit = ItemRow(entry) is { } row ? (int)Math.Max(1, GameData.Int(row, "_iStackLimit")) : 1;
        foreach (var stack in chest.Items.Values.Where(i => i.Entry == entry && i.OwnerId == ownerId && i.Stack < limit).ToArray())
        {
            int add = Math.Min(amount, limit - stack.Stack);
            stack.Stack += add; amount -= add;
            if (amount <= 0) return;
        }
        while (amount > 0)
        {
            int add = Math.Min(amount, limit);
            var item = new ChickenItem(run.NextGuid++, entry, add) { OwnerId = ownerId, OwnerName = ownerName };
            chest.Items[item.Guid] = item; amount -= add;
        }
    }

    private async Task<ChickenChestUnit?> SpawnChestAsync(Match match, ChickenRun run, int npcId, MapPoint at,
        Action<ChickenChestUnit> fill)
    {
        if (_data.Row("NPCConfig", npcId) is not { } npc) return null;
        var chest = new ChickenChestUnit
        {
            Uid = ChestUidBase | Interlocked.Increment(ref _chestSerial), SceneEntry = ChickenScene, InstanceId = match.Room!.Id,
            X = at.X, Y = at.Y, Z = at.Z, Face = at.Face, NpcId = npcId, Name = _data.ClientTextOf(npc, "_kName"),
            Quality = BoxDrop.ContainsKey(npcId) && npcId < 15000600 ? npcId - 15000500 : 0, Wire = item => Wire(item),
        };
        fill(chest);
        if (!await _instances.AddPropAsync(match.Room, chest)) return null;
        run.Chests.Add(chest);
        return chest;
    }

    /// <summary>(inferred) Supply crates 15000501..509 at random walkable points of the island at the battle start.</summary>
    private async Task SpawnCratesAsync(Match match, ChickenRun run)
    {
        int count = Math.Clamp(match.Players.Length * CratesPerPlayer, MinCrates, MaxCrates);
        int total = CrateWeights.Sum(c => c.Weight);
        for (int i = 0; i < count && !match.Settled; i++)
        {
            int pick = run.Rng.Next(total);
            int npc = CrateWeights.First(c => (pick -= c.Weight) < 0).Npc;
            var (x, z) = RandomPoint(run, IslandX, IslandZ, IslandRadius * 0.9f);
            if (WalkablePoint(x, z) is not { } at) continue;
            var drops = RollPool(run.Rng, BoxDrop[npc]);
            await SpawnChestAsync(match, run, npc, at with { Face = (float)(run.Rng.NextDouble() * 360) },
                chest => { foreach (var (entry, amount) in drops) AddToChest(run, chest, entry, amount); });
        }
    }

    /// <summary>A chest's changed contents to its viewers; an emptied chest goes away (inferred: the client copies the new
    /// info into the old with table.deepCopy, Lua_Table.lua:720-758, so an absent items map would not clear it).</summary>
    private async Task ChestChangedAsync(Match match, ChickenRun run, ChickenChestUnit chest)
    {
        if (chest.Items.Count == 0)
        {
            run.Chests.Remove(chest);
            if (match.Room != null) await _instances.RemovePropAsync(match.Room, chest);
            return;
        }
        await _world.ToUnitViewersAsync(chest, CharacterService.EntityBaseChange, chest.Change());
    }

    internal IReadOnlyList<ChickenChestUnit> ChestsForTest(GameSession session) =>
        Chicken(session) is (_, var run, _) ? run.Chests.ToArray() : [];

    /// <summary>Tests: a chest with known contents where the player stands (crates are rolled at random points).</summary>
    internal async Task<long> PlaceChestForTestAsync(GameSession near, int npcId, params (int Entry, int Amount)[] items)
    {
        if (Chicken(near) is not (var match, var run, _)) return 0;
        var chest = await SpawnChestAsync(match, run, npcId, new MapPoint(near.Position.X, near.Position.Y, near.Position.Z, 0),
            c => { foreach (var (entry, amount) in items) AddToChest(run, c, entry, amount); });
        return chest?.Uid ?? 0;
    }

    // ---- 82631 / 82632 pickup ----

    /// <summary>Moves up to <paramref name="wanted"/> of a chest item into the pack; returns what moved.</summary>
    private int Take(ChickenRun run, ChickenPlayer me, ChickenChestUnit chest, ChickenItem item, int wanted, List<ItemBaseInfo> picked)
    {
        int take = Fits(me, item.Entry, Math.Min(wanted, item.Stack));
        if (take <= 0) return 0;
        var touched = AddToPack(run, me, item.Entry, take, item.OwnerId, item.OwnerName);
        if (item.Entry == IdentityCardItem && item.OwnerId > 0 && touched.Count > 0) me.IdentityCards[item.OwnerId] = touched[^1].Guid;
        item.Stack -= take;
        if (item.Stack <= 0) chest.Items.Remove(item.Guid);
        chest.Opened = true;
        picked.Add(Wire(item, take));
        return take;
    }

    /// <summary>82631: LC_DAL_Chicken.lua:4028-4057; success raises ChickenPickUpItemSuccess (:562-580).</summary>
    public async Task PickupAsync(GameSession session, PickUpChickenItemReq req)
    {
        var resp = new PickUpChickenItemResp { NpcId = req.NpcId, ItemGuid = req.ItemGuid };
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken pickup", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (ChestInReach(run, session, req.NpcId, out result) is not { } chest) return;
                if (!chest.Items.TryGetValue(req.ItemGuid, out var item)) { result = ErrorCodes.ItemMissing; return; }
                var picked = new List<ItemBaseInfo>();
                int taken = Take(run, me, chest, item, req.Count > 0 ? req.Count : item.Stack, picked);
                if (taken == 0) { result = ErrorCodes.BagFull; return; }
                result = 0;
                resp.Count = taken;
                resp.ItemInfo = picked[0];
                await AfterPickupAsync(match, run, session, picked, [chest]);
            });
        resp.Result = result;
        await session.SendGameAsync(PickupItem, resp);
    }

    /// <summary>82632: UI_Sys_Chicken_PickupWidgetBaseCtrl.lua:353-412, every item of the listed chests that fits.</summary>
    public async Task PickupAllAsync(GameSession session, PickUpAllChickenItemReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken pickup all", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                var picked = new List<ItemBaseInfo>();
                var chests = new List<ChickenChestUnit>();
                bool full = false;
                foreach (long uid in req.NpcIds.Distinct())
                {
                    if (ChestInReach(run, session, uid, out _) is not { } chest) continue;
                    chests.Add(chest);
                    foreach (var item in chest.Items.Values.ToArray())
                    {
                        Take(run, me, chest, item, item.Stack, picked);
                        if (chest.Items.ContainsKey(item.Guid)) full = true;
                    }
                }
                result = picked.Count > 0 ? 0 : full ? ErrorCodes.BagFull : ErrorCodes.NpcMissing;
                if (picked.Count > 0) await AfterPickupAsync(match, run, session, picked, chests);
            });
        await session.SendGameAsync(PickupAll, new PickUpAllChickenItemResp { Result = result, NpcIds = { req.NpcIds } });
    }

    private async Task AfterPickupAsync(Match match, ChickenRun run, GameSession session, List<ItemBaseInfo> picked,
        IEnumerable<ChickenChestUnit> chests)
    {
        await SyncRoundAsync(match, run, session);
        await session.SendGameAsync(PickupNotify, new PickChickenItemNotify { PickItems = { picked } });
        foreach (var chest in chests.Distinct().ToArray()) await ChestChangedAsync(match, run, chest);
    }

    // ---- 82627 discard ----

    /// <summary>82627: LC_ChickenHelper.lua:1045-1101 (nearest box or 0, and the position): into that box, else a new
    /// "Discarded Supply Crate" 15000053 there.</summary>
    public async Task DiscardAsync(GameSession session, DiscardChickenItemReq req)
    {
        var resp = new DiscardChickenItemResp { NpcId = req.NpcId, Pos = req.Pos };
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken discard", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (req.Items.Count == 0 || req.Items.Any(kv => !me.Pack.TryGetValue(kv.Key, out var held) || kv.Value <= 0 || kv.Value > held.Stack))
                { result = ErrorCodes.ItemMissing; return; }
                bool equipChanged = req.Items.Keys.Any(guid => me.EquipBar.ContainsValue(guid));
                var dropped = req.Items.Select(kv => (Item: me.Pack[kv.Key], Count: kv.Value)).ToArray();
                var target = req.NpcId != 0 ? ChestInReach(run, session, req.NpcId, out _) : null;
                foreach (var (item, count) in dropped) RemoveFromPack(me, item.Guid, count);
                if (target != null)
                {
                    foreach (var (item, count) in dropped) AddToChest(run, target, item.Entry, count, item.OwnerId, item.OwnerName);
                    await ChestChangedAsync(match, run, target);
                }
                else
                {
                    var pos = req.Pos is { } p && MonsterService.Distance(session.Position.X, session.Position.Z, p.X, p.Z) <= PickupReach
                        ? p : session.Position;
                    var at = WalkablePoint(pos.X, pos.Z) ?? new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0);
                    target = await SpawnChestAsync(match, run, DiscardCrate, at,
                        chest => { foreach (var (item, count) in dropped) AddToChest(run, chest, item.Entry, count, item.OwnerId, item.OwnerName); });
                }
                result = 0;
                resp.NpcId = target?.Uid ?? 0;
                if (equipChanged) await _character.RefreshStatsAsync(session);
                await SyncRoundAsync(match, run, session);
            });
        resp.Result = result;
        await session.SendGameAsync(DiscardItem, resp);
    }

    // ---- 82625 insert ----

    /// <summary>
    /// 82625: LC_ChickenHelper.lua:837-950 WearChickenItem. Items bar slot -> EatChickenItemConfig id (int32), skill and
    /// equip bars slot -> pack guid (ChickenDungeonInfo, MG_Chicken.proto:36-42); guid 0 clears the slot. A skill put on
    /// the bar starts DL AssemblySkillCd (5 s, on the item's cooldown). One equipment per _iSubType (inferred).
    /// </summary>
    public async Task InsertAsync(GameSession session, InsertChickenItemReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken insert", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                int type = (int)req.Type;
                if (type is not (ItemTypeItem or ItemTypeSkill or ItemTypeEquip) || req.Index is < 1 or > MaxBarSlot) { result = ErrorCodes.Parameter; return; }
                bool equipChanged = type == ItemTypeEquip;
                if (req.ItemGuid == 0)
                {
                    if (type == ItemTypeItem) me.ItemBar.Remove(req.Index);
                    else if (type == ItemTypeSkill) me.SkillBar.Remove(req.Index);
                    else me.EquipBar.Remove(req.Index);
                }
                else
                {
                    if (!me.Pack.TryGetValue(req.ItemGuid, out var item) || ItemType(item.Entry) != type) { result = ErrorCodes.ItemMissing; return; }
                    if (type == ItemTypeItem)
                    {
                        foreach (var slot in me.ItemBar.Where(s => s.Value == item.Entry).Select(s => s.Key).ToArray()) me.ItemBar.Remove(slot);
                        me.ItemBar[req.Index] = item.Entry;
                    }
                    else if (type == ItemTypeSkill)
                    {
                        foreach (var slot in me.SkillBar.Where(s => s.Value == item.Guid).Select(s => s.Key).ToArray()) me.SkillBar.Remove(slot);
                        me.SkillBar[req.Index] = item.Guid;
                        me.ItemCd[item.Entry] = Math.Max(me.ItemCd.GetValueOrDefault(item.Entry), NowMs() + AssemblySkillCdMs);
                    }
                    else
                    {
                        long subType = ItemRow(item.Entry) is { } row ? GameData.Int(row, "_iSubType") : 0;
                        foreach (var slot in me.EquipBar.Where(s => s.Value == item.Guid || me.Pack.TryGetValue(s.Value, out var worn)
                                     && ItemRow(worn.Entry) is { } w && GameData.Int(w, "_iSubType") == subType).Select(s => s.Key).ToArray())
                            me.EquipBar.Remove(slot);
                        me.EquipBar[req.Index] = item.Guid;
                    }
                }
                result = 0;
                if (equipChanged) await _character.RefreshStatsAsync(session);
                await SyncRoundAsync(match, run, session);
            });
        await session.SendGameAsync(InsertItem, new InsertChickenItemResp
        { Result = result, Type = req.Type, Index = req.Index, ItemGuid = req.ItemGuid });
    }

    // ---- 82626 use ----

    /// <summary>
    /// 82626: LC_ChickenHelper.lua:969-1042 (the client checks the cooldown first, tip 63191). EatChickenItemConfig
    /// _iUseEffect: 203 = add a buff (potions 10001..10005 -> buffs 15999400..800), 606 = cast a skill (the ID tag 50001
    /// -> 15909900 "Identity Card Revive": starts the teammate revive channel, as 82650). _iCd is in seconds.
    /// </summary>
    public async Task UseAsync(GameSession session, UseChickenItemReq req)
    {
        int result = ErrorCodes.Parameter;
        bool reviveChannel = false;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken use", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (!me.Pack.TryGetValue(req.ItemGuid, out var item) || ItemRow(item.Entry) is not { } row) { result = ErrorCodes.ItemMissing; return; }
                long now = NowMs();
                if (me.ItemCd.GetValueOrDefault(item.Entry) > now) { result = ErrorCodes.Cooldown; return; }
                var effects = UseEffects(row).ToArray();
                if (effects.Length == 0) { result = ErrorCodes.FeatureUnavailable; return; }
                if (effects.Any(e => e.Type == 606))
                {
                    reviveChannel = true; // the cards are spent when the channel completes
                    return;
                }
                foreach (var (type, value) in effects.Where(e => e.Type == 203))
                    await ApplyItemBuffAsync(session, me, value, now);
                RemoveFromPack(me, item.Guid, 1);
                if (GameData.Int(row, "_iCd") is var cd and > 0) me.ItemCd[item.Entry] = now + cd * 1000;
                result = 0;
                await SyncRoundAsync(match, run, session);
            });
        if (reviveChannel) result = await StartReviveCoreAsync(session);
        await session.SendGameAsync(UseItem, new UseChickenItemResp { Result = result, ItemGuid = req.ItemGuid, Count = req.Count });
    }

    private static IEnumerable<(int Type, int Value)> UseEffects(JsonElement row)
    {
        if (!row.TryGetProperty("_iUseEffect", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var effect in list.EnumerateArray())
            if (effect.ValueKind == JsonValueKind.Array && effect.GetArrayLength() >= 2
                && GameData.Number(effect[0]) is { } type && GameData.Number(effect[1]) is { } value)
                yield return ((int)type, (int)value);
    }

    /// <summary>
    /// Buff 203 of a potion. Its HP restore is the BuffConfig T_AXadd0_etc tuple [[2, coefficient, 101, 3]] = the
    /// recipient's max HP x coefficient / 10000 (PetCombatRules.AttributeAmount); BuffService does not run buff
    /// formulas, so it is applied here once per second over _iTime (one tick when shorter), (inferred) cadence.
    /// </summary>
    private async Task ApplyItemBuffAsync(GameSession session, ChickenPlayer me, int buffId, long now)
    {
        await _buffs.AddAsync(session, buffId, 0, session.Role!.Id);
        if (_data.Row("BuffConfig", buffId) is not { } buff) return;
        long perTick = PetCombatRules.AttributeAmount(buff, _character.Stats(session), _character.Stats(session));
        if (perTick <= 0) return;
        long duration = Math.Max(1000, GameData.Int(buff, "_iTime"));
        await HealAsync(session, me, perTick);
        me.Heals.Add((perTick, now + duration));
    }

    private async Task HealAsync(GameSession session, ChickenPlayer me, long amount)
    {
        if (session.Dead || amount <= 0) return;
        var stats = _character.Stats(session);
        long before = stats[2000], after = Math.Min(stats[101], before + amount);
        if (after <= before) return;
        me.Cure += after - before;
        await _character.SetHpSpAsync(session, after, null);
    }

    private async Task TickHealsAsync(Match match, ChickenRun run, long now)
    {
        foreach (var player in InRoom(match).ToArray())
        {
            var me = run.Players[player];
            if (me.Heals.Count == 0) continue;
            me.Heals.RemoveAll(h => h.UntilMs <= now || player.Dead);
            foreach (var (perTick, _) in me.Heals.ToArray()) await HealAsync(player, me, perTick);
        }
    }

    // ---- 82678 / 82679 upgrade ----

    /// <summary>
    /// 82678: UI_Sys_Chicken_UpgradeTipWidgetCtrl.lua:69 -> LC_ChickenHelper.lua:4283-4307, response LC_DAL_Chicken.lua
    /// :1168-1194. _iUpdateCount copies of a skill item become one _iUpgradeItem (22001 x3 -> 21001 x3 -> 20001,
    /// LC_DunGeon_Chicken.lua:2960-3090). A consumed copy on the skill bar hands its slot to the new item (inferred).
    /// </summary>
    public async Task UpgradeAsync(GameSession session, ChickenItemUpgradeReq req)
    {
        int result = ErrorCodes.Parameter;
        long guid = 0;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken upgrade", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                guid = UpgradeOnce(run, me, req.ItemConfigId, out result);
                if (result == 0) await SyncRoundAsync(match, run, session);
            });
        await session.SendGameAsync(ItemUpgrade, new ChickenItemUpgradeResp
        { Result = result, ItemConfigId = req.ItemConfigId, NewItemGuid = (ulong)guid });
    }

    /// <summary>82679: UI_Sys_Chicken_BagBaseCtrl.lua:669 -> LC_ChickenHelper.lua:4217; every possible upgrade, lower
    /// qualities first so a chain cascades.</summary>
    public async Task UpgradeAllAsync(GameSession session)
    {
        int result = ErrorCodes.Parameter;
        var created = new List<ulong>();
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken upgrade all", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                for (bool progress = true; progress;)
                {
                    progress = false;
                    foreach (int entry in me.Pack.Values.Select(i => i.Entry).Distinct()
                                 .OrderBy(e => ItemRow(e) is { } r ? GameData.Int(r, "_iQuality") : 0).ToArray())
                    {
                        long guid = UpgradeOnce(run, me, entry, out int one);
                        if (one != 0) continue;
                        created.Add((ulong)guid); progress = true;
                        break;
                    }
                }
                result = created.Count > 0 ? 0 : ErrorCodes.ItemsShort;
                if (created.Count > 0) await SyncRoundAsync(match, run, session);
            });
        await session.SendGameAsync(ItemUpgradeAll, new ChickenItemUpgradeAllResp { Result = result, NewItemGuids = { created } });
    }

    private long UpgradeOnce(ChickenRun run, ChickenPlayer me, int entry, out int result)
    {
        result = ErrorCodes.Parameter;
        if (ItemRow(entry) is not { } row || GameData.Int(row, "_iType") != ItemTypeSkill) return 0;
        int next = (int)GameData.Int(row, "_iUpgradeItem"), need = (int)GameData.Int(row, "_iUpdateCount");
        if (next <= 0 || need <= 0 || ItemRow(next) == null) return 0;
        var copies = me.Pack.Values.Where(i => i.Entry == entry)
            .OrderBy(i => me.SkillBar.ContainsValue(i.Guid) ? 1 : 0).ToList();
        if (copies.Sum(i => i.Stack) < need) { result = ErrorCodes.ItemsShort; return 0; }
        int barSlot = me.SkillBar.Where(s => copies.Any(c => c.Guid == s.Value)).Select(s => s.Key).DefaultIfEmpty(0).First();
        int left = need;
        foreach (var copy in copies)
        {
            if (left <= 0) break;
            int spend = Math.Min(left, copy.Stack);
            RemoveFromPack(me, copy.Guid, spend);
            left -= spend;
        }
        var created = AddToPack(run, me, next, 1);
        long guid = created[^1].Guid;
        if (barSlot > 0 && !me.SkillBar.ContainsKey(barSlot)) me.SkillBar[barSlot] = guid;
        result = 0;
        return guid;
    }
}
