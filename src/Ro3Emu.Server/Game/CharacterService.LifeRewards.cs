using Romsg;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>活力獎勵必須與來源領取記錄同筆提交；通知只在提交成功後送出。</summary>
    internal sealed record LifeVigorRewardPlan(Action<RoleItemPlanBuilder> Persist, Action Apply, Func<Task> Sync,
        EconomyVigorRewardEvidence Evidence);
    internal Func<GameSession, long, DBRestrictInfo?, LifeVigorRewardPlan>? StageLifeVigorReward { get; set; }

    private LifeVigorRewardPlan? StageVigorReward(GameSession session, RoleProgress before, RoleProgress after, DBRestrictInfo? prospectiveLifeRestriction = null)
    {
        const int vigorReward = 407;
        long original = before.Currency.GetValueOrDefault(vigorReward);
        long increase = checked(after.Currency.GetValueOrDefault(vigorReward) - original);
        if (increase <= 0) return null;
        var plan = StageLifeVigorReward?.Invoke(session, increase, prospectiveLifeRestriction)
            ?? throw new InvalidOperationException("活力獎勵缺少生活限制額度處理器。");
        // 僅移轉本次獎勵；歷史 407 餘額需另行稽核，不能推定未補發而再次入帳。
        if (before.Currency.ContainsKey(vigorReward)) after.Currency[vigorReward] = original;
        else after.Currency.Remove(vigorReward);
        return plan;
    }
}
