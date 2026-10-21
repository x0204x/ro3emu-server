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
    // ---- bag: remove, pay and exchange ----

    /// <summary>Removes up to <paramref name="count"/> of an item id from the bag (GM).</summary>
    public async Task<string> RemoveItemAsync(GameSession session, int entry, int count)
    {
        using var _ = await LockAsync(session);
        var progress = CloneProgress(session.Progress!);
        if (data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency)
        {
            long previous = progress.Currency.GetValueOrDefault(entry);
            long next = Math.Max(0, previous - count);
            progress.Currency[entry] = next;
            await CommitCharacterAsync(session, "gm-remove", progress: progress,
                costs: previous > next ? [(entry, previous - next)] : [],
                goods: next > previous ? [(entry, checked((int)(next - previous)))] : []);
            await SyncItemsAsync(session);
            if (next != previous) foreach (var handler in StateChanged) handler(session);
            return $"{data.ItemName(entry)}: {next}";
        }
        var staged = session.Items.Select(i => i.Clone()).ToList();
        int stacks = 0;
        long consumed = 0;
        foreach (var item in staged.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).ToArray())
        {
            if (count <= 0) break;
            int take = Math.Min(count, item.Stack);
            count -= take;
            consumed += take;
            item.Stack -= take;
            if (item.Stack == 0) staged.Remove(item);
            stacks++;
        }
        await CommitCharacterAsync(session, "gm-remove", nextItems: staged,
            costs: consumed > 0 ? [(entry, consumed)] : []);
        await SyncItemsAsync(session);
        if (stacks > 0) foreach (var handler in StateChanged) handler(session);
        return $"Removed {stacks} stack(s) of {data.ItemName(entry)}.";
    }

    /// <summary>Owned amount of a currency, or of an item in the bag (not worn or socketed).</summary>
    public long CountOf(GameSession session, int entry) =>
        data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency
            ? session.Progress!.Currency.GetValueOrDefault(entry)
            : session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).Sum(i => (long)i.Stack);

    /// <summary>
    /// Why <see cref="ExchangeAsync"/> would refuse a cost: a short currency is 192 "Insufficient Currency", a short bag
    /// item 154 "Not enough required items".
    /// </summary>
    public int ShortageOf(GameSession session, IReadOnlyList<(int Entry, long Count)> cost)
    {
        foreach (var g in cost.GroupBy(c => c.Entry))
        {
            long need = g.Sum(c => c.Count);
            if (need < 0) return ErrorCodes.Parameter;
            if (CountOf(session, g.Key) >= need) continue;
            return data.Row("ItemConfig", g.Key) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency ? ErrorCodes.CurrencyShort : ErrorCodes.ItemsShort;
        }
        return ErrorCodes.ItemsShort;
    }

    /// <summary>
    /// Bounded 83550 payment path. Caller holds <see cref="Lock"/>. Stage cost-only bag/currency changes, then commit
    /// those rows and the next 5050 blob in one SQLite transaction; publish in-memory balances only after commit.
    /// Other bag exchanges use the transaction in <see cref="ExchangeLockedAsync"/>.
    /// </summary>
    internal bool PayTalentLocked(GameSession session, IReadOnlyList<(int Entry, long Count)> cost, DBSeasonTalent next)
    {
        var need = cost.GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: g.Sum(c => c.Count))).ToList();
        if (need.Any(c => c.Count <= 0 || CountOf(session, c.Entry) < c.Count)) return false;
        var currency = new Dictionary<int, long>(session.Progress!.Currency);
        var stacks = new Dictionary<long, int>();
        var removed = new HashSet<long>();
        foreach (var (entry, count) in need)
        {
            if (data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency)
            {
                currency[entry] = currency.GetValueOrDefault(entry) - count;
                continue;
            }
            long left = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).OrderBy(i => i.Stack))
            {
                if (left == 0) break;
                int take = (int)Math.Min(left, item.Stack);
                int rest = item.Stack - take;
                if (rest == 0) removed.Add(item.Guid);
                else stacks[item.Guid] = rest;
                left -= take;
            }
            if (left != 0) return false;
        }

        roles.CommitTalentPurchase(session.Role!.Id, next.ToByteArray(), currency, stacks, removed, itemStore, session.Progress);
        session.Progress.Currency.Clear();
        foreach (var (entry, count) in currency) session.Progress.Currency[entry] = count;
        foreach (var item in session.Items.ToList())
        {
            if (removed.Contains(item.Guid)) session.Items.Remove(item);
            else if (stacks.TryGetValue(item.Guid, out int stack)) item.Stack = stack;
        }
        return true;
    }

    /// <summary>
    /// Pays costs and grants goods in one transaction. With <paramref name="persist"/>, caller state joins the same
    /// commit. Auxiliary ItemGained hooks and packets follow commit. Returns null without mutation for a short payment.
    /// </summary>
    public async Task<List<InvItem>?> ExchangeAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyDictionary<long, int>? instances = null, bool popup = true, bool sync = true,
        Action<RoleItemPlanBuilder>? persist = null, Action? committed = null, RewardSettlement? rewardSettlement = null,
        EconomyMutation? economy = null)
    {
        using var _ = await LockAsync(session);
        return await ExchangeLockedAsync(session, cost, goods, instances, popup, sync, persist, committed: committed,
            rewardSettlement: rewardSettlement, economy: economy);
    }

    /// <summary>
    /// The emblem upgrade's exchange: item-only payment and the target's new attrs committed together, before either is
    /// published to the session.
    /// </summary>
    internal Task<List<InvItem>?> ExchangeUnderLockAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyDictionary<long, int>? instances = null, bool popup = true, bool sync = true,
        (InvItem Item, Dictionary<int, long> Attrs)? itemUpdate = null) =>
        ExchangeLockedAsync(session, cost, goods, instances, popup, sync, itemUpdate: itemUpdate);

    /// <summary>A paid/refunded card cost with the original item's binding and card type.</summary>
    internal readonly record struct AttributedGood(int Entry, int Count, int BindType, long BindExpire, int CardType);

    /// <summary>
    /// ExchangeAsync with StateLock already held. Costs, grants and an optional caller mutation share one transaction;
    /// in-memory payment state is restored on failure, before any sync.
    /// Card operations reserve their target (<paramref name="reservedGuid"/>) from material consumption and return
    /// refunded copies with their recorded binding (<paramref name="attributedGoods"/>).
    /// Dungeon <paramref name="boundGoods"/> use normal item generation while preserving DropGroupConfig binding.
    /// Virtual mail rewards 100/101 use <paramref name="baseExp"/>/<paramref name="jobExp"/>:
    /// stage progression before payment, persist it with the mailbox claim, and publish only after commit.
    /// </summary>
    internal async Task<List<InvItem>?> ExchangeLockedAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyDictionary<long, int>? instances = null, bool popup = true, bool sync = true, Action<RoleItemPlanBuilder>? persist = null,
        long reservedGuid = 0, IReadOnlyList<AttributedGood>? attributedGoods = null,
        (InvItem Item, Dictionary<int, long> Attrs)? itemUpdate = null,
        IReadOnlyList<(int Entry, int Count, int Bind)>? boundGoods = null,
        IReadOnlyList<InvItem>? exactGoods = null, IReadOnlyDictionary<int, long>? currencyGoods = null,
        long baseExp = 0, long jobExp = 0, Action? committed = null,
        bool asyncCommit = false, Action? appliedBeforeNotify = null, RewardSettlement? rewardSettlement = null,
        Action<List<InvItem>>? stageItems = null, Action<List<InvItem>>? stageAfterGoods = null,
        EconomyMutation? economy = null)
        => await ExchangeOwnedCoreAsync(session, RequireOwnedRole(session), cost, goods, instances, popup, sync,
            persist, reservedGuid, attributedGoods, itemUpdate, boundGoods, exactGoods, currencyGoods, baseExp, jobExp, committed,
            asyncCommit, appliedBeforeNotify, rewardSettlement, stageItems, stageAfterGoods, economy);

    /// <summary>Checked owned Core for the complete payment, grant and optional caller plan.</summary>
    private async Task<List<InvItem>?> ExchangeOwnedCoreAsync(GameSession session, Ownership.OwnerContext context,
        IReadOnlyList<(int Entry, long Count)> cost, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyDictionary<long, int>? instances, bool popup, bool sync, Action<RoleItemPlanBuilder>? persist,
        long reservedGuid, IReadOnlyList<AttributedGood>? attributedGoods,
        (InvItem Item, Dictionary<int, long> Attrs)? itemUpdate,
        IReadOnlyList<(int Entry, int Count, int Bind)>? boundGoods,
        IReadOnlyList<InvItem>? exactGoods, IReadOnlyDictionary<int, long>? currencyGoods,
        long baseExp, long jobExp, Action? committed, bool asyncCommit, Action? appliedBeforeNotify, RewardSettlement? rewardSettlement,
        Action<List<InvItem>>? stageItems, Action<List<InvItem>>? stageAfterGoods, EconomyMutation? economy)
    {
        context.AssertRole(session.Role!.Id);
        context.BindPresentation(session);
        var attachment = session.Attachment;
        var lifeUse = LifeItemExchange.Value;
        if (lifeUse is null || !lifeUse.Active || !ReferenceEquals(lifeUse.Session, session) || lifeUse.Committed) lifeUse = null;
        if (baseExp < 0 || jobExp < 0) throw new ArgumentOutOfRangeException(nameof(baseExp));
        var nextProgress = CloneProgress(session.Progress!);
        if (baseExp > 0 || jobExp > 0) ApplyExp(nextProgress, baseExp, jobExp);
        var need = cost.GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: g.Sum(c => c.Count))).ToList();
        long Available(int entry) => CountOf(session, entry) -
            (session.Items.Any(i => i.Guid == reservedGuid && i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)) ? 1 : 0);
        if (need.Any(c => c.Count < 0 || Available(c.Entry) < c.Count)) return null;
        var picked = new List<(InvItem Item, int Count)>();
        foreach (var (guid, count) in instances ?? new Dictionary<long, int>())
        {
            if (session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0) is not { } item || EmblemService.IsReserved(session, item) || TotemService.IsReserved(session, item) || count < 1 || count > item.Stack - (guid == reservedGuid ? 1 : 0)) return null;
            picked.Add((item, count));
        }
        // Explicit GUID costs and entry costs share the same bag: reserve both before consuming either.
        if (need.Any(c => Available(c.Entry) - picked.Where(p => p.Item.Entry == c.Entry).Sum(p => (long)p.Count) < c.Count)) return null;
        if (itemUpdate is { } update)
        {
            if ((!asyncCommit && persist != null) || goods.Count != 0 || attributedGoods is { Count: > 0 } || boundGoods is { Count: > 0 }
                || exactGoods is { Count: > 0 } || currencyGoods is { Count: > 0 } ||
                baseExp != 0 || jobExp != 0 ||
                need.Any(c => data.Row("ItemConfig", c.Entry) is { } r && GameData.Int(r, "_iType") == ItemTypeCurrency))
                throw new InvalidOperationException("An item update requires an item-only exchange.");
            if (!session.Items.Contains(update.Item) || picked.Any(selected => selected.Item.Guid == update.Item.Guid)
                || need.Any(c => c.Entry == update.Item.Entry && c.Count > 0)) return null;
            if (!asyncCommit)
            {
                ApplyItemExchange(session, need, picked, update);
                if (sync) await SyncItemsAsync(session);
                return [];
            }
        }
        var p = session.Progress!;
        var reward = new Romsg.GetRewardNotify { TargetUid = session.Role!.Id };
        var granted = new List<InvItem>();
        var postCommitHooks = new List<(int Entry, int Count)>();
        var decomposed = new ItemAutoDecomposeTipsNtf();
        // Stage under the checked owner and old StateLock, but restore the published owner before
        // admitting the writer command. Callers can retain references to surviving card/equipment items.
        var originalItems = session.Items;
        var originalItemReferences = originalItems.ToArray();
        var originalNewItems = session.NewItems.ToArray();
        var beforeProgress = CloneProgress(p);
        var beforeItems = originalItems.Select(item => item.Clone()).ToArray();
        var stagedItems = originalItems.Select(item => item.Clone()).ToList();
        var stagedProgress = CloneProgress(p);
        var stagedPicked = picked.Select(choice =>
            (stagedItems.Single(item => item.Guid == choice.Item.Guid), choice.Count)).ToList();
        var originalProgress = (p.Level, p.Exp, p.JobLevel, p.JobExp);
        RoleItemPlanBuilder builder;
        int itemReceipt;
        FirstCardSightPlan? cardSight;
        DBEquipInfo? prunedPlans = null;
        session.Items = stagedItems;
        session.Progress = stagedProgress;
        try
        {
            PayExchange(session, need, stagedPicked, reservedGuid, persist: false);
            stageItems?.Invoke(stagedItems);
            if (itemUpdate is { } stagedUpdate)
                stagedItems.Single(item => item.Guid == stagedUpdate.Item.Guid).Attrs = new(stagedUpdate.Attrs);
            foreach (var (entry, count, bind) in goods.Select(g => (g.Entry, g.Count, Bind: 0)).Concat(boundGoods ?? []).Where(g => g.Count > 0))
            {
                bool currency;
                foreach (var item in AddItemCore(session, entry, count, out currency, persist: false, notifyItemGained: false, bind: bind))
                {
                    granted.Add(item);
                }
                if (!currency) postCommitHooks.Add((entry, count));
                reward.Rewards.Add(ItemInfo(new InvItem { Entry = entry, Stack = count, BindType = bind }));
                reward.RewardType.Add(1);
            }
            foreach (var grant in attributedGoods ?? [])
            {
                foreach (var item in AddAttributedCore(session, grant, persist: false))
                {
                    granted.Add(item);
                }
                if (grant.Count > 0 && data.Row("ItemConfig", grant.Entry) is { } row && GameData.Int(row, "_iType") != ItemTypeCurrency)
                    postCommitHooks.Add((grant.Entry, grant.Count));
            }
            foreach (var source in exactGoods ?? [])
            {
                var row = data.Row("ItemConfig", source.Entry)!.Value;
                int pack = EmblemService.IsEmblem(data, source.Entry) ? 5 : ConfigPack(row);
                var item = source.Clone();
                item.Guid = 0; // (inferred) claimed copy has a fresh bag identity.
                item.Grid = 0;
                item.Pack = pack;
                item.Sort = NextSort(session, pack);
                session.Items.Add(item);
                granted.Add(item);
                postCommitHooks.Add((item.Entry, item.Stack));
            }
            foreach (var (entry, amount) in currencyGoods ?? new Dictionary<int, long>())
                stagedProgress.Currency[entry] = checked(stagedProgress.Currency.GetValueOrDefault(entry) + amount);
            if (baseExp > 0 || jobExp > 0)
                (stagedProgress.Level, stagedProgress.Exp, stagedProgress.JobLevel, stagedProgress.JobExp) =
                    (nextProgress.Level, nextProgress.Exp, nextProgress.JobLevel, nextProgress.JobExp);
            if (rewardSettlement?.AutoDecompose == true)
            {
                decomposed = StageAutoDecomposeAward(session, stagedItems, stagedProgress, granted,
                    beforeItems.ToDictionary(i => i.Guid, i => i.Stack), postCommitHooks);
                granted.RemoveAll(item => !stagedItems.Contains(item));
            }
            // Tower clear goods retain their existing non-auto-decompose rule but join this kill's one commit.
            rewardSettlement?.BeforeAdditionalGoods?.Invoke();
            foreach (var (entry, count) in rewardSettlement?.AdditionalGoods ?? [])
            {
                foreach (var item in AddItemCore(session, entry, count, out _, persist: false, notifyItemGained: false))
                    granted.Add(item);
                if (!IsCurrency(entry)) postCommitHooks.Add((entry, count));
            }
            stageAfterGoods?.Invoke(stagedItems);
            builder = roles.NewTransaction();
            itemReceipt = builder.ItemDelta(session.Role!.Id, beforeItems, stagedItems);
            builder.Progress(session.Role.Id, stagedProgress);
            persist?.Invoke(builder);
            lifeUse?.Persist(builder);
            cardSight = StageFirstCardSight?.Invoke(session, postCommitHooks.Select(g => g.Entry).ToList(),
                rewardSettlement?.LibraryMonster ?? 0);
            cardSight?.Persist(builder);
            if (builder.HasCharacterFamilyEconomy && (prunedPlans = PrunedEquipmentPlans(session, stagedItems)) != null)
                builder.RoleBlob(session.Role.Id, "equip_plans", prunedPlans.ToByteArray());
            rewardSettlement?.EnlistAfterCards?.Invoke(builder);
            if (rewardSettlement != null && economy != null)
                throw new InvalidOperationException("Reward settlement already supplies its economy envelope.");
            if (economy != null) builder.WithEconomy(economy);
            if (rewardSettlement != null)
                builder.WithEconomy(RewardMutation(session, rewardSettlement, beforeProgress, stagedProgress,
                    need, goods.Concat((boundGoods ?? []).Select(g => (g.Entry, g.Count)))
                        .Concat(rewardSettlement.AdditionalGoods).ToArray(),
                    baseExp, jobExp, decomposed));
        }
        finally
        {
            session.Items = originalItems;
            session.Progress = p;
        }
        void PublishExchange(object?[] receipts)
        {
            try
            {
                ItemSnapshotWritePlan.ApplyGuids(stagedItems, (long[])receipts[itemReceipt]!);
                // Apply the complete committed snapshot without replacing surviving item objects held by callers.
                var surviving = originalItems.ToDictionary(item => item.Guid);
                var mapped = new Dictionary<InvItem, InvItem>(ReferenceEqualityComparer.Instance);
                originalItems.Clear();
                foreach (var item in stagedItems)
                {
                    if (surviving.TryGetValue(item.Guid, out var live))
                    {
                        CopyItem(live, item);
                        mapped[item] = live;
                        originalItems.Add(live);
                    }
                    else { mapped[item] = item; originalItems.Add(item); }
                }
                for (int i = 0; i < granted.Count; i++) granted[i] = mapped[granted[i]];
                CopyProgress(p, stagedProgress);
                if (rewardSettlement != null) rewardSettlement.Committed = true;
                foreach (var item in granted) session.NewItems.Add(item.Guid);
                cardSight?.Apply();
                if (prunedPlans != null) session.EquipmentPlans = prunedPlans;
                if (lifeUse != null)
                {
                    lifeUse.Committed = true;
                    lifeUse.Publish();
                }
                // Publish the owner's committed in-memory state before any notification can yield or fail.
                committed?.Invoke();
                appliedBeforeNotify?.Invoke();
            }
            catch
            {
                // A failed Apply cannot undo SQL. Restore the previous bag/progress view; the async
                // commit adapter quarantines the owner before any other operation can use that view.
                originalItems.Clear();
                for (int i = 0; i < originalItemReferences.Length; i++)
                {
                    CopyItem(originalItemReferences[i], beforeItems[i]);
                    originalItems.Add(originalItemReferences[i]);
                }
                CopyProgress(p, beforeProgress);
                session.NewItems.Clear();
                foreach (long guid in originalNewItems) session.NewItems.Add(guid);
                throw;
            }
        }
        try
        {
            // The specialized item-use scope also carries Mount/Wardrobe cooldowns. Those
            // converted family envelopes enlist bag_state; ordinary Life exchanges stay guarded.
            if (asyncCommit && lifeUse != null && !builder.HasAsyncFamilyEconomy)
                throw new InvalidOperationException("Life exchange is not an asynchronous owner family.");
            // Converted family envelopes, E04 rewards and 13e owned grants use owned admission. E05a shops opt the original
            // E01 receipt shape into that path with asyncCommit; other legacy callers retain the sync adapter.
            if (builder.HasEconomy && !builder.HasAsyncFamilyEconomy && !asyncCommit && rewardSettlement == null && economy == null)
            {
                var receipts = roles.Commit(builder, "character/exchange");
                try { PublishExchange(receipts); }
                catch (Exception error)
                {
                    // SQL has committed. The restored live snapshot must not admit another command or cleanup save.
                    if (session.Owner is { } owner) owner.State = Ownership.RoleOwnerState.RecoveryRequired;
                    session.CancelPendingCharacterIntents();
                    session.AbortForRecovery();
                    log.LogError(error, "Committed NPC purchase publication failed; role {Role} requires recovery", session.Role!.Id);
                    throw;
                }
            }
            else await roles.CommitAsync(builder, "character/exchange", PublishExchange);
        }
        catch (EconomyOutcomeUnknownException error)
        {
            // E01: neither snapshot is safe to publish/save if durable receipt recovery is unavailable.
            if (session.Owner is { } owner) owner.State = Ownership.RoleOwnerState.RecoveryRequired;
            session.CancelPendingCharacterIntents();
            session.AbortForRecovery();
            log.LogError(error, "NPC purchase {Operation} has an unresolved durable outcome; role {Role} requires recovery",
                error.OperationId, session.Role!.Id);
            throw;
        }
        async Task PresentExchangeAsync()
        {
            if (attachment?.Cancelled == true || !ReferenceEquals(session.Attachment, attachment)) return;
            // A hook may persist auxiliary state on another connection. A hook failure cannot undo the committed grant.
            foreach (var (entry, count) in postCommitHooks)
                foreach (var handler in ItemGained)
                    try { handler(session, entry, count); }
                    catch (Exception e) { log.LogError(e, "ItemGained hook failed after committed exchange for item {Entry}", entry); }
            if (sync) await SyncItemsAsync(session); // else the caller sends 5009 (ItemsChangedAsync) when it fits
            if (decomposed.DecomposeItemInfo.Count > 0)
                await session.SendGameAsync(EquipmentService.ItemAutoDecomposeNotice, decomposed);
            if (baseExp > 0 || jobExp > 0)
            {
                if (rewardSettlement?.CommandKind == "monster-kill")
                {
                    var floatReward = new Romsg.GetRewardNotify { TargetUid = session.Role!.Id, RewardType = { 1 } };
                    AddVirtualReward(floatReward, BaseExpItem, baseExp);
                    AddVirtualReward(floatReward, JobExpItem, jobExp);
                    await session.SendGameAsync(GetRewardNotify, floatReward);
                }
                if (p.Level != originalProgress.Level || p.JobLevel != originalProgress.JobLevel) await AfterLevelChangeAsync(session, persist: false);
                else await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
                AddVirtualReward(reward, BaseExpItem, baseExp);
                AddVirtualReward(reward, JobExpItem, jobExp);
                if (reward.RewardType.Count == 0) reward.RewardType.Add(1);
            }
            if (popup && reward.Rewards.Count > 0) await session.SendGameAsync(GetRewardNotify, reward);
            // LifeJob publishes its committed life/restriction state after Exchange returns. Its ItemsChangedAsync
            // runs this first notification once those fields agree with the already committed bag and cooldown.
            if (lifeUse != null) lifeUse.PendingStateChanged = StateChanged.ToArray();
            else foreach (var handler in StateChanged) handler(session);
        }
        if (rewardSettlement?.DeferPresentation == true) rewardSettlement.Presentation = PresentExchangeAsync;
        else await PresentExchangeAsync();
        return granted;
    }

    private void PayExchange(GameSession session, List<(int Entry, long Count)> need, List<(InvItem Item, int Count)> picked,
        long reservedGuid = 0, bool persist = true)
    {
        var changed = new List<InvItem>();
        var removed = new List<InvItem>();
        foreach (var (item, count) in picked) TakeFromStack(session, item, count, changed, removed, persist: persist);
        foreach (var (entry, count) in need)
        {
            if (count == 0) continue;
            if (data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency)
            {
                session.Progress!.Currency[entry] -= count;
                continue;
            }
            long left = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i))
                .OrderBy(i => i.Guid == reservedGuid).ThenBy(i => i.Stack).ToList())
            {
                if (left <= 0) break;
                int take = (int)Math.Min(left, item.Stack - (item.Guid == reservedGuid ? 1 : 0));
                if (take == 0) continue;
                left -= take;
                TakeFromStack(session, item, take, changed, removed, persist: persist);
            }
            if (left != 0) throw new InvalidOperationException("Exchange cost changed after validation.");
        }
    }

    /// <summary>Return paid copies with their recorded bind/type, stacking only identical base copies.</summary>
    private List<InvItem> AddAttributedCore(GameSession session, AttributedGood grant, bool persist = true)
    {
        if (grant.Count <= 0) return [];
        var row = data.Row("ItemConfig", grant.Entry)!.Value;
        if (GameData.Int(row, "_iType") == ItemTypeCurrency)
        {
            session.Progress!.Currency[grant.Entry] = session.Progress.Currency.GetValueOrDefault(grant.Entry) + grant.Count;
            return [];
        }
        int pack = ConfigPack(row);
        int limit = (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
        int remaining = grant.Count;
        var changed = new List<InvItem>();
        foreach (var item in session.Items.Where(i => i.Grid == 0 && !i.IsLocked && i.Enchantments.Count == 0
            && i.Entry == grant.Entry && i.BindType == grant.BindType
            && i.BindExpire == grant.BindExpire && i.Stack < limit && (i.StringAttrs?.Count ?? 0) == 0
            && (i.Attrs?.GetValueOrDefault(530) ?? 0) == grant.CardType
            && (i.Attrs == null || i.Attrs.All(a => (a.Key is 501 or 502 or 530) && a.Value == (a.Key == 530 ? grant.CardType : 0)))))
        {
            int add = Math.Min(remaining, limit - item.Stack);
            item.Stack += add;
            remaining -= add;
            if (persist) itemStore.Update(item);
            changed.Add(item);
            if (remaining == 0) break;
        }
        while (remaining > 0)
        {
            int count = Math.Min(remaining, limit);
            var item = new InvItem
            {
                Entry = grant.Entry, Stack = count, Pack = pack, Sort = NextSort(session, pack),
                BindType = grant.BindType, BindExpire = grant.BindExpire,
                Attrs = grant.CardType == 0 ? null : new Dictionary<int, long>
                { [501] = 0, [502] = 0, [530] = grant.CardType },
            };
            if (persist) itemStore.Insert(session.Role!.Id, item);
            session.Items.Add(item);
            changed.Add(item);
            remaining -= count;
        }
        return changed;
    }

    private void ApplyItemExchange(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(InvItem Item, int Count)> picked, (InvItem Item, Dictionary<int, long> Attrs) update)
    {
        var taken = picked.ToDictionary(p => p.Item.Guid, p => p.Count);
        foreach (var (entry, count) in cost)
        {
            long left = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).OrderBy(i => i.Stack))
            {
                if (left == 0) break;
                int used = taken.GetValueOrDefault(item.Guid);
                int take = (int)Math.Min(left, item.Stack - used);
                if (take == 0) continue;
                taken[item.Guid] = used + take;
                left -= take;
            }
        }
        var changed = new List<InvItem>();
        foreach (var (guid, count) in taken)
        {
            var item = session.Items.Single(i => i.Guid == guid);
            changed.Add(Copy(item, item.Stack - count, item.Attrs));
        }
        changed.Add(Copy(update.Item, update.Item.Stack, update.Attrs));
        itemStore.ApplyChanges(session.Role!.Id, changed);
        // No session mutation occurs if any SQL statement or the commit fails.
        foreach (var item in changed)
        {
            var original = session.Items.Single(i => i.Guid == item.Guid);
            if (item.Stack == 0) session.Items.Remove(original);
            else { original.Stack = item.Stack; original.Attrs = item.Attrs; }
        }

        static InvItem Copy(InvItem item, int stack, Dictionary<int, long>? attrs)
        {
            var copy = item.Clone();
            copy.Stack = stack;
            copy.Attrs = attrs;
            return copy;
        }
    }

    /// <summary>
    /// Pet exchange under the caller's StateLock: stage on copied items, then atomically persist the bag, currencies
    /// and next DBPetInfo before publishing any packets (inferred transaction invariant).
    /// The ordinary exchange overload retains its existing locking/sync behavior. Pet goods are configured items.
    /// </summary>
    internal Task<List<InvItem>?> ExchangeAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(int Entry, int Count)> goods, DBPetInfo nextPets)
    {
        var need = cost.GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: checked(g.Sum(c => c.Count)))).ToList();
        if (need.Any(c => c.Count < 0 || data.Row("ItemConfig", c.Entry) == null || CountOf(session, c.Entry) < c.Count)
            || goods.Any(g => g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null))
            return Task.FromResult<List<InvItem>?>(null);
        var before = session.Items;
        var currency = session.Progress!.Currency.ToDictionary();
        session.Items = before.Select(i => i.Clone()).ToList(); // every per-instance field survives the staging
        var granted = new List<InvItem>();
        try
        {
            foreach (var (entry, count) in need)
            {
                if (GameData.Int(data.Row("ItemConfig", entry)!.Value, "_iType") == ItemTypeCurrency)
                    session.Progress.Currency[entry] = session.Progress.Currency.GetValueOrDefault(entry) - count;
                else
                {
                    long left = count;
                    foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).OrderBy(i => i.Stack).ToList())
                    {
                        if (left == 0) break;
                        int take = (int)Math.Min(left, item.Stack);
                        TakeFromStack(session, item, take, [], [], persist: false);
                        left -= take;
                    }
                }
            }
            foreach (var (entry, count) in goods)
                granted.AddRange(AddItemCore(session, entry, count, out _, persist: false));
            roles.SavePetExchange(session.Role!.Id, nextPets.ToByteArray(), session.Progress.Currency, before, session.Items, session.Progress);
        }
        catch
        {
            session.Items = before;
            session.Progress.Currency.Clear();
            foreach (var (id, count) in currency) session.Progress.Currency[id] = count;
            throw;
        }
        session.Pets = nextPets;
        foreach (var item in granted) session.NewItems.Add(item.Guid);
        foreach (var (entry, count) in goods)
            foreach (var handler in ItemGained) handler(session, entry, count);
        return Task.FromResult<List<InvItem>?>(granted);
    }
}
