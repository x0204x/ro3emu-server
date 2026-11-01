using System.Security.Cryptography;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    public sealed record OwnedAwardSource(string CommandKind, string SourceKey, object Outcome);
    internal Func<GameSession, MonsterUnit, Ownership.ReservationKey[]>? GetKillTaskParticipantKeys { get; set; }
    internal Action<GameSession, MonsterUnit, RewardSettlement>? StageKillTaskObligations { get; set; }

    /// <summary>Stage E02/13e: freeze one owned payment and result before the writer is admitted.
    /// The natural source names a world or activity occurrence, while the operation ID identifies this attempt.</summary>
    internal EconomyMutation EconomyForOwnedExchange(GameSession session, string kind, string sourceKey,
        IReadOnlyList<(int Entry, long Count)> costs, IReadOnlyList<(int Entry, int Count)> goods, object outcome)
    {
        if (!EconomyMutation.IsOwnedExchangeCommand(kind))
            throw new ArgumentException("Expected a C/D 13e owned exchange.", nameof(kind));
        EconomyAsset Asset(int entry) => data.Row("ItemConfig", entry) is { } row
            ? new(GameData.Int(row, "_iType") == ItemTypeCurrency ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry)
            : throw new InvalidOperationException("Missing economy item configuration: " + entry);
        byte[] resolved = JsonSerializer.SerializeToUtf8Bytes(outcome);
        string fingerprint = Convert.ToHexString(SHA256.HashData(resolved));
        byte[] receipt = resolved.Length <= 15 * 1024 ? resolved
            : JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, ResultHash = fingerprint, ResultBytes = resolved.Length });
        return new EconomyMutation(Guid.NewGuid().ToString("N"), session.Account!, session.Role!.Id, kind,
            fingerprint, sourceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            costs.Where(x => x.Count > 0).GroupBy(x => x.Entry)
                .Select(g => new EconomyQuantity(Asset(g.Key), g.Sum(x => x.Count))).ToArray(),
            goods.Where(x => x.Count > 0).GroupBy(x => x.Entry)
                .Select(g => new EconomyQuantity(Asset(g.Key), g.Sum(x => (long)x.Count))).ToArray(), receipt);
    }

    private EconomyMutation EconomyForOwnedAward(GameSession session, OwnedAwardSource source, AwardPlan plan,
        RoleProgress before, RoleProgress after, IReadOnlyList<(int Entry, long Count)> costs,
        long baseExp, long jobExp, ItemAutoDecomposeTipsNtf conversion, EconomyVigorRewardEvidence? vigorEvidence = null)
    {
        if (source.CommandKind is not ("cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or
            "cd13e/journey-victory" or "cd13e/grant" or "cd13e/gm-grant"))
            throw new ArgumentException("Unsupported owned award source.", nameof(source));
        EconomyAsset Asset(int entry) => data.Row("ItemConfig", entry) is { } row
            ? new(GameData.Int(row, "_iType") == ItemTypeCurrency ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry)
            : throw new InvalidOperationException("Missing award item configuration: " + entry);
        EconomyQuantity[] Quantities(IEnumerable<(int Entry, long Count)> values) => values
            .Where(x => x.Entry is not (BaseExpItem or JobExpItem) && x.Count > 0)
            .GroupBy(x => x.Entry).Select(x => new EconomyQuantity(Asset(x.Key), x.Sum(v => v.Count))).ToArray();
        var original = Quantities(plan.Grants.Select(x => (x.Entry, (long)x.Count)));
        var inputs = Quantities(conversion.DecomposeItemInfo.Select(x => (x.Key, (long)x.Value)));
        var outputs = Quantities(conversion.DecomposeGainItemInfo.Values.SelectMany(x => x.ItemInfo)
            .Select(x => (x.Key, (long)x.Value)));
        var goods = original.Concat(inputs.Select(x => x with { Quantity = -x.Quantity })).Concat(outputs)
            .GroupBy(x => x.Asset).Select(x => new EconomyQuantity(x.Key, x.Sum(v => v.Quantity)))
            .Where(x => x.Quantity > 0).ToArray();
        var baseCosts = Enumerable.Range(before.Level, after.Level - before.Level).Select(BaseExpNeed).ToArray();
        var jobCosts = Enumerable.Range(before.JobLevel, after.JobLevel - before.JobLevel)
            .Select(level => JobExpNeed(before.Career, level)).ToArray();
        var evidence = new EconomyRewardEvidence(before.Level, before.Exp, after.Level, after.Exp,
            before.JobLevel, before.JobExp, after.JobLevel, after.JobExp, baseExp, jobExp,
            baseCosts, jobCosts, checked(before.Exp + baseExp - baseCosts.Sum() - after.Exp),
            checked(before.JobExp + jobExp - jobCosts.Sum() - after.JobExp), original, inputs, outputs);
        byte[] outcome = JsonSerializer.SerializeToUtf8Bytes(source.Outcome);
        if (outcome.Length > 15 * 1024)
            outcome = JsonSerializer.SerializeToUtf8Bytes(new
            { Version = 1, Hash = Convert.ToHexString(SHA256.HashData(outcome)) });
        var mutation = new EconomyMutation(Guid.NewGuid().ToString("N"), session.Account!, session.Role!.Id,
            source.CommandKind, Convert.ToHexString(SHA256.HashData(outcome)), source.SourceKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quantities(costs), goods, outcome, evidence);
        return vigorEvidence is null ? mutation : mutation.WithVigorEvidence(vigorEvidence);
    }
}
