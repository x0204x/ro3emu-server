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
    // ---- bag: grants and award application ----

    /// <summary>Adds items (or a currency, or exp for the virtual items 100/101) and tells the client.</summary>
    public async Task<string> AddItemAsync(GameSession session, int entry, int count)
    {
        if (data.Row("ItemConfig", entry) is not { } row) throw new ArgumentException("unknown item");
        if (entry == BaseExpItem) return await AddExpAsync(session, count, 0);
        if (entry == JobExpItem) return await AddExpAsync(session, 0, count);
        using var _ = await LockAsync(session);
        count = Math.Clamp(count, 1, GameData.Int(row, "_iType") == ItemTypeCurrency ? int.MaxValue : 999);
        // C/D 13a: an exchange keeps the live InvItem instances (the award path would replace them).
        await ExchangeLockedAsync(session, [], [(entry, count)],
            persist: tx => tx.WithEconomy(FamilyEconomy(session, "cd13a/character-gm-grant", [], [(entry, count)],
                new { Entry = entry, Count = count })));
        return $"+{count} {data.ItemName(entry)} ({entry})";
    }

    /// <summary>Loot: several items / currencies at once, one bag sync, marked new (no float: the drop notify shows them).</summary>
    public Task GrantAsync(GameSession session, IReadOnlyList<(int Entry, int Count)> items, Func<bool>? stillOwner = null) =>
        GrantBoundAsync(session, items.Select(i => (i.Entry, i.Count, Bind: 0)).ToList(), stillOwner);

    /// <summary>Quest grants preserve AwardConfig's bind flag; bound and unbound stacks remain separate.</summary>
    public async Task GrantBoundAsync(GameSession session, IReadOnlyList<(int Entry, int Count, int Bind)> items,
        Func<bool>? stillOwner = null)
    {
        if (stillOwner?.Invoke() == false) return;
        using var held = await LockIntentAsync(session, stillOwner);
        if (held == null) return;
        if (stillOwner?.Invoke() == false) return;
        var plan = new AwardPlan(0, 0, items.Where(x => x.Count > 0)
            .Select(x => new AwardGrant(x.Entry, x.Count, x.Bind)));
        if (plan.Grants.Count == 0) return;
        var source = new OwnedAwardSource("cd13e/grant", $"grant/{session.Role!.Id}/{Guid.NewGuid():N}",
            new { Version = 1, Grants = items });
        if (!await ApplyAwardAsync(session, plan, stateLockHeld: true, ownedAward: source,
            suppressRewardNotice: true))
            throw new InvalidOperationException("Owned bound grant was not committed.");
    }

    /// <summary>
    /// A stage reward grant (31178/31175, any type) is staged in copies and committed with its claim blob on one SQLite connection. No
    /// session mutation or success packet precedes commit; a failed transaction leaves the old state intact.
    /// </summary>
    public async Task<bool> GrantDailyStageAsync(GameSession session, IReadOnlyList<(int Entry, int Count, int Bind)> goods,
        byte[] previousDaily, byte[] nextDaily, DBTaskInfo? nextTasks = null, byte[]? expectedTasks = null, long questExp = 0)
    {
        using var held = await LockAsync(session);
        if (session.Daily == null || !JsonSerializer.SerializeToUtf8Bytes(session.Daily).AsSpan().SequenceEqual(previousDaily)
            || expectedTasks != null && (session.Tasks == null || !session.Tasks.ToByteArray().AsSpan().SequenceEqual(expectedTasks)))
            return false;
        var before = JsonSerializer.Deserialize<DailyActivityService.DailyState>(previousDaily);
        var after = JsonSerializer.Deserialize<DailyActivityService.DailyState>(nextDaily);
        if (before?.Claims == null || after?.Claims == null || before.DailyEpoch != after.DailyEpoch ||
            after.Claims.Except(before.Claims).Order().ToArray() is not { Length: > 0 } stages ||
            before.Claims.Except(after.Claims).Any() || questExp < 0 ||
            goods.Any(g => g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null)) return false;
        var plan = new AwardPlan(0, 0, goods.Select(g => new AwardGrant(g.Entry, g.Count, g.Bind)));
        // One stage (31178) or a batch (31175): the batch's source names all of its stages.
        object outcome = stages is [int stage]
            ? new { Version = 1, Stage = stage, Epoch = after.DailyEpoch, Grants = goods, QuestExp = questExp }
            : new { Version = 1, Stages = stages, Epoch = after.DailyEpoch, Grants = goods, QuestExp = questExp };
        var source = new OwnedAwardSource("cd13e/daily-stage",
            $"daily-stage/{session.Role!.Id}/{after.DailyEpoch}/{string.Join(",", stages)}", outcome);
        try
        {
            return await ApplyAwardAsync(session, plan, taskBaseExp: questExp,
                deliveredTaskState: nextTasks?.ToByteArray(), expectedTasks: expectedTasks,
                expectedDaily: previousDaily, nextDaily: nextDaily, stateLockHeld: true, ownedAward: source);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            log.LogError(error, "Daily stage transaction failed for {Role}", session.Role?.Id);
            return false;
        }
    }
    /// <summary>
    /// Applies an already resolved source award and optional costs/task boundary as one SQLite transaction
    ///. False means a cost was short and nothing changed. A source must supply
    /// prospective deliveredTaskState to make its once-only completion durable with the grant.
    /// </summary>
    public async Task<bool> ApplyAwardAsync(GameSession session, AwardPlan plan, long taskBaseExp = 0,
        IReadOnlyList<(int Entry, int Count)>? costs = null, byte[]? deliveredTaskState = null,
        byte[]? taskRuntimeState = null, int manifestType = 1, byte[]? expectedDaily = null,
        byte[]? nextDaily = null, byte[]? expectedTasks = null, Action<RoleItemPlanBuilder>? persist = null,
        bool stateLockHeld = false, Action? committed = null, EconomyMutation? familyEconomy = null,
        OwnedAwardSource? ownedAward = null, bool suppressRewardNotice = false, IReadOnlyList<int>? rewardIndexes = null)
    {
        using var _ = stateLockHeld ? null : await LockAsync(session);
        var context = RequireOwnedRole(session);
        return await ApplyAwardCoreAsync(session, context, plan, taskBaseExp, costs, deliveredTaskState,
            taskRuntimeState, manifestType, expectedDaily, nextDaily, expectedTasks, persist, committed,
            familyEconomy, ownedAward, suppressRewardNotice, rewardIndexes);
    }

    /// <summary>Checked owned Core: stage copied award values, commit, apply before notices.</summary>
    private async Task<bool> ApplyAwardCoreAsync(GameSession session, Ownership.OwnerContext context, AwardPlan plan,
        long taskBaseExp, IReadOnlyList<(int Entry, int Count)>? costs, byte[]? deliveredTaskState,
        byte[]? taskRuntimeState, int manifestType, byte[]? expectedDaily, byte[]? nextDaily,
        byte[]? expectedTasks, Action<RoleItemPlanBuilder>? persist, Action? committed,
        EconomyMutation? familyEconomy, OwnedAwardSource? ownedAward, bool suppressRewardNotice,
        IReadOnlyList<int>? rewardIndexes = null)
    {
        context.AssertRole(session.Role!.Id);
        context.BindPresentation(session);
        var attachment = session.Attachment;
        if (taskBaseExp < 0) throw new ArgumentOutOfRangeException(nameof(taskBaseExp));
        // PBManifestType.ManifestTypeRunRing (18) needs the slots LC_DAL_Reward uses to stop the wheel.
        if (manifestType is < 1 or > 19 or 15 || manifestType == TaskService.ManifestTypeRunRing && rewardIndexes is not { Count: > 0 })
            throw new ArgumentOutOfRangeException(nameof(manifestType));
        if ((expectedDaily == null) != (nextDaily == null)) throw new ArgumentException("Daily award requires before and after state");
        if (expectedTasks != null && deliveredTaskState == null) throw new ArgumentException("Task comparison requires next task state");
        if (expectedDaily != null && (session.Daily == null
                || !JsonSerializer.SerializeToUtf8Bytes(session.Daily).AsSpan().SequenceEqual(expectedDaily))) return false;
        if (expectedTasks != null && (session.Tasks == null || !session.Tasks.ToByteArray().AsSpan().SequenceEqual(expectedTasks))) return false;
        var nextTasks = deliveredTaskState == null ? null : DBTaskInfo.Parser.ParseFrom(deliveredTaskState);
        var nextRuntime = taskRuntimeState == null ? null : JsonSerializer.Deserialize<TaskRuntimeState>(taskRuntimeState)
            ?? throw new InvalidOperationException("invalid task runtime award state");
        var nextDailyState = nextDaily == null ? null : JsonSerializer.Deserialize<DailyActivityService.DailyState>(nextDaily)
            ?? throw new InvalidOperationException("invalid daily award state");
        var old = session.Progress ?? throw new InvalidOperationException("award requires role progress");
        var staged = CloneProgress(old);
        var oldItemGuids = session.Items.Select(i => i.Guid).ToHashSet();
        var items = session.Items.Select(i => i.Clone()).ToList();
        var expense = (costs ?? []).GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: g.Sum(c => (long)c.Count))).ToList();
        foreach (var (entry, count) in expense)
        {
            if (count < 0 || data.Row("ItemConfig", entry) == null) throw new InvalidOperationException($"invalid award cost {entry} x{count}");
            long owned = IsCurrency(entry) ? staged.Currency.GetValueOrDefault(entry)
                : items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).Sum(i => (long)i.Stack);
            if (owned < count) return false;
        }
        foreach (var (entry, count) in expense)
        {
            if (IsCurrency(entry))
            {
                staged.Currency[entry] -= count;
                continue;
            }
            long remaining = count;
            foreach (var item in items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).OrderBy(i => i.Stack).ToList())
            {
                if (remaining == 0) break;
                int take = (int)Math.Min(remaining, item.Stack);
                item.Stack -= take;
                remaining -= take;
                if (item.Stack == 0) items.Remove(item);
            }
        }
        var beforeGrants = items.Where(i => i.Guid > 0).ToDictionary(i => i.Guid, i => i.Stack);

        long baseExp = taskBaseExp, jobExp = 0;
        var notification = new Romsg.GetRewardNotify { TargetUid = session.Role!.Id };
        var gained = new List<(int Entry, int Count)>();
        var received = new HashSet<InvItem>();
        foreach (var grant in plan.Grants)
        {
            if (grant.Entry <= 0 || grant.Count <= 0 || data.Row("ItemConfig", grant.Entry) == null
                || grant.BindType is < 0 or > 2 || grant.ExpireTimeStamp < 0
                || grant.BindType == 2 && grant.ExpireTimeStamp == 0)
                throw new InvalidOperationException($"invalid award grant {grant.Entry}");
            if ((grant.Entry is BaseExpItem or JobExpItem) && (grant.BindType != 0 || grant.ExpireTimeStamp != 0))
                throw new InvalidOperationException("bound or expiring virtual EXP award");
            if (grant.Entry == BaseExpItem) { baseExp = checked(baseExp + grant.Count); continue; }
            if (grant.Entry == JobExpItem) { jobExp = checked(jobExp + grant.Count); continue; }
            if (IsCurrency(grant.Entry))
            {
                var currencyRow = data.Row("ItemConfig", grant.Entry)!.Value;
                if (grant.ExpireTimeStamp != 0 || grant.BindType == 2
                    || grant.BindType == 1 && (!currencyRow.TryGetProperty("_iTrade", out var trade) || GameData.Number(trade) != 0))
                    throw new InvalidOperationException("unsupported bound or expiring currency award");
                // (inferred) DBItemsInfo.currency has only an aggregate amount, so permanent bind does not partition the balance.
                staged.Currency[grant.Entry] = checked(staged.Currency.GetValueOrDefault(grant.Entry) + grant.Count);
            }
            else
            {
                var row = data.Row("ItemConfig", grant.Entry)!.Value;
                int stackLimit = GameData.Int(row, "_iType") == ItemTypeEquip ? 1 : (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
                int pack = TotemService.IsTotem(data, grant.Entry) ? 4 : EmblemService.IsEmblem(data, grant.Entry) ? 5 : ConfigPack(row);
                int left = grant.Count;
                var created = new List<InvItem>();
                foreach (var item in items.Where(i => CanStackGrant(i, grant.Entry, stackLimit, grant.BindType, grant.ExpireTimeStamp)))
                {
                    int add = Math.Min(left, stackLimit - item.Stack);
                    item.Stack += add;
                    left -= add;
                    received.Add(item);
                    if (left == 0) break;
                }
                while (left > 0)
                {
                    int stack = Math.Min(left, stackLimit);
                    var item = new InvItem
                    {
                        Entry = grant.Entry, Stack = stack, Pack = pack,
                        Sort = items.Where(i => i.Grid == 0 && i.Pack == pack).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1,
                        BindType = grant.BindType, BindExpire = grant.ExpireTimeStamp,
                        Attrs = TotemAttrFactory?.Invoke(grant.Entry) ?? EmblemAttrFactory?.Invoke(grant.Entry) ?? RollAttrs(grant.Entry)
                    };
                    items.Add(item);
                    created.Add(item);
                    received.Add(item);
                    left -= stack;
                }
                gained.Add((grant.Entry, grant.Count));
                if (IsEquipment(grant.Entry))
                {
                    // Each gear instance may roll distinct attributes; a single aggregate popup would misstate them.
                    foreach (var item in created) notification.Rewards.Add(ItemInfo(item));
                    continue;
                }
            }
            notification.Rewards.Add(ItemInfo(new InvItem
            {
                Entry = grant.Entry, Stack = grant.Count,
                BindType = grant.BindType, ExpireTimeStamp = grant.ExpireTimeStamp,
                Attrs = items.LastOrDefault(i => i.Entry == grant.Entry)?.Attrs
            }));
        }
        // Quest award auto returns are staged alongside the source grant, costs, tasks and daily state.
        // Other ApplyAward callers have no quest delivery boundary and retain their standalone behavior.
        var decomposed = deliveredTaskState == null && ownedAward?.CommandKind != "cd13e/grant" ? new ItemAutoDecomposeTipsNtf()
            : StageAutoDecomposeAward(session, items, staged, received, beforeGrants, gained);
        AddVirtualReward(notification, BaseExpItem, baseExp);
        AddVirtualReward(notification, JobExpItem, jobExp);
        int oldLevel = staged.Level, oldJob = staged.JobLevel;
        if (baseExp > 0 || jobExp > 0) ApplyExp(staged, baseExp, jobExp);
        // TaskService can hold unsaved in-memory objective progress. Compare that snapshot above, then
        // use the persisted blob as the SQLite CAS baseline for this single award transaction.
        byte[]? persistedTasks = expectedTasks == null ? null : roles.LoadTasks(session.Role.Id);
        var cardSight = StageFirstCardSight?.Invoke(session, gained.Select(g => g.Entry).ToList(), 0);
        void PublishAward()
        {
            session.Progress = staged;
            session.Items = items;
            if (nextTasks != null) session.Tasks = nextTasks;
            if (nextRuntime != null) session.TaskRuntime = nextRuntime;
            if (nextDailyState != null)
            {
                session.Daily = nextDailyState;
                session.DailyRows = DBDailyActivityInfo.Parser.ParseFrom(nextDailyState.Rows);
            }
            cardSight?.Apply();
            committed?.Invoke();
        }
        if (ownedAward != null)
        {
            if (familyEconomy != null) throw new InvalidOperationException("Award cannot carry two economy envelopes.");
            var economy = EconomyForOwnedAward(session, ownedAward, plan, old, staged, expense,
                baseExp, jobExp, decomposed);
            var builder = roles.NewTransaction();
            int itemReceipt = builder.ItemDelta(session.Role.Id, session.Items, items);
            builder.Add(new RoleAwardStatePlan(session.Role.Id, staged, deliveredTaskState, taskRuntimeState,
                expectedDaily, nextDaily, persistedTasks, expectedTasks != null));
            persist?.Invoke(builder);
            cardSight?.Persist(builder);
            builder.WithEconomy(economy);
            try
            {
                await roles.CommitAsync(builder, "character/owned-award", receipts =>
                {
                    ItemSnapshotWritePlan.ApplyGuids(items, (long[])receipts[itemReceipt]!);
                    staged.SourceRevision = builder.CommittedVector(session.Role.Id, RevisionStore.Progress);
                    PublishAward();
                });
            }
            catch (RolePlanConflictException) { return false; }
        }
        else if (familyEconomy != null)
        {
            // E05b pet-lottery seam. Ordinary award/EXP families retain their own migration boundary.
            if (familyEconomy.CommandKind != "cd13b/pet-lottery" || deliveredTaskState != null ||
                taskRuntimeState != null || expectedDaily != null || expectedTasks != null || baseExp != 0 || jobExp != 0)
                throw new InvalidOperationException("The pet lottery envelope cannot enlist unrelated award boundaries.");
            var builder = roles.NewTransaction();
            int itemReceipt = builder.ItemDelta(session.Role.Id, session.Items, items);
            builder.Progress(session.Role.Id, staged);
            persist?.Invoke(builder);
            cardSight?.Persist(builder);
            builder.WithEconomy(familyEconomy);
            await roles.CommitAsync(builder, "cd13b/pet-lottery", receipts =>
            {
                ItemSnapshotWritePlan.ApplyGuids(items, (long[])receipts[itemReceipt]!);
                PublishAward();
            });
        }
        else
        {
            bool escrowAward = false;
            try
            {
                if (!await roles.SaveAwardStateAsync(session.Role.Id, staged, deliveredTaskState, taskRuntimeState,
                    items, builder =>
                    {
                        persist?.Invoke(builder);
                        cardSight?.Persist(builder);
                        escrowAward = builder.HasEscrowEconomy;
                    }, expectedDaily, nextDaily, persistedTasks, expectedTasks != null)) return false;
            }
            catch (EconomyOutcomeUnknownException error) when (escrowAward)
            {
                QuarantineAward(error);
                throw;
            }
            try { PublishAward(); }
            catch (Exception error) when (escrowAward)
            {
                QuarantineAward(error);
                throw;
            }
            void QuarantineAward(Exception error)
            {
                if (session.Owner is { } owner) owner.State = Ownership.RoleOwnerState.RecoveryRequired;
                session.CancelPendingCharacterIntents();
                session.AbortForRecovery();
                log.LogError(error, "Committed E03 award publication is unresolved; role {Role} requires recovery",
                    session.Role!.Id);
            }
        }
        if (attachment?.Cancelled == true || !ReferenceEquals(session.Attachment, attachment)) return true;
        if (ownedAward?.CommandKind is "cd13e/grant" or "cd13e/gm-grant")
            foreach (var item in received.Where(i => i.Guid > 0 && items.Contains(i))) session.NewItems.Add(item.Guid);
        else
            foreach (var item in items.Where(i => i.Guid > 0 && !oldItemGuids.Contains(i.Guid))) session.NewItems.Add(item.Guid);
        if (expense.Count > 0 || plan.Grants.Any(g => g.Entry is not (BaseExpItem or JobExpItem))) await SyncItemsAsync(session);
        if (baseExp > 0 || jobExp > 0)
        {
            await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
            if (staged.Level != oldLevel || staged.JobLevel != oldJob)
            {
                await SendStatPlanAsync(session);
                await SendStatsAsync(session);
                foreach (var handler in ProgressHandlers) await handler(session);
            }
        }
        // A wheel (rewardIndexes) waits for its notice to stop and unlock, even when nothing showable was granted.
        if (!suppressRewardNotice && (notification.Rewards.Count > 0 || rewardIndexes is { Count: > 0 }))
        {
            notification.RewardType.Add(manifestType);
            if (rewardIndexes != null) notification.RewardIndexes.Add(rewardIndexes);
            await session.SendGameAsync(GetRewardNotify, notification);
        }
        if (decomposed.DecomposeItemInfo.Count > 0)
            await session.SendGameAsync(EquipmentService.ItemAutoDecomposeNotice, decomposed);
        foreach (var (entry, count) in gained) foreach (var handler in ItemGained) handler(session, entry, count);
        foreach (var handler in StateChanged) handler(session);
        return true;
    }

    private void ApplyExp(RoleProgress p, long baseExp, long jobExp)
    {
        p.Exp = checked(p.Exp + baseExp);
        for (long need; p.Level < MaxLevel && (need = BaseExpNeed(p.Level)) > 0 && p.Exp >= need;)
        {
            p.Exp -= need;
            p.Level++;
        }
        if (p.Level >= MaxLevel)
        {
            // Past the cap Base EXP feeds the Astral Level (CharacterService.Paragon) instead of being dropped.
            long overflow = p.Exp;
            p.Exp = 0;
            if (overflow > 0) AddParagonExp(p, overflow);
        }
        p.JobExp = checked(p.JobExp + jobExp);
        SkipFreeJobLevels(p);
        int maxJob = MaxJobLevel(p.Career);
        for (long need; p.JobLevel < maxJob && (need = JobExpNeed(p.Career, p.JobLevel)) > 0 && p.JobExp >= need;)
        {
            p.JobExp -= need;
            p.JobLevel++;
        }
        if (p.JobLevel >= maxJob) p.JobExp = Math.Min(p.JobExp, Math.Max(0, JobExpNeed(p.Career, p.JobLevel)));
    }

    private void AddVirtualReward(Romsg.GetRewardNotify notification, int entry, long amount)
    {
        while (amount > 0)
        {
            int chunk = (int)Math.Min(amount, int.MaxValue);
            notification.Rewards.Add(ItemInfo(new InvItem { Entry = entry, Stack = chunk }));
            amount -= chunk;
        }
    }

    private bool IsCurrency(int entry) => data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency;

    private static RoleProgress CloneProgress(RoleProgress source)
    {
        var result = new RoleProgress
        {
            SourceRevision = source.SourceRevision, Career = source.Career, Level = source.Level, JobLevel = source.JobLevel,
            Exp = source.Exp, JobExp = source.JobExp, BonusPoints = source.BonusPoints, StarterKit = source.StarterKit,
            ParagonLevel = source.ParagonLevel, ParagonExp = source.ParagonExp
        };
        Array.Copy(source.Stats, result.Stats, source.Stats.Length);
        foreach (var (key, value) in source.Currency) result.Currency[key] = value;
        foreach (var (key, value) in source.Skills) result.Skills[key] = value;
        foreach (var (key, value) in source.SkillBar) result.SkillBar[key] = value;
        return result;
    }

    private static void CopyProgress(RoleProgress target, RoleProgress source)
    {
        target.SourceRevision = source.SourceRevision;
        target.Career = source.Career; target.Level = source.Level; target.JobLevel = source.JobLevel;
        target.Exp = source.Exp; target.JobExp = source.JobExp; target.BonusPoints = source.BonusPoints;
        target.StarterKit = source.StarterKit;
        target.ParagonLevel = source.ParagonLevel; target.ParagonExp = source.ParagonExp;
        Array.Copy(source.Stats, target.Stats, source.Stats.Length);
        target.Currency.Clear(); foreach (var (key, value) in source.Currency) target.Currency[key] = value;
        target.Skills.Clear(); foreach (var (key, value) in source.Skills) target.Skills[key] = value;
        target.SkillBar.Clear(); foreach (var (key, value) in source.SkillBar) target.SkillBar[key] = value;
    }

    private static void CopyItem(InvItem target, InvItem source)
    {
        target.Entry = source.Entry; target.Stack = source.Stack; target.Pack = source.Pack;
        target.Sort = source.Sort; target.Grid = source.Grid; target.IsLocked = source.IsLocked;
        target.Attrs = source.Attrs; target.StringAttrs = source.StringAttrs;
        target.BindType = source.BindType; target.BindExpire = source.BindExpire;
        target.Enchantments = source.Enchantments;
    }

    /// <summary>Adds to the state and the database only; returns the instances that changed (new or restacked).</summary>
    private List<InvItem> AddItemCore(GameSession session, int entry, int count, out bool currency,
        bool persist = true, bool notifyItemGained = true, int bind = 0)
    {
        var changed = new List<InvItem>();
        var row = data.Row("ItemConfig", entry)!.Value;
        currency = GameData.Int(row, "_iType") == ItemTypeCurrency;
        var p = session.Progress!;
        if (currency)
        {
            p.Currency[entry] = Math.Max(0, p.Currency.GetValueOrDefault(entry) + count);
            return changed;
        }
        bool firstSightCandidate = persist && notifyItemGained && count > 0;
        int gainedCount = count;
        // Soul Echo uses PackType_Emblem=5 even though its exported ItemConfig._iBagTab is 1.
        int pack = TotemService.IsTotem(data, entry) ? 4 : EmblemService.IsEmblem(data, entry) ? 5 : ConfigPack(row);
        // (inferred) Even unappraised gear (some ItemConfig rows allow a stack of 2) gets one GUID per piece:
        // rolled stats, appraisal and enchantments belong to the instance and must never merge with another roll.
        int stackLimit = GameData.Int(row, "_iType") == ItemTypeEquip ? 1 : (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
        foreach (var item in session.Items.Where(i => CanStackGrant(i, entry, stackLimit, bind)))
        {
            if (count == 0) break;
            int add = Math.Min(count, stackLimit - item.Stack);
            item.Stack += add;
            count -= add;
            if (persist) itemStore.Update(item);
            changed.Add(item);
        }
        while (count > 0)
        {
            int stack = Math.Min(count, stackLimit);
            var item = new InvItem
            {
                Entry = entry, Stack = stack, Pack = pack, Sort = NextSort(session, pack),
                Attrs = TotemAttrFactory?.Invoke(entry) ?? EmblemAttrFactory?.Invoke(entry) ?? RollAttrs(entry), BindType = bind
            };
            if (persist) itemStore.Insert(session.Role!.Id, item);
            session.Items.Add(item);
            changed.Add(item);
            count -= stack;
        }
        // The durable item row must precede legacy observers. If a process dies before a card
        // observer saves 5057, CardService reconciles first sight from the committed bag on login.
        if (firstSightCandidate) foreach (var handler in ItemGained) handler(session, entry, gainedCount);
        return changed;
    }

    /// <summary>(inferred) Plain rewards stack only onto plain, unlocked instances with the same permanent binding.</summary>
    private static bool CanStackGrant(InvItem item, int entry, int limit, int bind, long bindExpire = 0) =>
        item.Grid == 0 && !item.IsLocked && item.Entry == entry && item.Stack < limit
        && (item.Attrs?.Count ?? 0) == 0 && (item.StringAttrs?.Count ?? 0) == 0 && item.Enchantments.Count == 0
        && item.BindType == bind && item.BindExpire == bindExpire;
}
