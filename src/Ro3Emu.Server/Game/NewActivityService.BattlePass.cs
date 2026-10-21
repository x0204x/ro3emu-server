using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>82976 BattlePass activity 200/201/9201 (clientB LC_BattlePassHelper.lua).</summary>
public sealed partial class NewActivityService
{
    private static readonly long[] PassIds = [200, 201, 9201];

    /// <summary>
    /// The earlier pass with the same EXP currency (Pass I for Pass II), or null. The client data gives Pass I and II
    /// the same condition and currency 132; on the original servers they were consecutive seasons.
    /// </summary>
    private long? PassBefore(long activityId)
    {
        if (!PassIds.Contains(activityId) || PassConfig(activityId) is not { } config) return null;
        long currency = GameData.Int(config, "_iExpCurrencyId");
        return PassIds.Where(id => id < activityId).Cast<long?>()
            .LastOrDefault(id => PassConfig(id!.Value) is { } earlier && GameData.Int(earlier, "_iExpCurrencyId") == currency);
    }

    /// <summary>
    /// With Pass:Play a pass opens only once the pass before it is at its last level. Both fill
    /// from the same currency, and the server's open windows never end, so together they would pay every level twice.
    /// </summary>
    private bool PassTurnReached(GameSession session, long activityId, DBNewActivityInfo? state)
    {
        if (!_passPlay || PassBefore(activityId) is not long before || PassConfig(before) is not { } config) return true;
        var infos = (state ?? session.NewActivities)?.ActivityInfo;
        return infos != null && infos.TryGetValue(before, out var info)
            && info.ActivityParam?.BattlePass is { } pass && pass.Level >= GameData.Int(config, "_iMaxLevel");
    }

    private void InitializePass(GameSession session, JsonElement row, PlayerActivityInfo info)
    {
        if (GameData.Int(row, "_iActivityType") != 23) return;
        info.ActivityParam ??= new PlayerActivityParam();
        info.ActivityParam.BattlePass ??= new PlayerActivityBattlePass();
    }

    private async Task<bool> HandlePassCommonAsync(GameSession session, CommonActivityReq req, CommonActivityRes res)
    {
        if (req.BattlePassClaimReward != null)
        {
            res.BattlePassClaimReward = new BattlePassClaimRewardRes();
            res.Result = await ClaimPassAsync(session, req.ActivityId, req.BattlePassClaimReward, res.BattlePassClaimReward);
            res.BattlePassClaimReward.Result = res.Result;
            return true;
        }
        if (req.BattlePassMarkUnlockViewed != null)
        {
            res.BattlePassMarkUnlockViewed = new BattlePassMarkUnlockViewedRes();
            res.Result = await MarkPassViewedAsync(session, req.ActivityId);
            res.BattlePassMarkUnlockViewed.Result = res.Result;
            return true;
        }
        if (req.BattlePassBuyLevel != null)
        {
            res.BattlePassBuyLevel = new BattlePassBuyLevelRes();
            res.Result = await BuyPassLevelAsync(session, req.ActivityId, req.BattlePassBuyLevel, res.BattlePassBuyLevel);
            res.BattlePassBuyLevel.Result = res.Result;
            return true;
        }
        if (req.BattlePassGift != null)
        {
            res.BattlePassGift = new BattlePassGiftRes();
            res.Result = await GiftPassAsync(session, req.ActivityId, req.BattlePassGift);
            res.BattlePassGift.Result = res.Result;
            return true;
        }
        return false;
    }

    private JsonElement? PassConfig(long activityId) => PassIds.Contains(activityId) ? _data.Row("BattlePassConfig", activityId) : null;

    private JsonElement? PassReward(long activityId, int level) => _data.Table("BattlePassRewardConfig").Values
        .FirstOrDefault(r => GameData.Int(r, "_iActivityId") == activityId && GameData.Int(r, "_iLevel") == level) is
    { ValueKind: JsonValueKind.Object } row ? row : null;

    private async Task<int> ClaimPassAsync(GameSession session, long activityId, BattlePassClaimRewardReq req, BattlePassClaimRewardRes res)
    {
        if (ActivityError(session, activityId, 23) is var error and not 0) return error;
        if (PassConfig(activityId) == null) return ErrorCodes.BattlePassClosed;
        if (req.RewardType is not (0 or 1)) return ErrorCodes.BattlePassRewardType;
        if (req.Levels.Count == 0) return ErrorCodes.BattlePassNoRewards;
        if (req.Levels.Count != req.Levels.Distinct().Count()) return ErrorCodes.BattlePassLevel;
        var current = session.NewActivities!.ActivityInfo[activityId].ActivityParam.BattlePass;
        if (req.RewardType == 1 && !current.Premium) return ErrorCodes.BattlePassPremium;
        var claimed = req.RewardType == 1 ? current.ClaimedPremiumLevels : current.ClaimedFreeLevels;
        var groups = new List<long>();
        foreach (int level in req.Levels)
        {
            if (level < 1 || PassReward(activityId, level) is not { } row) return ErrorCodes.BattlePassLevel;
            if (level > current.Level) return ErrorCodes.BattlePassLevelRequired;
            if (claimed.Contains(level)) return ErrorCodes.BattlePassClaimed;
            long award = GameData.Int(row, req.RewardType == 1 ? "_iPremiumReward" : "_iFreeReward");
            if (award <= 0) return ErrorCodes.RewardMissing;
            groups.Add(award);
        }
        var next = session.NewActivities.Clone();
        var updated = next.ActivityInfo[activityId].ActivityParam.BattlePass;
        foreach (int level in req.Levels)
        {
            if (req.RewardType == 1) updated.ClaimedPremiumLevels.Add(level);
            else updated.ClaimedFreeLevels.Add(level);
        }
        // 9201 rows name AwardConfig directly; 200/201 rows route through MultiverseGroupConfig.
        // Keep the two config namespaces explicit so no unrelated reward source gains a fallback.
        AwardGrant[]? direct = null;
        if (activityId == 9201)
        {
            try { direct = groups.SelectMany(id => _awards.ResolveDirect(session, id).Grants).ToArray(); }
            catch (AwardResolutionException) { return ErrorCodes.RewardMissing; }
        }
        int result = await CommitAsync(session, next, activityId == 9201 ? null : groups, extra: direct,
            source: $"pass/claim/{activityId}/{next.ActivityInfo[activityId].CycleStartTime}/{req.RewardType}/{string.Join(',', req.Levels.Order())}");
        if (result == 0) res.ClaimedLevels.Add(req.Levels);
        return result;
    }

    private async Task<int> MarkPassViewedAsync(GameSession session, long activityId)
    {
        if (ActivityError(session, activityId, 23) is var error and not 0) return error;
        if (session.NewActivities!.ActivityInfo[activityId].ActivityParam.BattlePass.UnlockPageViewed) return 0;
        var next = session.NewActivities.Clone();
        next.ActivityInfo[activityId].ActivityParam.BattlePass.UnlockPageViewed = true;
        return await CommitAsync(session, next);
    }

    private async Task<int> BuyPassLevelAsync(GameSession session, long activityId, BattlePassBuyLevelReq req, BattlePassBuyLevelRes res)
    {
        if (ActivityError(session, activityId, 23) is var error and not 0) return error;
        if (PassConfig(activityId) is not { } config || req.Count <= 0) return ErrorCodes.BattlePassLevel;
        var pass = session.NewActivities!.ActivityInfo[activityId].ActivityParam.BattlePass;
        int max = checked((int)GameData.Int(config, "_iMaxLevel"));
        if (pass.Level >= max || req.Count > max - pass.Level) return ErrorCodes.BattlePassMaxLevel;
        var cost = GameData.Pairs(config, "_kBuyLevelConsume").ToArray();
        if (cost.Length != 1 || cost[0].Id <= 0 || cost[0].Value <= 0 || cost[0].Value != Math.Floor(cost[0].Value))
            return ErrorCodes.FeatureUnavailable;
        long amount = checked((long)cost[0].Value * req.Count);
        if (amount > int.MaxValue) return ErrorCodes.BattlePassCurrency;
        var next = session.NewActivities.Clone();
        var updated = next.ActivityInfo[activityId].ActivityParam.BattlePass;
        updated.Level += req.Count;
        updated.BoughtLevelCount += req.Count;
        int result = await CommitAsync(session, next, costs: [((int)cost[0].Id, (int)amount)],
            source: $"pass/buy/{activityId}/{next.ActivityInfo[activityId].CycleStartTime}/{Guid.NewGuid():N}");
        if (result == 0) res.ActualCount = req.Count;
        return result;
    }

    private async Task<int> GiftPassAsync(GameSession session, long activityId, BattlePassGiftReq req)
    {
        if (ActivityError(session, activityId, 23) is var error and not 0) return error;
        if (req.TargetPlayerId == session.Role!.Id) return ErrorCodes.BattlePassGiftSelf;
        if (req.TargetPlayerId <= 0 || await PinSocialRecipientAsync(req.TargetPlayerId) == null)
            return ErrorCodes.PlayerOffline;
        if (PassConfig(activityId) is not { } config) return ErrorCodes.BattlePassClosed;
        int token = checked((int)GameData.Int(config, "_iGiftItemId"));
        if (token == 0 || Owned(session, token) < 1) return ErrorCodes.BattlePassGiftItem;
        if (req.GiftMessage.Length > 200) return ErrorCodes.BattlePassGiftMessage;
        int entitlement = checked((int)GameData.Int(config, "_iPremiumItemId"));
        if (entitlement <= 0 || _data.Row("ItemConfig", entitlement) == null) return ErrorCodes.BattlePassClosed;
        MailService.ActivityDeliveryLease? delivered = null;
        int result;
        await session.StateLock.WaitAsync();
        try
        {
            if (Owned(session, token) < 1) return ErrorCodes.BattlePassGiftItem;
            // (inferred) Gift product 8 grants item 1020001; the client opens its friend-gift UI on item use.
            // Deliver the configured premium entitlement through ordinary claimable mail, with token debit in
            // the same SQLite transaction. Recipient mail gate prevents a concurrent mailbox overwrite.
            using var delivery = await _mail.PrepareActivityDeliveryAsync(req.TargetPlayerId, session.Role.Id,
                session.Role.Name, activityId, req.GiftMessage, entitlement);
            if (delivery.Pending) return ErrorCodes.BattlePassGiftPending;
            var economy = new EscrowEconomyIntent("activity/pass-gift", session.Account ?? "", session.Role.Id)
                .Source(AdventureAsset(entitlement), 1, EconomyReason.ActivityReward,
                    EconomyCompartment.Mail, new EconomyOwner(EconomyOwnerKind.Role,
                        req.TargetPlayerId.ToString(CultureInfo.InvariantCulture)))
                .Evidence(new
                {
                    Recipient = req.TargetPlayerId, Activity = activityId, Token = token,
                    Entitlement = entitlement, Message = req.GiftMessage
                });
            result = await CommitAsync(session, session.NewActivities!.Clone(), costs: [(token, 1)],
                persist: delivery.Persist, stateLockHeld: true, economy: economy);
            if (result == 0) delivered = delivery;
        }
        finally { session.StateLock.Release(); }
        if (delivered != null)
        {
            var recipient = AttachedSocialSession(_loop.Owners!.Get(req.TargetPlayerId));
            var notices = _loop.Owners.Current?.Notices;
            if (recipient != null && notices != null)
                notices.Add(recipient, async () =>
                {
                    try { await delivered.NotifyAsync(); }
                    catch (Exception e) { _log.LogWarning(e, "Pass gift committed but mail notice failed for {Recipient}", req.TargetPlayerId); }
                });
        }
        return result;
    }

    /// <summary>Observe actual earned pass currency, keeping already credited EXP when a balance is spent elsewhere (inferred).</summary>
    private async Task ReconcilePassAsync(GameSession session)
    {
        if (session.NewActivities == null || session.ActivityRuntime == null) return;
        foreach (long activityId in PassIds)
        {
            if (ActivityError(session, activityId, 23, allowInitializing: true) != 0 || PassConfig(activityId) is not { } config) continue;
            var info = session.NewActivities.ActivityInfo[activityId];
            var current = info.ActivityParam.BattlePass;
            var runtime = session.ActivityRuntime.PassProgress.GetValueOrDefault(activityId) ?? new PassProgressRuntime();
            bool reset = runtime.Initialized && runtime.ActivityInstance != info.ActivityInstance;
            int expItem = checked((int)GameData.Int(config, "_iExpCurrencyId"));
            long balance = Owned(session, expItem);
            // A following pass starts from the balance it opens with: that EXP already went into the pass before it.
            long earned = reset ? 0 : runtime.Initialized ? Math.Max(0, balance - runtime.LastBalance)
                : _passPlay && PassBefore(activityId) != null ? 0 : balance;
            bool changed = reset || !runtime.Initialized || runtime.LastBalance != balance;
            int max = checked((int)GameData.Int(config, "_iMaxLevel"));
            int level = reset ? 0 : current.Level, exp = reset ? 0 : current.Exp;
            if (earned > 0 && level < max)
            {
                long sum = (long)exp + earned;
                while (level < max && PassReward(activityId, level + 1) is { } reward)
                {
                    long required = GameData.Int(reward, "_iExpRequired");
                    if (required <= 0 || sum < required) break;
                    sum -= required;
                    level++;
                }
                exp = level == max ? 0 : checked((int)Math.Min(sum, int.MaxValue));
                changed |= level != current.Level || exp != current.Exp;
            }
            int premiumItem = checked((int)GameData.Int(config, "_iPremiumItemId"));
            // Ro3Emu:Pass:FreePremium: the premium track opens without spending the item.
            bool owned = Owned(session, premiumItem) > 0;
            bool activate = (reset || !current.Premium) && (_freePremiumPass || owned);
            changed |= activate;
            if (!changed) continue;
            var next = session.NewActivities.Clone();
            if (reset) next.ActivityInfo[activityId].ActivityParam.BattlePass = new PlayerActivityBattlePass();
            var updated = next.ActivityInfo[activityId].ActivityParam.BattlePass;
            updated.Level = level;
            updated.Exp = exp;
            if (activate)
            {
                updated.Premium = true;
                updated.PremiumTime = Now;
                if (config.TryGetProperty("_iPrivilegeIds", out var privileges) && privileges.ValueKind == JsonValueKind.Array)
                    foreach (var id in privileges.EnumerateArray())
                        if (id.TryGetInt32(out int privilege) && privilege > 0) updated.ActivatedPrivilegeIds.Add(privilege);
            }
            var nextRuntime = CloneRuntime(session);
            nextRuntime.PassProgress[activityId] = new PassProgressRuntime
            { Initialized = true, LastBalance = balance, ActivityInstance = info.ActivityInstance };
            var costs = activate && !_freePremiumPass ? new[] { (Entry: premiumItem, Count: 1) } : [];
            if (await CommitAsync(session, next, costs: costs, runtime: nextRuntime,
                source: $"pass/activate/{activityId}/{info.ActivityInstance}/{premiumItem}") != 0) return;
        }
    }

    private static long Owned(GameSession session, int entry) => entry > 0
        ? session.Progress!.Currency.GetValueOrDefault(entry) + session.Items.Where(i => i.Entry == entry && i.Grid == 0).Sum(i => (long)i.Stack)
        : 0;
}

public sealed partial class ActivityRuntimeState
{
    public Dictionary<long, PassProgressRuntime> PassProgress { get; set; } = new();
}

public sealed class PassProgressRuntime
{
    public bool Initialized { get; set; }
    public long LastBalance { get; set; }
    public long ActivityInstance { get; set; }
}
