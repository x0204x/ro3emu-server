using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The cash shop (MG_Pay 2001-2004) without payment: Ro3Emu:Shop:FreeProducts (owner decision 2026-10-31). The client
/// buys through Lua_SDK_PayMgr.ReqPurchaseRequest: 2001 CreateOrderReq {chargeType 1 RechargeConfig / 2 ProductConfig,
/// chargeId}, from the top-up panel (UI_Sys_TopUpListWidgetCtrl), the pass buy panel (LC_BattlePassHelper,
/// enum_ProductId.BattlePass) and the month cards (LC_MonthCardHelper.Purchase). On the PC build _onCreateOrderResult hides
/// the "Awaiting payment..." loader, shows ErrMsg for a nonzero result and otherwise opens orderData.data.transaction_url
/// when there is one; "{}" opens nothing. The goods go straight into the bag in the order's transaction (inferred: the
/// original paid after the store's callback); the pass and the month cards then take their owned entitlement items as
/// for a gift (ReconcilePassAsync / ReconcileMonthCardAsync). The top-up panel lists the first-purchase bonus for the
/// ids 2003 returns and drops an id when 2004 names it.
/// Caps, all (inferred) as the client data has no purchase limit: a top-up package FreeTopUpsPerDay times per game day,
/// its first-purchase bonus once per role; a gift pack once; a month card only below the card's own day limit
/// (LC_MonthCardHelper.JustIsUpperLimit); the pass only while the open pass is not premium and its item is not owned;
/// the pass gift token while none is owned, once per game day.
/// </summary>
public sealed partial class NewActivityService
{
    public const uint CreateOrder = 2001, PayResult = 2002, FirstRechargeAward = 2003, FirstRechargeUpdate = 2004;

    /// <summary>EPayChargeType (Lua_SDK_PayMgr) and EProductType (MG_Pay.proto).</summary>
    private const long ChargeRecharge = 1, ChargeProduct = 2, ProductGiftPack = 1, ProductMonthCard = 2, ProductPass = 3;

    /// <summary>The PC rows: UI_Sys_TopUpPanelCtrl's m_kPlatformCode when neither UNITY_ANDROID nor UNITY_IOS.</summary>
    private const long PcPlatform = 3;

    private const long MonthCardActivity = 199;
    private const int MonthCardType = 21, PassType = 23;

    public async Task CreateOrderAsync(GameSession session, CreateOrderReq req)
    {
        var response = new CreateOrderResp { ChargeId = req.ChargeId, ChargeType = req.ChargeType };
        long firstCharge = 0;
        if (!_freeProducts || session.Role == null) response.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            var lease = _deadlines.Capture(session);
            LegacyOwnerScope? ownerScope = _loop.Owners != null ? await EnterLocalWithGateAsync(session, "activity/purchase") : null;
            if (ownerScope == null) await session.ActivityGate.WaitAsync();
            try
            {
                if (!_deadlines.IsCurrent(session, lease)) return;
                string order = $"ro3emu-{session.Role.Id.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
                (response.Result, firstCharge) = await PurchaseAsync(session, req, order);
                if (response.Result == 0) { response.OrderId = order; response.OrderData = "{}"; }
                if (!_deadlines.IsCurrent(session, lease)) return;
            }
            finally { session.ActivityGate.Release(); ownerScope?.Dispose(); }
        }
        if (response.Result != 0) response.ErrMsg = ErrorText(response.Result);
        await session.SendGameAsync(CreateOrder, response);
        if (firstCharge > 0) await session.SendGameAsync(FirstRechargeUpdate, new UpdateRechargeFirstAwardResp { RechargeId = firstCharge });
    }

    /// <summary>2002: an order this server issued to the role is paid already; anything else is "Order does not exist".</summary>
    public Task PayResultAsync(GameSession session, PayResultReq req) => session.SendGameAsync(PayResult, new PayResultResp
    {
        OrderId = req.OrderId,
        Result = !_freeProducts ? ErrorCodes.FeatureUnavailable
            : session.Role != null && req.OrderId.StartsWith($"ro3emu-{session.Role.Id.ToString(CultureInfo.InvariantCulture)}-",
                StringComparison.Ordinal) ? 0 : ErrorCodes.ShopGiftOrderMissing,
    });

    /// <summary>2003: the PC packages whose first-purchase bonus the role has not taken.</summary>
    public Task FirstRechargeAwardsAsync(GameSession session)
    {
        var response = new GetRechargeFirstAwardResp();
        if (!_freeProducts || session.ActivityRuntime is not { } runtime) response.Result = ErrorCodes.FeatureUnavailable;
        else response.IdList.Add(RechargeConfig.All(_data)
            .Where(r => r.Platform == PcPlatform && r.FirstChargeAward.Length >= 2
                && runtime.Purchases.GetValueOrDefault(PurchaseKey(ChargeRecharge, r.Id)) is not { Total: > 0 })
            .Select(r => r.Id).Order());
        return session.SendGameAsync(FirstRechargeAward, response);
    }

    private static string PurchaseKey(long chargeType, long id) => $"{chargeType}:{id}";

    private string ErrorText(long code) => ErrorCodeConfig.Get(_data, code) is { Message: [var text, ..] } && _data.Text(text) is { Length: > 0 } s
        ? s : code.ToString(CultureInfo.InvariantCulture);

    private long GameDayNumber => DateOnly.FromDateTime(DailyActivityService.GameDay(_clock.GetLocalNow().DateTime)).DayNumber;

    private async Task<(int Result, long FirstCharge)> PurchaseAsync(GameSession session, CreateOrderReq req, string order)
    {
        if (!session.ActivitiesReady || session.NewActivities == null || session.ActivityRuntime == null || session.Progress == null)
            return (ErrorCodes.FeatureUnavailable, 0);
        string key = PurchaseKey(req.ChargeType, req.ChargeId);
        var bought = session.ActivityRuntime.Purchases.GetValueOrDefault(key) ?? new PurchaseRecord();
        long day = GameDayNumber;
        int today = bought.Day == day ? bought.DayCount : 0;
        (int Result, List<AwardGrant> Grants, long FirstCharge) priced = req.ChargeType switch
        {
            ChargeRecharge => Recharge(req.ChargeId, bought, today),
            ChargeProduct => Product(session, req.ChargeId, bought, today),
            _ => (ErrorCodes.TopUpType, [], 0),
        };
        if (priced.Result != 0) return (priced.Result, 0);
        var runtime = CloneRuntime(session);
        runtime.Purchases[key] = new PurchaseRecord { Total = bought.Total + 1, Day = day, DayCount = today + 1 };
        int result = await CommitAsync(session, session.NewActivities.Clone(), extra: priced.Grants, runtime: runtime,
            source: "purchase/" + order);
        if (result != 0) return (result, 0);
        await ReconcilePassAsync(session);
        await ReconcileMonthCardAsync(session);
        return (0, priced.FirstCharge);
    }

    /// <summary>A top-up: _kAward, plus _kFirstChargeAward the first time or _kAwardBonus after (UI_Sys_TopUpListWidgetCtrl
    /// shows "First Purchase Bonus" for an id 2003 listed, else "Extra Bonus").</summary>
    private (int, List<AwardGrant>, long) Recharge(long id, PurchaseRecord bought, int today)
    {
        if (RechargeConfig.Get(_data, id) is not { } row || row.Platform != PcPlatform || Grant(row.Award) is not { } award)
            return (ErrorCodes.TopUpOption, [], 0);
        if (today >= Math.Max(1, _freeTopUpsPerDay)) return (ErrorCodes.TopUpLimit, [], 0);
        bool first = bought.Total == 0 && Grant(row.FirstChargeAward) != null;
        List<AwardGrant> grants = [award];
        if (Grant(first ? row.FirstChargeAward : row.AwardBonus) is { } bonus) grants.Add(bonus);
        return (0, grants, first ? row.Id : 0);
    }

    private AwardGrant? Grant(long[] pair) => pair is [> 0 and <= int.MaxValue, > 0 and <= int.MaxValue, ..]
        && _data.Row("ItemConfig", pair[0]) != null ? new AwardGrant((int)pair[0], (int)pair[1]) : null;

    private (int, List<AwardGrant>, long) Product(GameSession session, long id, PurchaseRecord bought, int today)
    {
        if (ProductConfig.Get(_data, id) is not { } row || row.Platform != PcPlatform) return (ErrorCodes.TopUpOption, [], 0);
        List<AwardGrant> grants;
        try { grants = [.. _awards.ResolveTask(session, row.Award, Playbook(session)).Grants]; }
        catch (AwardResolutionException) { return (ErrorCodes.RewardMissing, [], 0); }
        if (grants.Count == 0) return (ErrorCodes.RewardMissing, [], 0);
        int result = row.Type switch
        {
            ProductGiftPack => bought.Total > 0 ? ErrorCodes.AlreadyPurchased : 0,
            ProductMonthCard => MonthCardRoom(session, grants),
            ProductPass => PassRoom(session, grants, today),
            _ => ErrorCodes.TopUpType,
        };
        return (result, grants, 0);
    }

    /// <summary>The month cards whose item the product grants, below their day limit; their _iBuyAward joins the order.</summary>
    private int MonthCardRoom(GameSession session, List<AwardGrant> grants)
    {
        if (ActivityError(session, MonthCardActivity, MonthCardType) is var error and not 0) return error;
        var cards = MonthCardConfig.All(_data).Where(c => grants.Any(g => g.Entry == c.Item)).ToList();
        if (cards.Count == 0) return ErrorCodes.TopUpOption;
        var owned = session.NewActivities!.ActivityInfo[MonthCardActivity].ActivityParam.MonthCard.Infos;
        foreach (var card in cards)
        {
            long lastDays = owned.GetValueOrDefault(card.Id) is { } info && info.ExpireTs > Now ? info.LastDays : 0;
            // Owned cards not yet converted count as the days they will add.
            lastDays += Owned(session, (int)card.Item) * card.Day;
            if (lastDays + card.Day >= card.DayLimit) return ErrorCodes.TopUpLimit;
        }
        try
        {
            foreach (var card in cards.Where(c => c.BuyAward > 0))
                grants.AddRange(_awards.Resolve(session, card.BuyAward, Playbook(session)).Grants);
        }
        catch (AwardResolutionException) { return ErrorCodes.RewardMissing; }
        return 0;
    }

    /// <summary>The open passes whose premium or gift item the product grants (BattlePassConfig).</summary>
    private int PassRoom(GameSession session, List<AwardGrant> grants, int today)
    {
        var passes = BattlePassConfig.All(_data).Where(p => ActivityError(session, p.Id, PassType) == 0).ToList();
        var premium = passes.Where(p => grants.Any(g => g.Entry == p.PremiumItemId)).ToList();
        var gift = passes.Where(p => grants.Any(g => g.Entry == p.GiftItemId)).ToList();
        if (premium.Count == 0 && gift.Count == 0) return ErrorCodes.BattlePassClosed;
        if (premium.Count > 0 && premium.All(p => Owned(session, (int)p.PremiumItemId) > 0
            || session.NewActivities!.ActivityInfo[p.Id].ActivityParam.BattlePass.Premium)) return ErrorCodes.AlreadyPurchased;
        if (gift.Count > 0 && (today > 0 || gift.Any(p => Owned(session, (int)p.GiftItemId) > 0))) return ErrorCodes.TopUpLimit;
        return 0;
    }
}

public sealed partial class ActivityRuntimeState
{
    /// <summary>Free purchases by "chargeType:chargeId" (NewActivityService.Purchases).</summary>
    public Dictionary<string, PurchaseRecord> Purchases { get; set; } = [];
}

public sealed class PurchaseRecord
{
    public long Total { get; set; }
    public long Day { get; set; }
    public int DayCount { get; set; }
}
