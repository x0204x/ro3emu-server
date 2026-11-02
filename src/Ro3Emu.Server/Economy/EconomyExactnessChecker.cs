using System.Globalization;
using System.Numerics;

namespace Ro3Emu.Server.Economy;

/// <summary>
/// Pure committed-cut equations. No packets, clocks, RNG, gameplay builders or SQL writes.
/// Snapshot completeness and ledger continuity are prerequisites; a concrete violation still wins over unknowns.
/// This library verifies supplied evidence, not the authority of an arbitrary caller supplying that evidence.
/// </summary>
public static class EconomyExactnessChecker
{
    private readonly record struct BalanceKey(EconomyAsset Asset, EconomyLocation Location);

    public static EconomyCheckResult Check(EconomySnapshot opening, EconomySnapshot closing, EconomyLedgerWindow? ledger)
    {
        var issues = opening.Issues.Concat(closing.Issues).ToList();
        var uncovered = opening.UncoveredMutationFamilies.Concat(closing.UncoveredMutationFamilies)
            .Concat(ledger?.UncoveredMutationFamilies ?? []).Distinct().Order(StringComparer.Ordinal).ToArray();
        var before = Balances(opening, "opening", issues);
        var after = Balances(closing, "closing", issues);
        var assets = new List<EconomyResidual>();
        var locations = new List<EconomyLocationResidual>();
        void Unknown(string key, string detail) => issues.Add(new(EconomyIssueKind.Coverage, key, detail));
        void Fail(string key, string detail) => issues.Add(new(EconomyIssueKind.Corruption, key, detail));

        if (opening.Cut.RealmId != closing.Cut.RealmId || string.IsNullOrWhiteSpace(opening.Cut.RealmId))
            Unknown("cut/realm", "Cuts must name the same nonempty realm");
        if (opening.Cut.SchemaVersion != closing.Cut.SchemaVersion || string.IsNullOrEmpty(opening.Cut.SchemaVersion) ||
            opening.Cut.CatalogVersion != closing.Cut.CatalogVersion || string.IsNullOrEmpty(opening.Cut.CatalogVersion) ||
            opening.Cut.CoverageVersion != closing.Cut.CoverageVersion || string.IsNullOrEmpty(opening.Cut.CoverageVersion))
            Unknown("cut/version", "Schema, catalogue and coverage versions must match");
        if (opening.Cut.CommitSequence == null || closing.Cut.CommitSequence == null)
            Unknown("cut/watermark", "Legacy baseline has no committed economy sequence; wall-clock time is not a cut");
        else if (opening.Cut.CommitSequence < 0 || closing.Cut.CommitSequence < opening.Cut.CommitSequence)
            Fail("cut/order", "Invalid committed-cut order");

        if (ledger == null)
            Unknown("ledger", "No committed ledger window; existing holdings are a baseline, not new sources");
        else
        {
            issues.AddRange(ledger.Issues);
            if (!ledger.CompleteHistory) Unknown("ledger/continuity", "Archive/range or prior source-key continuity is not established");
            if (ledger.ReasonVersion != EconomyReasonCatalog.Version)
                Unknown("ledger/reasons", "Unknown reason catalogue version");
            if (ledger.RealmId != opening.Cut.RealmId || ledger.CatalogVersion != opening.Cut.CatalogVersion ||
                ledger.CoverageVersion != opening.Cut.CoverageVersion || ledger.FromExclusive != opening.Cut.CommitSequence ||
                ledger.ThroughInclusive != closing.Cut.CommitSequence)
                Unknown("ledger/cut", "Ledger identity or actual committed range differs from the snapshots");

            ValidateLedger(ledger, issues);
            var delta = new Dictionary<BalanceKey, BigInteger>();
            var sources = new Dictionary<EconomyAsset, BigInteger>();
            var sinks = new Dictionary<EconomyAsset, BigInteger>();
            foreach (var leg in ledger.Legs)
            {
                Add(delta, new(leg.Asset, leg.Location), leg.Delta);
                if (leg.Kind == EconomyLegKind.Source) Add(sources, leg.Asset, leg.Delta);
                if (leg.Kind == EconomyLegKind.Sink) Add(sinks, leg.Asset, -(BigInteger)leg.Delta);
            }
            var totalBefore = Totals(before);
            var totalAfter = Totals(after);
            var totalLegs = Totals(delta);
            foreach (var asset in totalBefore.Keys.Concat(totalAfter.Keys).Concat(totalLegs.Keys).Distinct())
            {
                var b = totalBefore.GetValueOrDefault(asset);
                var a = totalAfter.GetValueOrDefault(asset);
                var s = sources.GetValueOrDefault(asset);
                var k = sinks.GetValueOrDefault(asset);
                var l = totalLegs.GetValueOrDefault(asset);
                var residual = a - b - s + k;
                var legResidual = a - b - l;
                assets.Add(new(asset, N(b), N(a), N(s), N(k), N(l), N(residual), N(legResidual)));
                if (residual != 0 || legResidual != 0) Fail($"asset/{asset}", $"Source/sink residual {residual}; real-owner leg residual {legResidual}");
            }
            foreach (var key in before.Keys.Concat(after.Keys).Concat(delta.Keys).Distinct())
            {
                var residual = after.GetValueOrDefault(key) - before.GetValueOrDefault(key) - delta.GetValueOrDefault(key);
                locations.Add(new(key.Asset, key.Location, N(residual)));
                if (residual != 0) Fail($"location/{key}", $"Unexplained owner/location delta {residual}");
            }
            ValidateFingerprints(opening, closing, ledger, issues);
            ValidateCommittedBalances(before, ledger, issues);
            ValidateRewardProgress(opening, closing, ledger, issues);
        }
        var verdict = issues.Any(x => x.Kind == EconomyIssueKind.Corruption) ? EconomyVerdict.Fail
            : issues.Count != 0 || uncovered.Length != 0 ? EconomyVerdict.NotMeasured : EconomyVerdict.Pass;
        return new(verdict, assets, locations, [.. issues.Distinct()], uncovered);
    }

    private static void ValidateRewardProgress(EconomySnapshot opening, EconomySnapshot closing,
        EconomyLedgerWindow ledger, List<EconomyIssue> issues)
    {
        var ordered = ledger.Receipts.OrderBy(x => x.CommitSequence).ThenBy(x => x.CommitOrder).ToArray();
        foreach (long role in ordered.Where(x => EconomyMutation.IsRewardCommand(x.CommandKind))
                     .Select(x => x.ActorRoleId).Distinct())
        {
            if (role <= 0 || !opening.Progress.TryGetValue(role, out var first))
            {
                issues.Add(new(EconomyIssueKind.Coverage, "progress/" + role, "Opening cut lacks reward role progression."));
                continue;
            }
            bool hasOpeningRevision = opening.ProgressRevisions.TryGetValue(role, out long revision);
            bool hasClosingRevision = closing.ProgressRevisions.TryGetValue(role, out long closingRevision);
            bool revisionAware = hasOpeningRevision && hasClosingRevision;
            if (!revisionAware && (opening.ProgressRevisions.Count != 0 || closing.ProgressRevisions.Count != 0))
                issues.Add(new(EconomyIssueKind.Coverage, "progress/" + role, "A cut lacks the role progress revision."));
            EconomyProgress? known = first;
            foreach (var receipt in ordered.Where(x => x.ActorRoleId == role))
            {
                string key = "progress/" + receipt.OperationId;
                if (revisionAware)
                {
                    if (receipt.BeforeProgressRevision is not long before || receipt.AfterProgressRevision is not long after)
                    {
                        if (receipt.CommandKind != "dungeon-obligation-earn")
                        {
                            issues.Add(new(EconomyIssueKind.Coverage, key, "Receipt lacks role progress revision evidence."));
                            known = null;
                        }
                    }
                    else
                    {
                        if (before < revision || after < before)
                            issues.Add(new(EconomyIssueKind.Corruption, key, "Role progress revision moved backwards."));
                        else if (before > revision)
                        {
                            issues.Add(new(EconomyIssueKind.Coverage, key,
                                "Uncovered progress write occurred before this economy receipt."));
                            known = null;
                        }
                        revision = after;
                        if (!EconomyMutation.IsRewardCommand(receipt.CommandKind) && after > before)
                        {
                            issues.Add(new(EconomyIssueKind.Coverage, key,
                                "This economy receipt writes progress without resolved EXP evidence."));
                            known = null;
                        }
                    }
                }
                if (!EconomyMutation.IsRewardCommand(receipt.CommandKind)) continue;
                var e = receipt.RewardEvidence;
                if (e is null)
                {
                    issues.Add(new(EconomyIssueKind.Corruption, key, "Reward receipt lacks resolved EXP evidence."));
                    continue;
                }
                try { ValidateExpTransition(e); }
                catch (ArgumentException error)
                {
                    issues.Add(new(EconomyIssueKind.Corruption, key, error.Message));
                    continue;
                }
                if (known is not null && known != new EconomyProgress(e.BeforeBaseLevel, e.BeforeBaseExp, e.BeforeJobLevel, e.BeforeJobExp))
                    issues.Add(new(EconomyIssueKind.Corruption, key,
                        "Reward EXP before-state breaks the committed progression chain."));
                known = new(e.AfterBaseLevel, e.AfterBaseExp, e.AfterJobLevel, e.AfterJobExp);
            }
            if (revisionAware && closingRevision != revision)
            {
                issues.Add(new(closingRevision > revision ? EconomyIssueKind.Coverage : EconomyIssueKind.Corruption,
                    "progress/" + role, "Progress revision differs from the committed economy receipt chain."));
                known = null;
            }
            if (!closing.Progress.TryGetValue(role, out var final))
                issues.Add(new(EconomyIssueKind.Coverage, "progress/" + role, "Closing cut lacks role progression."));
            else if (known is not null && final != known)
                issues.Add(new(EconomyIssueKind.Corruption, "progress/" + role,
                    "Closing progression differs from committed reward receipts."));
        }
    }

    private static void ValidateExpTransition(EconomyRewardEvidence e)
    {
        var baseCosts = e.BaseLevelCosts ?? [];
        var jobCosts = e.JobLevelCosts ?? [];
        if (e.BeforeBaseLevel <= 0 || e.BeforeJobLevel <= 0 || e.AfterBaseLevel < e.BeforeBaseLevel ||
            e.AfterJobLevel < e.BeforeJobLevel || e.BeforeBaseExp < 0 || e.BeforeJobExp < 0 ||
            e.AfterBaseExp < 0 || e.AfterJobExp < 0 || e.ResolvedBaseExp < 0 || e.ResolvedJobExp < 0 ||
            e.BaseCapDiscard < 0 || e.JobCapDiscard < 0 ||
            baseCosts.Count != e.AfterBaseLevel - e.BeforeBaseLevel ||
            jobCosts.Count != e.AfterJobLevel - e.BeforeJobLevel ||
            baseCosts.Any(x => x <= 0) || jobCosts.Any(x => x < 0) ||
            (BigInteger)e.BeforeBaseExp + e.ResolvedBaseExp - baseCosts.Aggregate(BigInteger.Zero, (v, x) => v + x) - e.BaseCapDiscard != e.AfterBaseExp ||
            (BigInteger)e.BeforeJobExp + e.ResolvedJobExp - jobCosts.Aggregate(BigInteger.Zero, (v, x) => v + x) - e.JobCapDiscard != e.AfterJobExp)
            throw new ArgumentException("Resolved EXP cannot explain the level transitions and remainders.");
    }

    private static Dictionary<BalanceKey, BigInteger> Balances(EconomySnapshot snapshot, string phase, List<EconomyIssue> issues)
    {
        var result = new Dictionary<BalanceKey, BigInteger>();
        foreach (var h in snapshot.Holdings)
        {
            ValidateIdentity(h.Asset, h.Location, phase, issues);
            if (h.Quantity <= 0) issues.Add(new(EconomyIssueKind.Corruption, phase, "A materialized holding must have positive quantity"));
            if (h.Asset.Kind == EconomyAssetKind.Item && string.IsNullOrEmpty(h.Fingerprint))
                issues.Add(new(EconomyIssueKind.Coverage, phase, "Item holding lacks binding/attribute fingerprint evidence"));
            if (result.ContainsKey(new(h.Asset, h.Location)))
                issues.Add(new(EconomyIssueKind.Corruption, phase, "Duplicate denomination/location holding; distinct assets need distinct audit locations"));
            Add(result, new(h.Asset, h.Location), h.Quantity);
        }
        foreach (var pair in result.Where(x => x.Value > long.MaxValue))
            issues.Add(new(EconomyIssueKind.Corruption, phase, $"Location quantity overflow: {pair.Key}"));
        return result;
    }

    private static void ValidateLedger(EconomyLedgerWindow ledger, List<EconomyIssue> issues)
    {
        void Fail(string key, string detail) => issues.Add(new(EconomyIssueKind.Corruption, key, detail));
        var receiptMap = new Dictionary<string, EconomyReceipt>(StringComparer.Ordinal);
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in ledger.PriorSourceClaims)
        {
            if (string.IsNullOrWhiteSpace(claim.SourceKey) || string.IsNullOrWhiteSpace(claim.OperationId) || !claims.TryAdd(claim.SourceKey, claim.OperationId))
                Fail("source/history", "Invalid or duplicated retained source claim");
        }
        foreach (var receipt in ledger.Receipts)
        {
            string key = $"receipt/{receipt.OperationId}";
            if (string.IsNullOrWhiteSpace(receipt.OperationId) || !receiptMap.TryAdd(receipt.OperationId, receipt))
                Fail(key, "Invalid or duplicate operation receipt");
            if (!receipt.Committed || receipt.LegCount < 0 || receipt.CommitOrder < 0 || receipt.CommitSequence <= ledger.FromExclusive || receipt.CommitSequence > ledger.ThroughInclusive)
                Fail(key, "Receipt is uncommitted or outside the requested range, or has invalid leg count");
            foreach (string source in receipt.SourceKeys ?? [])
                if (string.IsNullOrWhiteSpace(source) || !claims.TryAdd(source, receipt.OperationId))
                    Fail(key, $"Invalid or already committed natural source: {source}");
        }
        foreach (var group in ledger.Receipts.GroupBy(x => (x.CommitSequence, x.CommitOrder)).Where(x => x.Count() > 1))
            Fail($"receipt/order/{group.Key}", "Ambiguous operation order within one committed group");
        var legNumbers = new HashSet<(string, int)>();
        foreach (var leg in ledger.Legs)
        {
            string key = $"leg/{leg.OperationId}/{leg.LegNumber}";
            ValidateIdentity(leg.Asset, leg.Location, key, issues);
            if (leg.LegNumber < 0 || !legNumbers.Add((leg.OperationId, leg.LegNumber))) Fail(key, "Invalid or duplicate leg number");
            if (!receiptMap.TryGetValue(leg.OperationId, out var receipt) || receipt.CommitSequence != leg.CommitSequence)
                Fail(key, "Missing receipt or different committed sequence");
            if (leg.CommitSequence <= ledger.FromExclusive || leg.CommitSequence > ledger.ThroughInclusive)
                Fail(key, "Leg is outside (opening, closing]");
            if (!EconomyReasonCatalog.Allows(leg.Reason, leg.Kind)) Fail(key, "Unknown/illegal reason and leg kind");
            if ((leg.Kind == EconomyLegKind.Source && leg.Delta <= 0) || (leg.Kind == EconomyLegKind.Sink && leg.Delta >= 0) ||
                (leg.Kind == EconomyLegKind.Transfer && leg.Delta == 0) || (leg.Kind == EconomyLegKind.Metadata && leg.Delta != 0))
                Fail(key, "Illegal delta sign");
            if (leg.Kind == EconomyLegKind.Source && (string.IsNullOrWhiteSpace(leg.SourceKey) ||
                !(receipt?.SourceKeys?.Contains(leg.SourceKey, StringComparer.Ordinal) ?? false)))
                Fail(key, "Source leg has no matching receipt natural-source claim");
            if (leg.Kind == EconomyLegKind.Transfer && (leg.EdgeNumber < 0 || leg.Counterparty == null || leg.Counterparty == leg.Location))
                Fail(key, "Transfer has no distinct real-owner counterpart");
            if (leg.Kind != EconomyLegKind.Transfer && leg.Counterparty != null) Fail(key, "External/metadata leg has a real-owner transfer counterpart");
            if (leg.Kind == EconomyLegKind.Metadata && (string.IsNullOrEmpty(leg.BeforeFingerprint) || string.IsNullOrEmpty(leg.AfterFingerprint)))
                Fail(key, "Metadata change lacks before/after evidence");
        }
        foreach (var receipt in ledger.Receipts)
            if (ledger.Legs.Count(x => x.OperationId == receipt.OperationId) != receipt.LegCount)
                Fail($"receipt/{receipt.OperationId}", "Receipt leg count mismatch");
        foreach (var edge in ledger.Legs.Where(x => x.Kind == EconomyLegKind.Transfer).GroupBy(x => (x.OperationId, x.EdgeNumber)))
        {
            var legs = edge.ToArray();
            if (legs.Length != 2 || legs[0].Asset != legs[1].Asset || (BigInteger)legs[0].Delta + legs[1].Delta != 0 ||
                legs[0].Counterparty != legs[1].Location || legs[1].Counterparty != legs[0].Location)
                Fail($"transfer/{edge.Key}", "Transfer edge must contain two opposite, reciprocal legs of the same denomination");
        }
    }

    private static void ValidateIdentity(EconomyAsset asset, EconomyLocation location, string key, List<EconomyIssue> issues)
    {
        if (!Enum.IsDefined(asset.Kind) || asset.Id <= 0 || asset.Epoch == null || !Enum.IsDefined(location.Owner.Kind) ||
            !Enum.IsDefined(location.Compartment) || string.IsNullOrWhiteSpace(location.Owner.Id) || string.IsNullOrWhiteSpace(location.Key) ||
            location.Instance == null || location.Owner.Id.Length > 512 || location.Key.Length > 2048 || location.Instance.Length > 512 || asset.Epoch.Length > 512)
            issues.Add(new(EconomyIssueKind.Corruption, key, "Invalid denomination, owner or location identity"));
    }

    private static void ValidateFingerprints(EconomySnapshot opening, EconomySnapshot closing, EconomyLedgerWindow ledger, List<EconomyIssue> issues)
    {
        var initial = opening.Holdings.Where(h => !string.IsNullOrEmpty(h.Fingerprint)).GroupBy(h => new BalanceKey(h.Asset, h.Location))
            .ToDictionary(g => g.Key, g => g.First().Fingerprint);
        var final = closing.Holdings.Where(h => !string.IsNullOrEmpty(h.Fingerprint)).GroupBy(h => new BalanceKey(h.Asset, h.Location))
            .ToDictionary(g => g.Key, g => g.First().Fingerprint);
        var states = new Dictionary<BalanceKey, string?>(initial.ToDictionary(x => x.Key, x => (string?)x.Value));
        var order = ledger.Receipts.GroupBy(x => x.OperationId).ToDictionary(x => x.Key, x => x.First().CommitOrder);
        foreach (var leg in ledger.Legs.OrderBy(x => x.CommitSequence).ThenBy(x => order.GetValueOrDefault(x.OperationId)).ThenBy(x => x.LegNumber))
        {
            var key = new BalanceKey(leg.Asset, leg.Location);
            if (leg.Asset.Kind == EconomyAssetKind.Item &&
                ((leg.Delta < 0 && string.IsNullOrEmpty(leg.BeforeFingerprint)) || (leg.Delta > 0 && string.IsNullOrEmpty(leg.AfterFingerprint))))
                issues.Add(new(EconomyIssueKind.Coverage, $"fingerprint/{leg.OperationId}/{leg.LegNumber}", "Item debit/credit lacks fingerprint evidence"));
            if (leg.BeforeFingerprint == null && leg.AfterFingerprint == null && !initial.ContainsKey(key) && !final.ContainsKey(key)) continue;
            if (leg.BeforeFingerprint != states.GetValueOrDefault(key))
                issues.Add(new(EconomyIssueKind.Corruption, $"fingerprint/{leg.OperationId}/{leg.LegNumber}", "Before fingerprint does not match the committed state chain"));
            states[key] = leg.AfterFingerprint;
        }
        foreach (var key in states.Keys.Concat(final.Keys).Distinct())
            if (states.GetValueOrDefault(key) != final.GetValueOrDefault(key))
                issues.Add(new(EconomyIssueKind.Corruption, $"fingerprint/{key}", "Closing instance fingerprint differs from ledger evidence"));
    }

    private static void ValidateCommittedBalances(Dictionary<BalanceKey, BigInteger> before, EconomyLedgerWindow ledger, List<EconomyIssue> issues)
    {
        var state = new Dictionary<BalanceKey, BigInteger>(before);
        foreach (var commit in ledger.Legs.GroupBy(x => x.CommitSequence).OrderBy(x => x.Key))
        {
            foreach (var leg in commit) Add(state, new(leg.Asset, leg.Location), leg.Delta);
            foreach (var key in commit.Select(x => new BalanceKey(x.Asset, x.Location)).Distinct())
                if (state[key] < 0 || state[key] > long.MaxValue)
                    issues.Add(new(EconomyIssueKind.Corruption, $"balance/{commit.Key}/{key}", "Committed balance underflow/overflow"));
        }
    }

    private static Dictionary<EconomyAsset, BigInteger> Totals(Dictionary<BalanceKey, BigInteger> values)
    {
        var totals = new Dictionary<EconomyAsset, BigInteger>();
        foreach (var (key, value) in values) Add(totals, key.Asset, value);
        return totals;
    }
    private static void Add<T>(Dictionary<T, BigInteger> target, T key, BigInteger value) where T : notnull => target[key] = target.GetValueOrDefault(key) + value;
    private static string N(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
