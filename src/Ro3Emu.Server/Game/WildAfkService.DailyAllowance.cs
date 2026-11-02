using System.Text.Json;

namespace Ro3Emu.Server.Game;

public sealed partial class WildAfkService
{
    /// <summary>本服指定每日 00:00 補充；採用與其他活動相同的遊戲時區，不修改其 05:00 日界。</summary>
    private long AllowanceDay(long nowMs)
    {
        var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), _clock.LocalTimeZone).Date;
        return new DateTimeOffset(day, _clock.LocalTimeZone.GetUtcOffset(day)).ToUnixTimeSeconds();
    }

    /// <summary>
    /// 原角色的 DailyEpoch 日期代表已領過該日額度。遷移只記錄該日期：今日 05:00 已領過不重發，
    /// 凌晨仍留昨日日期則補今日一次。離線多天只補目前登入日，不能按經過天數追補。
    /// 呼叫端持有角色 owner；一般計時／查詢另外持有 AFK gate，登入則尚未發布角色。
    /// </summary>
    private long RestoreAllowance(GameSession session, long now)
    {
        var state = session.WildAfk!;
        long current = AllowanceDay(now);
        long previous = state.AllowanceEpoch != 0 ? state.AllowanceEpoch : AllowanceDay(state.DailyEpoch * 1000);
        bool grant = current > previous;
        if (!grant && state.AllowanceEpoch != 0) return 0;
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
        next.AccountedAtMs = state.AccountedAtMs;
        next.LastKillAtMs = state.LastKillAtMs;
        long consumed = 0;
        if (grant)
        {
            bool running = next.Info.IsHighMagnification && !next.Info.IsSuspended;
            if (running) consumed = ConsumeUntil(next, Math.Min(now, current * 1000));
            next.Info.HighMultiplierTimeSeconds = Math.Min(CapSeconds, next.Info.HighMultiplierTimeSeconds + DailySeconds);
            next.Info.IsHighMagnification = true;
            next.Info.IsSuspended = !running;
            next.AccountedAtMs = Math.Max(next.AccountedAtMs, current * 1000);
        }
        next.AllowanceEpoch = grant ? current : previous;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        _roles.SaveWildAfk(session.Role!.Id, bytes);
        // 先完成持久化才發布額度與日期；失敗時舊狀態完整保留，可安全重試。
        state.Info = next.Info;
        state.AllowanceEpoch = next.AllowanceEpoch;
        state.ConsumedTodaySeconds = next.ConsumedTodaySeconds;
        state.RemainderMs = next.RemainderMs;
        state.AccountedAtMs = next.AccountedAtMs;
        state.Persisted = bytes;
        state.LazyMarked = null;
        _lazySave.Persisted(session);
        return consumed;
    }
}
