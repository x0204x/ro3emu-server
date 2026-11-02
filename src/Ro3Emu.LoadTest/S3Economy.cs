using Ro3Emu.Server.Economy;

namespace Ro3Emu.LoadTest;

public sealed partial class RunReport
{
    public EconomySnapshot? S3OpeningEconomy { get; set; }
    public EconomySnapshot? S3ClosingEconomy { get; set; }
    /// <summary>Delivery observations only; never used as the committed accounting oracle.</summary>
    public Dictionary<int, long> S3ExpectedExternalChange { get; set; } = [];
    public string[] S3UntrackedEconomySources { get; set; } = [];
    public string[] S3SetupFixtures { get; set; } = [];
    /// <summary>Retained E01 receipts between the actual committed cuts; incomplete mutation coverage prevents PASS.</summary>
    public EconomyLedgerWindow? S3CommittedLedger { get; set; }
    public EconomyCheckResult? S3EconomyCheck { get; set; }
    public int S3GatherCompletions { get; set; }
    public int S3TowerEntries { get; set; }
    public int S3TowerSettlements { get; set; }
    public bool S3CompletedRun { get; set; }

    public TargetRow EvaluateLedgerRow()
    {
        const string target = "exact";
        if (Scenario != "S3") return new("Correctness", "S3 ledger exactness", target, "NOT MEASURED", "S3 only");
        if (S3OpeningEconomy == null || S3ClosingEconomy == null)
        {
            bool corrupt = (S3OpeningEconomy?.Issues ?? []).Concat(S3ClosingEconomy?.Issues ?? [])
                .Any(x => x.Kind == EconomyIssueKind.Corruption);
            return new("Correctness", "S3 ledger exactness", target, corrupt ? "FAIL" : "NOT MEASURED",
                "Opening or closing committed-state snapshot missing" + (corrupt ? "; available snapshot contains corruption" : ""));
        }
        S3EconomyCheck = EconomyExactnessChecker.Check(S3OpeningEconomy, S3ClosingEconomy, S3CommittedLedger);
        string[] required = ["exchange-list", "exchange-buy", "mail-claim", "npc-shop-buy", "tower-entry", "gather-start"];
        string[] absent = [.. required.Where(name => !Actions.TryGetValue(name, out var action) || action.Completed == 0)];
        bool complete = S3CompletedRun && RequestedBots >= 500 && RequestedSeconds >= 1800 &&
            UnsupportedBehaviours.Length == 0 && S3TowerEntries > 0 && S3GatherCompletions > 0 &&
            S3ClosingEconomy.Cut.CapturedUtc - S3OpeningEconomy.Cut.CapturedUtc >= TimeSpan.FromSeconds(RequestedSeconds * .95) &&
            absent.Length == 0 && required.All(name => Actions.TryGetValue(name, out var action) &&
                action.Offered == action.Completed + action.Refused + action.TimedOut + action.Failed &&
                action.TimedOut == 0 && action.Failed == 0);
        string detail = $"{S3EconomyCheck.Assets.Count} denominations; {S3EconomyCheck.Issues.Count} issues; " +
            $"{S3EconomyCheck.UncoveredMutationFamilies.Count} uncovered mutation families; " +
            "packet rewards are delivery diagnostics only" +
            (complete ? "" : $"; incomplete S3 workload (missing completed: {string.Join(", ", absent)})");
        string status = S3EconomyCheck.Verdict == EconomyVerdict.Fail ? "FAIL"
            : S3EconomyCheck.Verdict == EconomyVerdict.Pass && complete && S3UntrackedEconomySources.Length == 0 ? "PASS" : "NOT MEASURED";
        return new("Correctness", "S3 ledger exactness", target, status, detail);
    }

    public IEnumerable<string> S3MarkdownLines()
    {
        if (Scenario != "S3") yield break;
        yield return "";
        yield return "## S3 economy evidence";
        yield return "";
        yield return $"Opening: {S3OpeningEconomy?.Cut.CapturedUtc.ToString("u") ?? "NOT RUN"}; closing: {S3ClosingEconomy?.Cut.CapturedUtc.ToString("u") ?? "NOT RUN"}.";
        yield return "Scope: entire realm, including offline/deleted roles, system stock and recoverable event archives. Packet-derived changes are delivery diagnostics only.";
        yield return $"Tower entries: {S3TowerEntries}; successful tower settlements: {S3TowerSettlements}; completed gathering interactions: {S3GatherCompletions}.";
        if (S3SetupFixtures.Length > 0) yield return "Setup fixtures (outside measurement): " + string.Join(", ", S3SetupFixtures) + ".";
        if (S3UntrackedEconomySources.Length > 0) yield return "Untracked delivery observations: " + string.Join(", ", S3UntrackedEconomySources) + ".";
        yield return "";
        yield return "| Cut | Sequence | Holding rows | Decoded rows / blobs / bytes | Decode ms |";
        yield return "|---|---:|---:|---:|---:|---:|";
        foreach (var (name, snapshot) in new[] { ("Opening", S3OpeningEconomy), ("Closing", S3ClosingEconomy) })
            if (snapshot != null)
                yield return $"| {name} | {snapshot.Cut.CommitSequence?.ToString() ?? "unavailable (legacy)"} | {snapshot.Holdings.Count} | {snapshot.Metrics.Sum(x => x.Rows)} / {snapshot.Metrics.Sum(x => x.Blobs)} / {snapshot.Metrics.Sum(x => x.Bytes)} | {snapshot.DecodeMilliseconds:F3} |";
        if (S3EconomyCheck is not { } check) yield break;
        yield return "";
        yield return "Uncovered mutation families: " + string.Join(", ", check.UncoveredMutationFamilies) + ".";
        foreach (var issue in check.Issues) yield return $"- {issue.Kind}: `{issue.Key}` — {issue.Detail}";
        yield return "";
        yield return "| Denomination | Opening | Closing | Sources | Sinks | Residual | Real-owner leg residual |";
        yield return "|---|---:|---:|---:|---:|---:|---:|";
        foreach (var row in check.Assets)
            yield return $"| {row.Asset.Kind}/{row.Asset.Id}/{row.Asset.Epoch} | {row.Opening} | {row.Closing} | {row.Sources} | {row.Sinks} | {row.Residual} | {row.LegResidual} |";
    }
}

/// <summary>Shared committed-cut and receipt readers. No bot-ID filter or second accounting implementation.</summary>
public static class S3EconomyReader
{
    public static EconomySnapshot Read(string dataDir, EconomyCatalog catalog) =>
        EconomySnapshotReader.Read(Path.Combine(dataDir, "ro3emu.db"), catalog, "auto");

    public static EconomyLedgerWindow? ReadLedger(string dataDir, EconomyCatalog catalog,
        EconomySnapshot opening, EconomySnapshot closing) =>
        opening.Cut.CommitSequence is { } from && closing.Cut.CommitSequence is { } through
            ? EconomyLedgerReader.Read(Path.Combine(dataDir, "ro3emu.db"), catalog, closing.Cut.RealmId, from, through)
            : null;
}
