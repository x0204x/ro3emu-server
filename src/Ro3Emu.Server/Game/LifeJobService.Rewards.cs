using System.Security.Cryptography;
using Google.Protobuf;
using Ro3Emu.Server.Economy;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class LifeJobService
{
    /// <summary>
    /// G 寶箱以物品 407 表示活力獎勵，生活 UI／採集實際讀取 5018 的限制 1004。
    /// 先建立複本，由獎勵交易連同來源記錄提交；不得用交易後 hook 另外補發。
    /// </summary>
    private CharacterService.LifeVigorRewardPlan StageVigorReward(GameSession session, long amount, DBRestrictInfo? prospective)
    {
        if (amount <= 0 || session.Role == null || session.Restrictions == null
            || !session.Restrictions.RestrictCount.ContainsKey(VigorId))
            throw new InvalidOperationException("活力獎勵要求已載入的角色限制額度。");
        if (ReferenceEquals(prospective, session.Restrictions))
            throw new InvalidOperationException("生活獎勵必須合成到未發布的限制複本。");
        long role = session.Role.Id;
        // 生活製造已先扣除成本時，合成到同一份未發布複本，不能用舊快照覆蓋。
        var next = prospective ?? session.Restrictions.Clone();
        var vigor = next.RestrictCount[VigorId];
        long beforeReward = vigor.Count;
        string beforeHash = Convert.ToHexString(SHA256.HashData(session.Restrictions.ToByteArray()));
        long cap = Param("VitalityExtraLimit", RestrictMax(VigorId));
        long accepted = Math.Min(amount, Math.Max(0, cap - vigor.Count));
        vigor.Count = checked(vigor.Count + accepted);
        return new CharacterService.LifeVigorRewardPlan(
            tx => { if (prospective == null) tx.RoleBlob(role, "life_restrict", next.ToByteArray()); },
            () => { if (prospective == null) session.Restrictions = next; },
            () => SyncRestrictionsAsync(session),
            new EconomyVigorRewardEvidence(amount, accepted, session.Restrictions.RestrictCount[VigorId].Count,
                beforeReward, vigor.Count, beforeHash, Convert.ToHexString(SHA256.HashData(next.ToByteArray()))));
    }
}
