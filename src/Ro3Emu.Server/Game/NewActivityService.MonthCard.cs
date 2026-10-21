using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Activity 199/5054 MonthCard, using the two ProductConfig item entitlements.</summary>
public sealed partial class NewActivityService
{
    private void InitializeMonthCard(GameSession session, JsonElement row, PlayerActivityInfo info)
    {
        if (GameData.Int(row, "_iActivityType") != 21) return;
        info.ActivityParam ??= new PlayerActivityParam();
        info.ActivityParam.MonthCard ??= new DBMonthCardInfo();
    }

    private async Task<bool> HandleMonthCardCommonAsync(GameSession session, CommonActivityReq req, CommonActivityRes res)
    {
        if (req.GainMonthCardDailyAward == null) return false;
        res.GainMonthCardDailyAward = new GainMonthCardDailyAwardResp { Req = req.GainMonthCardDailyAward.Clone() };
        res.Result = await ClaimMonthCardAsync(session, req.ActivityId, req.GainMonthCardDailyAward);
        return true;
    }

    private async Task<int> ClaimMonthCardAsync(GameSession session, long activityId, GainMonthCardDailyAwardReq req)
    {
        if (ActivityError(session, activityId, 21) is var error and not 0) return error;
        if (activityId != 199 || req.MonthCardId.Count == 0 || req.MonthCardId.Count != req.MonthCardId.Distinct().Count())
            return ErrorCodes.Parameter;
        long day = _activityClock.Day(Now);
        var current = session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard;
        var groups = new List<long>();
        foreach (long id in req.MonthCardId)
        {
            if (_data.Row("MonthCardConfig", id) is not { } config) return ErrorCodes.Parameter;
            if (!current.Infos.TryGetValue(id, out var card) || card.MonthCardId != id) return ErrorCodes.MonthCardNotOwned;
            if (card.ExpireTs <= Now) return ErrorCodes.MonthCardExpired;
            if (card.DayAward || session.ActivityRuntime!.MonthCards.GetValueOrDefault(id)?.ClaimedDay == day)
                return ErrorCodes.MonthCardClaimed;
            long group = GameData.Int(config, "_iDailyAward");
            if (group <= 0) return ErrorCodes.RewardMissing;
            groups.Add(group);
        }
        var next = session.NewActivities.Clone();
        var nextRuntime = CloneRuntime(session);
        foreach (long id in req.MonthCardId)
        {
            next.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].DayAward = true;
            nextRuntime.MonthCards[id] = new MonthCardRuntime { ClaimedDay = day };
        }
        return await CommitAsync(session, next, groups, runtime: nextRuntime,
            source: $"month-card/claim/{day}/{string.Join(',', req.MonthCardId.Order())}");
    }

    /// <summary>Convert owned 701/702 once and advance each card at the realm-local day boundary (inferred).</summary>
    private async Task ReconcileMonthCardAsync(GameSession session)
    {
        if (session.NewActivities == null || session.ActivityRuntime == null || ActivityError(session, 199, 21, allowInitializing: true) != 0) return;
        foreach (var row in _data.Table("MonthCardConfig").Values)
        {
            long id = GameData.Int(row, "_iId");
            int token = checked((int)GameData.Int(row, "_iItem"));
            int days = checked((int)GameData.Int(row, "_iDay"));
            int maxDays = checked((int)GameData.Int(row, "_iDayLimit"));
            if (id <= 0 || token <= 0 || days <= 0 || maxDays < days) continue;
            long today = _activityClock.Day(Now);
            var existing = session.NewActivities.ActivityInfo[199].ActivityParam.MonthCard.Infos.GetValueOrDefault(id);
            long expiryDay = existing?.ExpireTs > 0 ? _activityClock.Day(existing.ExpireTs - 1) + 1 : today;
            bool expired = existing == null || existing.ExpireTs <= Now;
            long baseDay = expired ? today : expiryDay;
            int available = (int)Math.Min(Owned(session, token), (maxDays - (baseDay - today)) / days);
            available = Math.Clamp(available, 0, 4); // config 143 days admits at most four fresh months
            long newExpiryDay = baseDay + (long)available * days;
            long newExpiry = available > 0 ? StartOfLocalDay(newExpiryDay) : existing?.ExpireTs ?? 0;
            long lastDays = newExpiry > Now ? Math.Max(1, _activityClock.Day(newExpiry - 1) - today + 2) : 0;
            var oldRuntime = session.ActivityRuntime.MonthCards.GetValueOrDefault(id);
            long claimDay = oldRuntime?.ClaimedDay ?? (existing?.DayAward == true ? today : 0);
            bool dayAward = newExpiry <= Now || claimDay == today;
            bool changed = available > 0 || existing != null && (existing.LastDays != lastDays || existing.DayAward != dayAward)
                || oldRuntime == null && existing?.DayAward == true;
            if (!changed) continue;
            var next = session.NewActivities.Clone();
            var nextRuntime = CloneRuntime(session);
            nextRuntime.MonthCards[id] = new MonthCardRuntime { ClaimedDay = claimDay };
            next.ActivityInfo[199].ActivityParam.MonthCard.Infos[id] = new MonthCardInfo
            {
                MonthCardId = id, ExpireTs = newExpiry, LastDays = lastDays, DayAward = dayAward,
            };
            var costs = available > 0 ? new[] { (Entry: token, Count: available) } : [];
            if (await CommitAsync(session, next, costs: costs, runtime: nextRuntime,
                source: $"month-card/convert/{id}/{newExpiry}") != 0) return;
        }
    }

    private long StartOfLocalDay(long dayNumber)
    {
        DateTime date = DateOnly.FromDayNumber(checked((int)dayNumber)).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(date, _activityClock.LocalTimeZone.GetUtcOffset(date)).ToUnixTimeSeconds();
    }
}

public sealed partial class ActivityRuntimeState
{
    public Dictionary<long, MonthCardRuntime> MonthCards { get; set; } = new();
}

public sealed class MonthCardRuntime
{
    public long ClaimedDay { get; set; }
}
