using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>Persistent social request identity; paired with 5013 fragment counters (inferred).</summary>
public sealed class AdventureFragmentAsk
{
    public long Owner { get; set; }
    public int Item { get; set; }
    public long Created { get; set; }
    public bool Finished { get; set; }
}

public sealed partial class ActivityRuntimeState
{
    public Dictionary<long, AdventureFragmentAsk> AdventureFragmentAsks { get; set; } = new();
}

public sealed partial class NewActivityService
{
    private const int PreCrystal = 1350001;
    private const int PreCrystalScore = 50_000;
    private readonly SemaphoreSlim _adventureSocialGate = new(1, 1);

    /// <summary>Shared Pre progress is durable across roles and server restart, keyed by cycle and realm area.</summary>
    private void InitializeAdventureServices(AccountStore accountStore, ItemStore itemStore)
    {
    }

    private (long Cycle, int Area, long Day) PreKey(GameSession session, long id) =>
        (session.NewActivities!.ActivityInfo[id].CycleStartTime, session.Role!.Area,
            DateOnly.FromDateTime(_clock.GetLocalNow().Date).DayNumber);

    private (long Score, long Today, long Submitters) PreTotals(GameSession session, long id)
    {
        var (cycle, area, day) = PreKey(session, id);
        var totals = _accounts.ReadSync<AdventurePreTotals>(
            new AdventurePreTotalsReadPlan(id, cycle, area, day, session.Role!.Id));
        return (totals.Score, totals.Today, totals.Submitters);
    }

    /// <summary>82960, LC_DAL_Season.OnDonateItem. One crystal contributes 50,000 points.</summary>
    public async Task PreSubmitAsync(GameSession session, NewActivityPreSubmitItemReq req)
    {
        var res = new NewActivityPreSubmitItemResp();
        await session.ActivityGate.WaitAsync();
        try
        {
            res.Result = ActivityError(session, req.ActivityID, 2);
            if (res.Result == 0)
            {
                if (req.Num is < 1 or > 1000000) res.Result = ErrorCodes.Parameter;
                else
                {
                    var next = session.NewActivities!.Clone();
                    var state = next.ActivityTypeState[2].ActivityState[req.ActivityID].SelfData.SeasonPre;
                    state.Score = checked(state.Score + req.Num * PreCrystalScore);
                    var (cycle, area, day) = PreKey(session, req.ActivityID);
                    res.Result = await CommitAdventureAsync(session, next, costs: [(PreCrystal, checked((int)req.Num))], persist: tx =>
                    {
                        tx.Add(new Ro3Emu.Server.Accounts.Persistence.ActivityPreDonationPlan(req.ActivityID,
                            cycle, area, day, session.Role!.Id, req.Num * PreCrystalScore, req.Num));
                    });
                    if (res.Result == 0) res.Num = req.Num;
                }
            }
        }
        finally { session.ActivityGate.Release(); }
        await session.SendGameAsync(82960, res);
    }

    /// <summary>82961, global progress and today's contribution for the current cycle.</summary>
    public async Task PreGetDataAsync(GameSession session, NewActivityPreGetDataReq req)
    {
        var res = new NewActivityPreGetDataResp();
        await session.ActivityGate.WaitAsync();
        try
        {
            res.Result = ActivityError(session, req.ActivityID, 2);
            if (res.Result == 0)
            {
                var (score, today, submitters) = PreTotals(session, req.ActivityID);
                res.AreaProgress = score;
                res.SelfTodayProgress = today;
                res.TodaySubmitRoleNum = submitters;
                res.IsTodaySubmit = today > 0;
            }
        }
        finally { session.ActivityGate.Release(); }
        await session.SendGameAsync(82961, res);
    }

    /// <summary>82962 milestone claims from SeasonPreProgressRewardConfig; every id is once per cycle.</summary>
    public async Task PreReceiveAsync(GameSession session, NewActivityPreReceiveRewardReq req)
    {
        var res = new NewActivityPreReceiveRewardResp();
        await session.ActivityGate.WaitAsync();
        try
        {
            res.Result = ActivityError(session, req.ActivityID, 2);
            if (res.Result == 0)
            {
                var score = PreTotals(session, req.ActivityID).Score;
                var requested = req.RewardIDs.Distinct().ToArray();
                var rows = requested.Select(id => _data.Row("SeasonPreProgressRewardConfig", id)).ToArray();
                var current = session.NewActivities!.ActivityTypeState[2].ActivityState[req.ActivityID].SelfData.SeasonPre;
                if (requested.Length == 0 || requested.Length != req.RewardIDs.Count
                    || rows.Any(r => r == null || GameData.Int(r.Value, "_iActivityid") != req.ActivityID
                        || score < GameData.Int(r.Value, "_iEndType"))
                    || requested.Any(id => current.ReceivedRewardIDs.ContainsKey(id))) res.Result = ErrorCodes.TaskRequirements;
                else
                {
                    var next = session.NewActivities.Clone();
                    var state = next.ActivityTypeState[2].ActivityState[req.ActivityID].SelfData.SeasonPre;
                    foreach (var id in requested) state.ReceivedRewardIDs[id] = Now;
                    res.Result = await CommitAdventureAsync(session, next, rows.Select(r => GameData.Int(r!.Value, "_iReward")));
                    if (res.Result == 0) res.ReceiveSuccessIDs.AddRange(requested);
                }
            }
        }
        finally { session.ActivityGate.Release(); }
        await session.SendGameAsync(82962, res);
    }

    /// <summary>82963 and the matching 82976 oneof.</summary>
    public Task FragmentAskAsync(GameSession session, NewActivityFragmentGatherAskReq req) =>
        FragmentDirectAsync(session, 82963, req, new NewActivityFragmentGatherAskResp(),
            async r => await FragmentAskCoreAsync(session, 105, req, r));

    private async Task<int> FragmentAskCoreAsync(GameSession session, long activityId, NewActivityFragmentGatherAskReq req, NewActivityFragmentGatherAskResp res)
    {
        if (ActivityError(session, activityId, 5) is var error and not 0) return error;
        if (!FragmentItems.Any(i => i == req.ItemConfigID)) return ErrorCodes.Parameter;
        var next = session.NewActivities!.Clone();
        var gather = next.ActivityInfo[activityId].ActivityParam.FragmentGather;
        long dayEnd = new DateTimeOffset(_clock.GetLocalNow().Date.AddDays(1), _clock.LocalTimeZone.GetUtcOffset(_clock.GetLocalNow().Date.AddDays(1))).ToUnixTimeSeconds();
        if (gather.DailyData.ExpireTs <= Now) gather.DailyData = new FragmentGatherDaily { ExpireTs = dayEnd };
        if (gather.DailyData.AskTimes >= 20) return ErrorCodes.Cooldown;
        var runtime = CloneRuntime(session);
        long ask = Random.Shared.NextInt64(1, long.MaxValue);
        while (runtime.AdventureFragmentAsks.ContainsKey(ask)) ask = Random.Shared.NextInt64(1, long.MaxValue);
        runtime.AdventureFragmentAsks[ask] = new AdventureFragmentAsk { Owner = session.Role!.Id, Item = checked((int)req.ItemConfigID), Created = Now };
        gather.ReqID++;
        gather.DailyData.AskTimes++;
        int result = await CommitAdventureAsync(session, next, runtime: runtime);
        if (result == 0) { res.AskID = ask; res.ItemConfigID = req.ItemConfigID; }
        return result;
    }

    public async Task FragmentAskFinishedAsync(GameSession session, NewActivityFragmentGatherAskFinishedReq req)
    {
        if (!session.ActivitiesReady || session.Owner == null || session.Attachment == null)
        {
            await FragmentDirectAsync(session, 82967, req, new NewActivityFragmentGatherAskFinishedResp(),
                r => FragmentAskFinishedCoreAsync(session, 105, req, r));
            return;
        }
        var scope = await EnterSocialWithGateAsync(session, req.UserID, "activity/fragment-status", req.AskID);
        int error;
        var response = new NewActivityFragmentGatherAskFinishedResp();
        try { error = await FragmentAskFinishedCoreAsync(session, 105, req, response); }
        finally { session.ActivityGate.Release(); scope.Dispose(); }
        if (error != 0) await session.SendGameAsync(2, new CommonErrorNtf { ErrorCode = error });
        await session.SendGameAsync(82967, response);
    }

    private async Task<int> FragmentAskFinishedCoreAsync(GameSession session, long activityId,
        NewActivityFragmentGatherAskFinishedReq req, NewActivityFragmentGatherAskFinishedResp res)
    {
        if (ActivityError(session, activityId, 5) is var error and not 0) return error;
        if (req.UserID <= 0 || req.AskID <= 0) return ErrorCodes.Parameter;
        var targetOwner = await PinSocialRecipientAsync(req.UserID);
        if (targetOwner == null || await _roles.GetAsync(req.UserID) is not { } target
            || target.Area != session.Role!.Area) return ErrorCodes.Parameter;
        var online = AttachedSocialSession(targetOwner);
        var blob = online == null
            ? (await _accounts.ReadAsync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(req.UserID, "activity_runtime"))).Value
            : null;
        var state = online?.ActivityRuntime ?? (blob == null ? new ActivityRuntimeState()
            : JsonSerializer.Deserialize<ActivityRuntimeState>(blob) ?? new());
        if (!state.AdventureFragmentAsks.TryGetValue(req.AskID, out var ask) || ask.Owner != req.UserID) return ErrorCodes.Parameter;
        res.IsFinished = ask.Finished;
        return 0;
    }

    public Task FragmentCraftAsync(GameSession session, NewActivityFragmentGatherCraftReq req) =>
        FragmentDirectAsync(session, 82965, req, new NewActivityFragmentGatherCraftResp(),
            r => FragmentCraftCoreAsync(session, 105, r));

    private async Task<int> FragmentCraftCoreAsync(GameSession session, long activityId, NewActivityFragmentGatherCraftResp res)
    {
        if (ActivityError(session, activityId, 5) is var error and not 0) return error;
        var next = session.NewActivities!.Clone();
        var gather = next.ActivityInfo[activityId].ActivityParam.FragmentGather;
        gather.ProgressFinish++;
        gather.Seed = Random.Shared.Next();
        int result = await CommitAdventureAsync(session, next, costs: FragmentItems.Select(i => (i, 1)).ToArray());
        if (result == 0) res.Level = 1; // (inferred) crystal-set synthesis has no level weighting in extracted config.
        return result;
    }

    public Task FragmentProgressAsync(GameSession session, NewActivityFragmentGatherProgressReq req) =>
        FragmentDirectAsync(session, 82966, req, new NewActivityFragmentGatherProgressResp(),
            _ => FragmentProgressCoreAsync(session, 105, req));

    private async Task<int> FragmentProgressCoreAsync(GameSession session, long activityId, NewActivityFragmentGatherProgressReq req)
    {
        if (ActivityError(session, activityId, 5) is var error and not 0) return error;
        var row = _data.Row("SeasonPrepProgressReward", req.GatherTimes);
        if (row == null || GameData.Int(row.Value, "_iActivityid") != activityId) return ErrorCodes.Parameter;
        var next = session.NewActivities!.Clone();
        var gather = next.ActivityInfo[activityId].ActivityParam.FragmentGather;
        long threshold = GameData.Int(row.Value, "_iEndType");
        if (gather.ProgressFinish < threshold || gather.ProgressAward >= threshold) return ErrorCodes.TaskRequirements;
        gather.ProgressAward = checked((int)threshold);
        return await CommitAdventureAsync(session, next, [GameData.Int(row.Value, "_iReward")]);
    }

    public async Task FragmentSendAsync(GameSession session, NewActivityFragmentGatherSendReq req)
    {
        if (!session.ActivitiesReady || session.Owner == null || session.Attachment == null)
        {
            await FragmentDirectAsync(session, 82964, req, new NewActivityFragmentGatherSendResp(),
                _ => FragmentSendCoreAsync(session, 105, req));
            return;
        }
        var scope = await EnterSocialWithGateAsync(session, req.ToUserID, "activity/fragment-send", req.AskID);
        var notices = new OwnerNotices(scope.Context);
        int error;
        try { error = await FragmentSendCoreAsync(session, 105, req); }
        finally
        {
            notices.Dispose();
            session.ActivityGate.Release();
            scope.Dispose();
            try { await notices.FlushAsync(); }
            catch (Exception e) { _log.LogWarning(e, "Committed fragment notice failed for {Role}", session.Role?.Id); }
        }
        if (error != 0) await session.SendGameAsync(2, new CommonErrorNtf { ErrorCode = error });
        await session.SendGameAsync(82964, new NewActivityFragmentGatherSendResp());
    }

    /// <summary>
    /// 82964: one configured crystal moves from donor to the ask owner in the same SQLite transaction as
    /// the recipient's 5013 and request state. Source ActivityGate is held. A second player's ActivityGate
    /// is acquired without waiting, so reciprocal sends never deadlock. Offline ask owners use the same
    /// lifecycle/revision identity as attached roles. Cross-role gifting eligibility beyond item ownership is (inferred).
    /// </summary>
    private async Task<int> FragmentSendCoreAsync(GameSession sender, long activityId, NewActivityFragmentGatherSendReq req)
    {
        if (ActivityError(sender, activityId, 5) is var error and not 0) return error;
        if (req.ToUserID <= 0 || req.ToUserID == sender.Role!.Id || req.AskID <= 0
            || !FragmentItems.Any(i => i == req.ItemID) || _character.CountOf(sender, checked((int)req.ItemID)) < 1)
            return ErrorCodes.Parameter;
        var receiverOwner = await PinSocialRecipientAsync(req.ToUserID);
        if (receiverOwner == null) return ErrorCodes.Parameter;
        var receiverRole = await _roles.GetAsync(req.ToUserID);
        if (receiverRole == null || receiverRole.Area != sender.Role.Area) return ErrorCodes.Parameter;
        var receiver = AttachedSocialSession(receiverOwner);
        if (receiver != null && !receiver.ActivityGate.Wait(0)) return ErrorCodes.Cooldown;
        try
        {
            var ordered = receiver == null ? [sender] : new[] { sender, receiver }.OrderBy(s => s.Role!.Id).ToArray();
            using var first = await CharacterService.Lock(ordered[0]);
            using var second = ordered.Length == 2 ? await CharacterService.Lock(ordered[1]) : null;
            var senderVector = await _roles.ReadRevisionVectorAsync(sender.Role.Id);
            var receiverVector = await _roles.ReadRevisionVectorAsync(req.ToUserID);
            var transaction = _accounts.NewTransaction();
            transaction.ExpectRole(senderVector);
            transaction.ExpectRole(receiverVector);
            var targetNewSnapshot = await _accounts.ReadAsync<RoleBlobSnapshot>(
                new RoleBlobSnapshotRead(req.ToUserID, "new_activities"));
            var targetRuntimeSnapshot = await _accounts.ReadAsync<RoleBlobSnapshot>(
                new RoleBlobSnapshotRead(req.ToUserID, "activity_runtime"));
            transaction.Guard(new RoleBlobGuardPlan(req.ToUserID, "new_activities", targetNewSnapshot));
            transaction.Guard(new RoleBlobGuardPlan(req.ToUserID, "activity_runtime", targetRuntimeSnapshot));
            byte[]? targetNewBytes = targetNewSnapshot.Value;
            byte[]? targetRuntimeBytes = targetRuntimeSnapshot.Value;
            var targetNew = receiver?.NewActivities?.Clone()
                ?? (targetNewBytes is { } blob ? DBNewActivityInfo.Parser.ParseFrom(blob) : null);
            var targetRuntime = receiver != null ? CloneRuntime(receiver)
                : targetRuntimeBytes == null ? new ActivityRuntimeState()
                : JsonSerializer.Deserialize<ActivityRuntimeState>(targetRuntimeBytes) ?? new();
            if (targetNew == null || !targetNew.ActivityInfo.TryGetValue(activityId, out var targetInfo)
                || targetInfo.ActivityParam?.FragmentGather == null
                || !targetRuntime.AdventureFragmentAsks.TryGetValue(req.AskID, out var ask)
                || ask.Owner != req.ToUserID || ask.Item != req.ItemID || ask.Finished
                || ask.Created < sender.NewActivities!.ActivityInfo[activityId].CycleStartTime)
                return ErrorCodes.TaskRequirements;

            var senderOriginal = sender.Items.Select(i => i.Clone()).ToList();
            var sourceItems = senderOriginal.Select(i => i.Clone()).ToList();
            var source = sourceItems.FirstOrDefault(i => i.Grid == 0 && i.Entry == req.ItemID && i.Stack > 0
                && !i.IsLocked && !EmblemService.IsReserved(sender, i));
            if (source == null) return ErrorCodes.ItemsShort;
            var targetOriginal = receiver?.Items.Select(i => i.Clone()).ToList()
                ?? await _accounts.ReadAsync<List<InvItem>>(new ActivityInventoryReadPlan(req.ToUserID));
            var targetItems = targetOriginal.Select(i => i.Clone()).ToList();
            source.Stack--;
            if (source.Stack == 0) sourceItems.Remove(source);
            var existing = targetItems.FirstOrDefault(i => i.Grid == 0 && i.Entry == source.Entry
                && i.BindType == source.BindType && i.BindExpire == source.BindExpire
                && i.Attrs == null && i.StringAttrs == null && !i.IsLocked);
            if (existing != null) existing.Stack = checked(existing.Stack + 1);
            else
            {
                var gift = source.Clone();
                gift.Guid = 0; gift.Stack = 1; gift.Grid = 0; gift.Sort = targetItems.Where(i => i.Pack == gift.Pack).Select(i => i.Sort).DefaultIfEmpty().Max() + 1;
                targetItems.Add(gift);
            }
            ask.Finished = true;
            var gather = targetInfo.ActivityParam.FragmentGather;
            if (gather.DailyData == null) gather.DailyData = new FragmentGatherDaily();
            long dayEnd = new DateTimeOffset(_clock.GetLocalNow().Date.AddDays(1), _clock.LocalTimeZone.GetUtcOffset(_clock.GetLocalNow().Date.AddDays(1))).ToUnixTimeSeconds();
            if (gather.DailyData.ExpireTs <= Now) gather.DailyData = new FragmentGatherDaily { ExpireTs = dayEnd };
            gather.DailyData.ReceiveTimes++;
            transaction.Guard(new ActivityInventoryGuardPlan(sender.Role!.Id, senderOriginal));
            transaction.Guard(new ActivityInventoryGuardPlan(req.ToUserID, targetOriginal));
            transaction.Add(new ItemSnapshotWritePlan(sender.Role.Id, sourceItems), receipt =>
                ItemSnapshotWritePlan.ApplyGuids(sourceItems, (long[])receipt!));
            transaction.Add(new ItemSnapshotWritePlan(req.ToUserID, targetItems), receipt =>
                ItemSnapshotWritePlan.ApplyGuids(targetItems, (long[])receipt!));
            transaction.RoleBlob(req.ToUserID, "new_activities", targetNew.ToByteArray());
            transaction.RoleBlob(req.ToUserID, "activity_runtime", JsonSerializer.SerializeToUtf8Bytes(targetRuntime));
            transaction.WithEscrowEconomy(new EscrowEconomyIntent("activity/fragment-transfer",
                sender.Account ?? "", sender.Role.Id,
                sourceKey: $"activity/fragment-ask/{req.ToUserID}/{req.AskID}",
                transferReason: EconomyReason.InventoryRelocation).Evidence(new
                {
                    AskId = req.AskID, Donor = sender.Role.Id, Recipient = req.ToUserID,
                    Item = req.ItemID, SourceGuid = source.Guid, ConsumedAsk = true
                }));
            await _roles.CommitAsync(transaction, "activity/fragment-transfer", _ =>
            {
                sender.Items = sourceItems;
                if (receiver != null)
                {
                    receiver.Items = targetItems;
                    receiver.NewActivities = targetNew;
                    receiver.ActivityRuntime = targetRuntime;
                }
            });
            // Run authoritative observers while both owners are still pinned. OwnerNotices freezes
            // their packets; the wrapper flushes transport only after releasing the reservation.
            await _character.ItemsChangedAsync(sender, false);
            if (receiver != null)
            {
                await _character.ItemsChangedAsync(receiver, false);
                await SyncAsync(receiver);
            }
            return 0;
        }
        finally { if (receiver != null) receiver.ActivityGate.Release(); }
    }

    private async Task FragmentDirectAsync<TReq, TRes>(GameSession session, uint id, TReq req, TRes res, Func<TRes, Task<int>> handler)
        where TRes : Google.Protobuf.IMessage<TRes>
    {
        await session.ActivityGate.WaitAsync();
        int error;
        try { error = await handler(res); }
        finally { session.ActivityGate.Release(); }
        // Legacy 82963–67 responses have no result field; 2 is the client error channel.
        if (error != 0) await session.SendGameAsync(2, new CommonErrorNtf { ErrorCode = error });
        await session.SendGameAsync(id, res);
    }

    /// <summary>83450 legacy direct form; current Lua submits through 82976.</summary>
    public async Task WorldFillAsync(GameSession session, WorldExplorationFillPhaseNodeReq req)
    {
        var res = new WorldExplorationFillPhaseNodeRes();
        await session.ActivityGate.WaitAsync();
        try { res.Err = await WorldFillCoreAsync(session, 111, req); }
        finally { session.ActivityGate.Release(); }
        await session.SendGameAsync(83450, res);
    }

    /// <summary>82975 is used by SevenDays; other adventure rewards have subtype-specific requests.</summary>
    private Task<int> ClaimAdventureAsync(GameSession session, NewActivityGetRewardCommonReq req) =>
        Task.FromResult(ActivityError(session, req.ActivityID) is var error and not 0 ? error : ErrorCodes.Parameter);
}
