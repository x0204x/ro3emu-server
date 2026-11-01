using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    internal string RewardRealmId => roles.RewardRealmId;
    internal Task<bool> RewardSourceCommittedAsync(string sourceKey) => roles.RewardSourceCommittedAsync(sourceKey);

    /// <summary>E04: the declared roll, automatic conversion, consumed EXP thresholds and final progress
    /// accompany the source claim in the same transaction as the detached bag and currency changes.</summary>
    private EconomyMutation RewardMutation(GameSession session, RewardSettlement settlement,
        RoleProgress before, RoleProgress after, IReadOnlyList<(int Entry, long Count)> costs,
        IReadOnlyList<(int Entry, int Count)> goods, long baseExp, long jobExp, ItemAutoDecomposeTipsNtf conversion,
        EconomyVigorRewardEvidence? vigorEvidence = null)
    {
        EconomyAsset Asset(int entry) => new(IsCurrency(entry) ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry);
        EconomyQuantity[] Quantities(IEnumerable<(int Entry, long Count)> values) => values
            .Where(v => v.Entry is not (BaseExpItem or JobExpItem) && v.Count > 0)
            .GroupBy(v => v.Entry).Select(g => new EconomyQuantity(Asset(g.Key), g.Sum(v => v.Count))).ToArray();
        var original = Quantities(goods.Select(g => (g.Entry, (long)g.Count)));
        var inputs = Quantities(conversion.DecomposeItemInfo.Select(x => (x.Key, (long)x.Value)));
        var outputs = Quantities(conversion.DecomposeGainItemInfo.Values.SelectMany(x => x.ItemInfo).Select(x => (x.Key, (long)x.Value)));
        var final = original.Concat(inputs.Select(q => q with { Quantity = -q.Quantity })).Concat(outputs)
            .GroupBy(q => q.Asset).Select(g => new EconomyQuantity(g.Key, g.Sum(q => q.Quantity)))
            .Where(q => q.Quantity != 0).ToArray();
        var baseCosts = Enumerable.Range(before.Level, after.Level - before.Level).Select(BaseExpNeed).ToArray();
        var jobCosts = Enumerable.Range(before.JobLevel, after.JobLevel - before.JobLevel).Select(l => JobExpNeed(before.Career, l)).ToArray();
        var evidence = new EconomyRewardEvidence(before.Level, before.Exp, after.Level, after.Exp,
            before.JobLevel, before.JobExp, after.JobLevel, after.JobExp, baseExp, jobExp,
            baseCosts, jobCosts, checked(before.Exp + baseExp - baseCosts.Sum() - after.Exp),
            checked(before.JobExp + jobExp - jobCosts.Sum() - after.JobExp), original, inputs, outputs);
        var mutation = new EconomyMutation(settlement.OperationId, session.Account!, session.Role!.Id, settlement.CommandKind,
            JsonSerializer.Serialize(new { Version = 1, settlement.SourceKey, settlement.CommandKind }), settlement.SourceKey,
            settlement.EffectiveUtcMs, Quantities(costs), final,
            JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Result = 0, settlement.SourceKey, Reward = evidence }), evidence);
        return vigorEvidence is null ? mutation : mutation.WithVigorEvidence(vigorEvidence);
    }
}
