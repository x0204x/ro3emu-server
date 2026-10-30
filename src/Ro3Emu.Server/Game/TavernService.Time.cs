using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class TavernService
{
    // 非持久化的傳輸缺口，僅記錄事實；下一次角色 owner 交易才結算 meter。
    private sealed class TransportGap(long start) { public long Start { get; } = start; public long? End { get; set; } }
    private readonly Dictionary<GameSession, List<TransportGap>> _transportGaps = new();
    private void ObserveTransportLost(GameSession s, long at)
    {
        _loop.Owners?.AssertLoop();
        if (!ValidLease(s)) return;
        if (!_transportGaps.TryGetValue(s, out var gaps)) _transportGaps[s] = gaps = [];
        // 重複的失聯通知保留第一個時點，不把較晚通知當作新的可計時區段。
        if (gaps.Count == 0 || gaps[^1].End != null) gaps.Add(new(at));
    }
    private void ObserveTransportRestored(GameSession s, long at)
    {
        _loop.Owners?.AssertLoop();
        if (_transportGaps.TryGetValue(s, out var gaps) && gaps.Count > 0 && gaps[^1].End == null)
            gaps[^1].End = Math.Max(gaps[^1].Start, at);
    }
    private long InactiveMs(GameSession s, long from, long to) => !_transportGaps.TryGetValue(s, out var gaps) ? 0
        : gaps.Sum(gap => Math.Max(0, Math.Min(to, gap.End ?? to) - Math.Max(from, gap.Start)));

    private long Goal => TaskConfig.Get(_data, PublicParamConfig.BarroomTaskId(_data)) is { } row
        && row.ProgressTarget is [[106010, > 0]] ? row.ProgressTarget[0][1] : 0;

    /// <summary>（推定）僅已提交且仍附著的房間租約累積時間；離線不補算。</summary>
    public static Meter Accrue(Meter previous, long cutoffMs, bool eligible, long goalSeconds, long inactiveMs = 0)
    {
        var next = Copy(previous);
        long end = Math.Max(previous.AccountedAtMs, cutoffMs);
        if (eligible && goalSeconds > 0)
            next.ActiveMs = Math.Min(checked(goalSeconds * 1000),
                checked(previous.ActiveMs + Math.Max(0, end - previous.AccountedAtMs - inactiveMs)));
        next.AccountedAtMs = end;
        return next;
    }

    private async Task AdvanceCoreAsync(GameSession s, long cutoff)
    {
        if (s.Role == null || s.Tavern is not { } previous || !ValidLease(s)) return;
        if (_detaching.TryGetValue(s, out long detached)) cutoff = Math.Min(cutoff, detached);
        cutoff = Math.Min(cutoff, checked((previous.DayEpoch + 86400) * 1000));
        var next = Accrue(previous, cutoff, eligible: true, Goal, InactiveMs(s, previous.AccountedAtMs, cutoff));
        if (next.AccountedAtMs == previous.AccountedAtMs) return;
        long seconds = next.ActiveMs / 1000 - previous.ActiveMs / 1000;
        string source = $"tavern/{s.Role.Id}/{previous.DayEpoch}/{previous.Lease}/{previous.AccountedAtMs}/{next.AccountedAtMs}";
        var staged = _tasks.StageTavernSeconds(s, source, seconds);
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role.Id, "tavern", JsonSerializer.SerializeToUtf8Bytes(next));
        staged?.Persist(transaction);
        await _roles.CommitAsync(transaction, "tavern/time", _ =>
        {
            s.Tavern = next; staged?.Apply();
            if (_transportGaps.TryGetValue(s, out var gaps))
            {
                gaps.RemoveAll(gap => gap.End is { } end && end <= next.AccountedAtMs);
                if (gaps.Count == 0) _transportGaps.Remove(s);
            }
        });
    }

    private async Task<bool> BeforeResetAsync(GameSession s, long boundary)
    {
        if (s.Tavern is not { } meter) return true;
        // 每日結算已持有角色與 TaskLock；此處不反向取得房間鎖。
        await AdvanceCoreAsync(s, Math.Min(boundary * 1000, checked((meter.DayEpoch + 86400) * 1000)));
        if (PendingAward(s)) await _tasks.SettleOwnerProgressCoreAsync(s);
        return !PendingAward(s);
    }

    private bool PendingAward(GameSession s) => PendingDailyAward(s.Tavern, s.Daily, s.Tasks, s.DailyRows,
        PublicParamConfig.BarroomTaskId(_data), Goal);

    // 跨日登入時 TaskService 尚未載入；用持久化任務副本守住尚未成功交付的舊日債務。
    internal static bool PendingDailyAward(Meter? meter, DailyActivityService.DailyState? daily,
        DBTaskInfo? quests, DBDailyActivityInfo? rows, long taskId, long goalSeconds) => meter != null
        && daily?.DailyEpoch == meter.DayEpoch && goalSeconds > 0 && meter.ActiveMs / 1000 >= goalSeconds
        && quests?.Infos.ContainsKey(taskId) == true && quests.CompleteTaskNum.GetValueOrDefault(taskId) == 0
        && quests.Infos[taskId].Status != TaskState.Delivery
        && rows?.ActivityData.TryGetValue((int)taskId, out var row) == true && row.TaskCompleteTimes == 0;

    /// <summary>定時器與正常事件共用的實際房間計時入口；不接受客戶端自報秒數。</summary>
    public async Task ReconcileAsync(GameSession s)
    {
        if (s.Role == null || !ValidLease(s)) return;
        using var scope = await EnterScopeAsync(s, room: false);
        await _daily.ReconcileAsync(s);
        await AdvanceCoreAsync(s, Now);
        await _tasks.RefreshAsync(s);
        await SyncAsync(s);
    }


    private (byte[] Bytes, Action Committed)? PlanRollover(GameSession s, long boundary)
    {
        if (s.Tavern is not { } meter) return null;
        var next = Copy(meter);
        next.DayEpoch = boundary; next.ActiveMs = 0;
        next.AccountedAtMs = Math.Max(next.AccountedAtMs, boundary * 1000);
        return (JsonSerializer.SerializeToUtf8Bytes(next), () => s.Tavern = next);
    }
}
