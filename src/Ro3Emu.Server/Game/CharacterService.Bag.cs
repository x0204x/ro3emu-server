using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- bag: clear, use, sort and pack ----

    public async Task<string> ClearBagAsync(GameSession session)
    {
        using var _ = await LockAsync(session);
        var removed = session.Items.Where(i => i.Grid == 0 && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).ToList();
        var next = session.Items.Except(removed).Select(i => i.Clone()).ToList();
        await CommitCharacterAsync(session, "gm-clear", nextItems: next,
            costs: removed.Select(i => (i.Entry, (long)i.Stack)).ToArray());
        await SyncItemsAsync(session);
        if (removed.Count > 0) foreach (var handler in StateChanged) handler(session);
        return $"Removed {removed.Count} item(s).";
    }

    private void TakeFromStack(GameSession session, InvItem item, int take, List<InvItem> changed, List<InvItem> removed,
        bool persist = true)
    {
        item.Stack -= take;
        if (item.Stack > 0)
        {
            if (persist) itemStore.Update(item);
            changed.Add(item);
        }
        else
        {
            if (persist) itemStore.Delete(item.Guid);
            session.Items.Remove(item);
            removed.Add(item);
        }
    }

    /// <summary>31323: uses one item from a stack (the client plays the item's own effect list).</summary>
    public Task UseItemAsync(GameSession session, ItemUseReq req) => UseItemCoreAsync(session, req, reply: true);

    /// <summary>BuffService supplies live item-use restrictions to every use path, including shortcuts.</summary>
    public Func<GameSession, bool>? IsItemUseForbidden { get; set; }

    private async Task<int> UseItemCoreAsync(GameSession session, ItemUseReq req, bool reply)
    {
        if (session.Role == null)
        {
            if (reply) await session.SendGameAsync(ItemUse, new ItemUseResp { Result = ErrorCodes.FeatureUnavailable, ItemGuid = req.ItemGuid });
            return ErrorCodes.FeatureUnavailable;
        }
        using var ownerScope = await EnterItemUseAsync(session, req);
        using var activityGate = await LockTreasureUseAsync(session, req);
        using var _ = await LockAsync(session);
        var item = session.Items.FirstOrDefault(i => i.Guid == req.ItemGuid && i.Grid == 0);
        int count = req.ItemCount;
        var row = item != null ? data.Row("ItemConfig", item.Entry) : null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int group = row is { } r0 && r0.TryGetProperty("_kCDGroup", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0
            ? (int)(GameData.Number(g[0]) ?? 0) : 0;
        int result = count is <= 0 or > 999 ? ErrorCodes.Parameter
            : item == null || EmblemService.IsEmblem(data, item.Entry) || EmblemService.IsReserved(session, item) || TotemService.IsReserved(session, item) ? ErrorCodes.ItemMissing
            : item.Stack < count ? ErrorCodes.ItemsShort : row == null ? ErrorCodes.ItemNoConfig
            : count > 1 && GameData.Int(row.Value, "_iGroupUse") == 0
                && !(LifeItemUse != null && HandlesLifeItem(row.Value)) && !IsChoiceBox(row.Value) ? ErrorCodes.Parameter
            : req.ItemParam.Count > 0 && WingDestination(row.Value, req) == null && !CarriesPetChoice(row.Value, req)
                && !CarriesBoxParam(row.Value, req)
                && !AcceptsItemActionParam(row.Value, req) ? ErrorCodes.FeatureUnavailable
            : item.BindExpire > 0 && item.BindExpire <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? ErrorCodes.ItemMissing
            : session.Dead ? ErrorCodes.UseWhileDead
            : IsItemUseForbidden?.Invoke(session) == true ? ErrorCodes.ItemUseForbidden
            : session.ItemCds.GetValueOrDefault(item.Entry) > now || group > 0 && session.ItemGroupCds.GetValueOrDefault(group) > now ? ErrItemInCd
            : 0;
        if (result == 0) result = ItemRuleError(session, row!.Value, item!.Entry, count);
        if (result != 0)
        {
            if (reply) await session.SendGameAsync(ItemUse, new ItemUseResp { Result = result, ItemGuid = req.ItemGuid });
            return result;
        }
        int entry = item!.Entry;
        // Cooldowns: ItemConfig._iCD for the item, _kCDGroup [group, shareMs] for its group; absolute end times.
        long itemCd = GameData.Int(row!.Value, "_iCD") is var cd and > 0 ? now + cd : 0;
        long shareCd = group > 0 && row.Value.TryGetProperty("_kCDGroup", out var g2) && g2.GetArrayLength() > 1 && GameData.Number(g2[1]) is { } share and > 0
            ? now + (long)share : 0;
        var stored = session.BagState.Info.Clone();
        int useTimes = StageUseTimes(stored, row.Value, entry, count);
        if (itemCd > 0 || shareCd > 0)
        {
            stored.ItemCds ??= new CdData();
            stored.ShareCds ??= new CdData();
            if (itemCd > 0) stored.ItemCds.Data[entry] = itemCd;
            if (shareCd > 0) stored.ShareCds.Data[group] = shareCd;
        }
        // The specialized owner consumes the item itself. Include cooldowns in that same transaction too.
        byte[] cooldownBytes = stored.ToByteArray();
        void PersistCooldown(RoleItemPlanBuilder builder) => builder.RoleBlob(session.Role!.Id, "bag_state", cooldownBytes);
        void PublishCooldown()
        {
            session.BagState.Info = stored;
            if (itemCd > 0) session.ItemCds[entry] = itemCd;
            if (shareCd > 0) session.ItemGroupCds[group] = shareCd;
        }
        int? specialized = null;
        if (SpecialItemUse.Count > 0 || LifeItemUse != null)
        {
            var lifeUse = new LifeItemExchangeScope(session, PersistCooldown, PublishCooldown);
            var previous = LifeItemExchange.Value;
            LifeItemExchange.Value = lifeUse;
            try
            {
                foreach (var handler in SpecialItemUse)
                {
                    specialized = await handler(session, req, item, row.Value, count);
                    if (specialized != null) break;
                }
                if (specialized == null && LifeItemUse != null)
                    specialized = await LifeItemUse(session, item, row.Value, count);
            }
            finally { lifeUse.Active = false; LifeItemExchange.Value = previous; }
        }
        if (specialized == null && IsTreasureMapUse(row.Value))
            specialized = StartTreasureMap == null ? ErrorCodes.FeatureUnavailable
                : await StartTreasureMap(session, req, PersistCooldown, PublishCooldown);
        result = specialized ?? (CanApplyUseEffects(row.Value) ? WingError(session, row.Value, req) : ErrorCodes.FeatureUnavailable);
        if (result != 0)
        {
            if (reply) await session.SendGameAsync(ItemUse, new ItemUseResp { Result = result, ItemGuid = req.ItemGuid });
            return result;
        }
        if (specialized == null)
        {
            var stagedItems = session.Items.Select(i => i.Clone()).ToList();
            var stagedItem = stagedItems.Single(i => i.Guid == item.Guid);
            stagedItem.Stack -= count;
            if (stagedItem.Stack == 0) stagedItems.Remove(stagedItem);
            // A consumed unit and its cooldown are one durable transition. Publish only after commit.
            await CommitCharacterAsync(session, "use", nextItems: stagedItems,
                blobs: new Dictionary<string, byte[]> { ["bag_state"] = cooldownBytes },
                apply: PublishCooldown, costs: [(entry, count)]);
        }
        if (reply) await session.SendGameAsync(ItemUse, new ItemUseResp { Result = 0, ItemGuid = item.Guid, ItemCount = count, ItemId = entry, ItemCd = itemCd, ShareCd = shareCd, ItemTimes = useTimes });
        await SyncItemsAsync(session);
        if (itemCd > 0 || shareCd > 0)
        {
            var notify = new ItemsCDNotify();
            notify.Items.Add(new ItemCDInfo { ItemId = entry, ItemCd = itemCd, ShareCd = shareCd });
            await session.SendGameAsync(ItemsCdNotify, notify);
        }
        if (specialized == null) await ApplyUseEffectsAsync(session, row.Value, count, req);
        else await AfterItemActionAsync(session);
        foreach (var handler in ItemUsed) handler(session, entry, count);
        foreach (var handler in StateChanged) handler(session);
        return 0;
    }

    private const int ErrItemInCd = 161;

    /// <summary>Shortcut/quick-use eligibility recognizes each effect owner; actual use rechecks their runtime limits.</summary>
    private bool CanUseItemEffects(JsonElement row) => CanApplyUseEffects(row)
        || SpecialItemUse.Count > 0 && HandlesCollectionItem(row)
        || HandlesItemAction(row)
        || LifeItemUse != null && HandlesLifeItem(row)
        || StartTreasureMap != null && IsTreasureMapUse(row)
        || SpecialItemUse.Count > 0 && IsAwardBox(row);

    private static bool HandlesCollectionItem(JsonElement row)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array
            || effects.GetArrayLength() != 1 || effects[0].ValueKind != JsonValueKind.Array
            || effects[0].GetArrayLength() == 0) return false;
        return GameData.Number(effects[0][0]) is 200 or 201;
    }

    private static bool HandlesLifeItem(JsonElement row)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array
            || effects.GetArrayLength() != 1 || effects[0].ValueKind != JsonValueKind.Array) return false;
        var effect = effects[0];
        long Arg(int n) => effect.GetArrayLength() > n ? (long)(GameData.Number(effect[n]) ?? 0) : 0;
        return Arg(0) == 607 || Arg(0) == 315 && Arg(1) == LifeJobService.VigorId;
    }

    private bool CanApplyUseEffects(JsonElement row)
    {
        if (GameData.Int(row, "_iIsUse") == 0 || !row.TryGetProperty("_iUseEffect", out var effects)
            || effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() == 0) return false;
        bool wing = IsWing(row);
        foreach (var effect in effects.EnumerateArray())
        {
            if (effect.ValueKind != JsonValueKind.Array || effect.GetArrayLength() == 0 || GameData.Number(effect[0]) is not { } number) return false;
            // A wing's other effects (8 CloseUI, 104 RebuildAutoMovePath, 106 UseWingEnd) are client-hosted
            // (ActionConfig _iEXECHost 0): the client plays them after the 31323 reply.
            if (wing && ClientHosted((int)number)) continue;
            if (number != 100 && (number != 203 || GiveBuff == null) && (number is not (JumpSameMap or JumpOtherMap) || WingJump == null)) return false;
        }
        return true;
    }

    private const int JumpSameMap = 101, JumpOtherMap = 102;

    /// <summary>A 204 SelfChoosePet box sends its pick as selfChoosePet; PetService.TryUseSelfChooseAsync owns the use.</summary>
    private static bool CarriesPetChoice(JsonElement row, ItemUseReq req) => UseActions(row).Contains(PetService.SelfChoosePetAction)
        && req.ItemParam.Count == 1 && req.ItemParam[0].VarCase == ItemUseParamInfo.VarOneofCase.SelfChoosePet;

    /// <summary>Moves the player for a wing: a destination scene (the current one for a Fly Wing). WorldCoreService.</summary>
    public Func<GameSession, long, Task<bool>>? WingJump { get; set; }

    private bool ClientHosted(int action) => data.Row("ActionConfig", action) is { } a && GameData.Int(a, "_iEXECHost") == 0;

    /// <summary>ItemConfig 1002001 Fly Wing [[101,0],[104],[106]] and 1002000 Butterfly Wing [[8,...],[102,0],[104]].</summary>
    private static bool IsWing(JsonElement row) => UseActions(row).Any(a => a is JumpSameMap or JumpOtherMap);

    private static IEnumerable<int> UseActions(JsonElement row) =>
        row.TryGetProperty("_iUseEffect", out var effects) && effects.ValueKind == JsonValueKind.Array
            ? effects.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0)
                .Select(e => (int)(GameData.Number(e[0]) ?? 0))
            : [];

    /// <summary>
    /// The Butterfly Wing's destination: the map the player picked on the map panel, sent as the single
    /// ItemUseParamInfo.butterflyWings.mapId (re/clientB/lua-src/Logic/Knapsack/LC_ItemHelper.lua:1236-1270). The panel
    /// only offers maps whose MapConfig._iFly is 1 (Config/CFG/Lua_CFG_MapConfigBase.lua:174-189 CanFly).
    /// </summary>
    private long? WingDestination(JsonElement row, ItemUseReq req)
    {
        if (!UseActions(row).Contains(JumpOtherMap) || req.ItemParam.Count != 1
            || req.ItemParam[0].VarCase != ItemUseParamInfo.VarOneofCase.ButterflyWings) return null;
        long map = req.ItemParam[0].ButterflyWings.MapId;
        return data.Row("MapConfig", map) is { } m && GameData.Int(m, "_iFly") == 1 ? map : null;
    }

    /// <summary>
    /// Refusals before the item is spent: a Fly Wing needs a current map with _iFly 1 (the client checks the same,
    /// LC_ItemHelper.lua:1330-1360) and no instance; a Butterfly Wing needs its picked destination.
    /// </summary>
    private int WingError(GameSession session, JsonElement row, ItemUseReq req)
    {
        var actions = UseActions(row).ToList();
        if (!actions.Any(a => a is JumpSameMap or JumpOtherMap)) return 0;
        // Neither wing leaves an instance or interrupts a scene load (a Butterfly Wing out of a dungeon would skip its
        // leave/settlement path).
        if (session.InstanceId != 0 || session.SceneLoading) return ErrorCodes.FeatureUnavailable;
        if (actions.Contains(JumpOtherMap) && WingDestination(row, req) == null) return ErrorCodes.Parameter;
        if (actions.Contains(JumpSameMap) && (data.Row("MapConfig", session.SceneEntry) is not { } map
            || GameData.Int(map, "_iFly") != 1)) return ErrorCodes.FeatureUnavailable;
        return 0;
    }

    /// <summary>
    /// ItemConfig._iUseEffect [[actionId, args...]] for server actions (ActionConfig _iEXECHost 1): 100 RecoverFixed
    /// [1 HP / 2 SP, flat, percent of max /10000], 203 GivePlayerBuff [buffId]. Others are logged.
    /// </summary>
    private async Task ApplyUseEffectsAsync(GameSession session, JsonElement row, int count, ItemUseReq? req = null)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array) return;
        foreach (var e in effects.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() == 0 || GameData.Number(e[0]) is not { } action) continue;
            double Arg(int i) => e.GetArrayLength() > i ? GameData.Number(e[i]) ?? 0 : 0;
            switch ((int)action)
            {
                case 100:
                    var stats = Stats(session);
                    bool sp = Arg(1) == 2;
                    long max = sp ? stats[MaxSp] : stats[MaxHp];
                    long gain = EquipmentStunts.PotionRecovery(session, Arg(2) + max * Arg(3) / 10000, count, data);
                    if (sp) await SetHpSpAsync(session, null, stats[Sp] + gain);
                    else await SetHpSpAsync(session, stats[Hp] + gain, null);
                    break;
                case 203:
                    if (GiveBuff != null) await GiveBuff(session, (int)Arg(1), 0);
                    break;
                case JumpSameMap when WingJump != null:
                    await WingJump(session, session.SceneEntry);
                    break;
                case JumpOtherMap when WingJump != null && req != null && WingDestination(row, req) is { } destination:
                    await WingJump(session, destination);
                    break;
                case var client when ClientHosted(client):
                    break;
                default:
                    log.LogInformation("Item use action {Action} not implemented ({Effect})", (int)action, e.ToString());
                    break;
            }
        }
    }

    /// <summary>31301 sort: items of the pack ordered by ItemConfig._iSort then id; the reply must echo the request.</summary>
    public async Task SortAsync(GameSession session, ArrangeItemReq req)
    {
        using var _ = await LockAsync(session);
        int pack = (int)req.PackType;
        var staged = session.Items.Select(i => i.Clone()).ToList();
        var items = staged.Where(i => i.Grid == 0 && i.Pack == pack)
            .OrderBy(i => data.Row("ItemConfig", i.Entry) is { } r ? GameData.Int(r, "_iSort") : 0).ThenBy(i => i.Entry).ThenByDescending(i => i.Stack).ToList();
        for (int n = 0; n < items.Count; n++)
        {
            items[n].Sort = n + 1;
        }
        await CommitCharacterAsync(session, "sort", nextItems: staged);
        await session.SendGameAsync(PackItems, new ArrangeItemRes { Result = 0, Req = req });
        await SyncItemsAsync(session);
    }

    private DBPackInfo FullPack(IEnumerable<InvItem> items)
    {
        var pack = new DBPackInfo();
        foreach (var item in items)
        {
            pack.Items[item.Guid] = ItemInfo(item);
            pack.SortedIndex[item.Guid] = item.Sort;
            pack.PackCapacity = PackCapacity(item.Pack);
        }
        return pack;
    }

    private int PackCapacity(int pack) => (int)(data.Row("BackpackConfig", pack) is { } row ? GameData.Int(row, "_iTabCapacity") : 0);

    private const int PackItem = 1;
    private const int PackCard = 3;
    private const int ItemTypeCard = 4;

    /// <summary>
    /// The bag pack of an ItemConfig row: its _iBagTab, except that PackType_Card holds cards only. BackpackConfig
    /// tab 3 lists _iTabContent [4] (cards) and tab 1 lists [2,3,9]; the client never reads _iTabContent, it is the
    /// server's type-to-tab rule. The client depends on it: the 5009 handler (LC_DAL_Knapsack, proto at line 59)
    /// sorts pack 3 with LC_ItemHelper.SortFuncs[4] (proto at line 1357), which compares kCardInfo.iCardStar of any
    /// two same-quality items, and ParseItemBaseInfoByPre fills kCardInfo only for enum_ItemType.Card. A non-card
    /// with _iBagTab 3 (the card fragments 1200500..1200503, type 9) next to a same-quality card therefore made the
    /// sort throw "attempt to compare number with nil" and the client dropped the whole bag.
    /// </summary>
    internal static int ConfigPack(JsonElement row)
    {
        int tab = (int)Math.Max(1, GameData.Int(row, "_iBagTab"));
        return tab == PackCard && GameData.Int(row, "_iType") != ItemTypeCard ? PackItem : tab;
    }

    /// <summary>Moves saved non-cards out of the card pack (granted by _iBagTab alone before <see cref="ConfigPack"/>).</summary>
    private void MigrateCardPackItems(GameSession session)
    {
        var moved = session.Items.Where(i => i.Grid == 0 && i.Pack == PackCard
            && data.Row("ItemConfig", i.Entry) is { } row && ConfigPack(row) != PackCard).ToList();
        if (moved.Count == 0) return;
        var sorts = session.Items.Where(i => i.Grid == 0).GroupBy(i => i.Pack).ToDictionary(g => g.Key, g => g.Max(i => i.Sort));
        var replacements = new Dictionary<InvItem, InvItem>(ReferenceEqualityComparer.Instance);
        var builder = roles.NewTransaction();
        foreach (var old in moved.OrderBy(i => i.Sort).ThenBy(i => i.Guid))
        {
            var item = old.Clone();
            item.Pack = ConfigPack(data.Row("ItemConfig", item.Entry)!.Value);
            item.Sort = sorts[item.Pack] = sorts.GetValueOrDefault(item.Pack) + 1;
            builder.ItemUpdate(item);
            replacements[old] = item;
        }
        roles.Commit(builder, "character/card-pack-migration");
        session.Items = session.Items.Select(i => replacements.GetValueOrDefault(i, i)).ToList();
    }

    /// <summary>Mail-only preflight for combined plain, bound, exact-instance and long-currency grants under StateLock.</summary>
    internal int PlainGrantError(GameSession session, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyList<InvItem>? exactGoods = null, IReadOnlyDictionary<int, long>? currencyGoods = null,
        IReadOnlyList<(int Entry, int Count, int Bind)>? boundGoods = null)
    {
        var addedSlots = new Dictionary<int, long>();
        var currency = new Dictionary<int, long>();
        foreach (var group in goods.Select(g => (g.Entry, g.Count, Bind: 0)).Concat(boundGoods ?? []).GroupBy(g => g.Entry))
        {
            if (data.Row("ItemConfig", group.Key) is not { } row || group.Any(g => g.Count <= 0))
                return ErrorCodes.ItemNoConfig;
            long count = group.Sum(g => (long)g.Count);
            if (group.Key is BaseExpItem or JobExpItem)
            {
                long current = group.Key == BaseExpItem ? session.Progress!.Exp : session.Progress!.JobExp;
                if (count > long.MaxValue - current) return ErrorCodes.Parameter;
                continue;
            }
            if (GameData.Int(row, "_iType") == ItemTypeCurrency)
            {
                currency[group.Key] = count;
                continue;
            }
            int pack = EmblemService.IsEmblem(data, group.Key) ? 5 : ConfigPack(row);
            int limit = GameData.Int(row, "_iType") == ItemTypeEquip ? 1 : (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
            // Bound and unbound grants cannot share a stack (AddItemCore / CanStackGrant).
            foreach (var binding in group.GroupBy(g => g.Bind))
            {
                long free = session.Items.Where(i => CanStackGrant(i, group.Key, limit, binding.Key)).Sum(i => (long)(limit - i.Stack));
                long needed = Math.Max(0, binding.Sum(g => (long)g.Count) - free);
                addedSlots[pack] = addedSlots.GetValueOrDefault(pack) + (needed + limit - 1) / limit;
            }
        }
        foreach (var item in exactGoods ?? [])
        {
            if (item.Stack <= 0 || item.Grid != 0 || data.Row("ItemConfig", item.Entry) is not { } row
                || GameData.Int(row, "_iType") == ItemTypeCurrency) return ErrorCodes.ItemNoConfig;
            int pack = EmblemService.IsEmblem(data, item.Entry) ? 5 : ConfigPack(row);
            addedSlots[pack] = addedSlots.GetValueOrDefault(pack) + 1;
        }
        foreach (var (entry, amount) in currencyGoods ?? new Dictionary<int, long>())
        {
            if (amount <= 0 || data.Row("ItemConfig", entry) is not { } row
                || GameData.Int(row, "_iType") != ItemTypeCurrency) return ErrorCodes.ItemNoConfig;
            long other = currency.GetValueOrDefault(entry);
            if (amount > long.MaxValue - other) return ErrorCodes.Parameter;
            currency[entry] = other + amount;
        }
        foreach (var (entry, amount) in currency)
        {
            long current = session.Progress!.Currency.GetValueOrDefault(entry);
            if (current < 0 || amount > long.MaxValue - current) return ErrorCodes.Parameter;
        }
        foreach (var (pack, slots) in addedSlots)
            if (PackCapacity(pack) is var capacity
                && (capacity <= 0 || session.Items.Count(i => i.Grid == 0 && i.Pack == pack) + slots > capacity))
                return ErrorCodes.BagFull;
        return 0;
    }

    private static long NextSort(GameSession session, int pack) =>
        session.Items.Where(i => i.Grid == 0 && i.Pack == pack).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;

    /// <summary>ItemBaseInfo as the client parses it: itemInfos must not be empty; equipment must be appraised to be worn.</summary>
    private ItemBaseInfo ItemInfo(InvItem item)
    {
        // itemBindInfo present (even empty): the bag's sort compares itemBindType of both items, nil < nil throws.
        var info = new ItemBaseInfo
        {
            ItemGuid = item.Guid, ItemEntry = item.Entry, Stack = item.Stack, IsLocked = item.IsLocked,
            ItemBindInfo = new ItemBindInfo { ItemBindType = (ItemBindType)item.BindType, ExpireTimeStamp = item.BindExpire },
        };
        info.ItemInfos[AttrSetUpSource] = new ItemAttrValue { LongVal = 0 };
        if (IsEquipment(item.Entry)) info.ItemInfos[AttrAppraise] = new ItemAttrValue { LongVal = IsAppraised(item) ? 1 : 0 };
        if (item.Attrs != null)
            foreach (var (key, value) in item.Attrs) info.ItemInfos[key] = new ItemAttrValue { LongVal = value };
        if (item.StringAttrs != null)
            foreach (var (key, value) in item.StringAttrs) info.ItemInfos[key] = new ItemAttrValue { StrVal = value };
        if (IsEquipment(item.Entry)) info.ItemInfos[EquipmentRating.ScoreKey] = new ItemAttrValue { LongVal = EquipmentRating.Calculate(item, data) };
        if (IsCard(item.Entry))
            foreach (var key in new[] { AttrCardLevel, AttrCardStar, AttrCardType })
                if (!info.ItemInfos.ContainsKey(key)) info.ItemInfos[key] = new ItemAttrValue { LongVal = 0 };
        foreach (var (id, enchant) in item.Enchantments)
        {
            var value = new EnchantAttr { GetTime = enchant.GetTime };
            value.Attr.Add(enchant.Attr);
            info.Enchantment[id] = value;
        }
        return info;
    }
}
