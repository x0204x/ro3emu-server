namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // LAB 訂閱者自行限制樣本數；正式預設不訂閱、不記錄角色或封包內容。
    internal readonly record struct ImpactTimingSample(long CastId, int SkillId, string Stage, long TimeMs, long TargetId);
    internal Action<ImpactTimingSample>? ImpactTimingObserver { get; set; }

    private void TraceImpact(ActiveSkillCast action, string stage, long targetId = 0)
    {
        if (ImpactTimingObserver is not { } observer) return;
        try { observer(new(action.Id, action.Request.SkillId, stage, Now, targetId)); }
        catch { /* 診斷不能中斷傷害交易或改變擊殺結果。 */ }
    }

    private void ScheduleCastDeadline(ActiveSkillCast action, long offsetMs, Func<Task> callback)
        => ScheduleRuntime(action.WorldScene,
            TimeSpan.FromMilliseconds(Math.Max(0, action.StartMs + offsetMs - Now)), callback);
}
