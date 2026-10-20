using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

/// <summary>E01 offline evidence only. Readers pin SQLite/WAL transactions; no repair or checkpoint is issued.</summary>
public static class EconomyProbeCli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            if (args[0] is not ("snapshot" or "compare" or "verify-receipts" or "restore-check"))
                return LegacyProbe(args);
            string command = args[0];
            int required = command == "snapshot" ? 5 : command == "verify-receipts" ? 7 : 6;
            if (args.Length != required) return Usage();
            string database = Path.GetFullPath(args[1]);
            var catalog = EconomyCatalog.Load(args[2]);
            var snapshot = EconomySnapshotReader.Read(database, catalog, args[3]);
            var binding = new ToolBinding(typeof(EconomySnapshotReader).Assembly.GetName().Version?.ToString() ?? "unknown",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EconomySnapshotReader).Assembly.Location))));
            if (command == "snapshot")
            {
                var issues = snapshot.Issues.ToList();
                if (snapshot.Cut.CommitSequence == null)
                    issues.Add(new(EconomyIssueKind.Coverage, "cut/watermark", "No durable economy watermark."));
                var verdict = Verdict(issues, snapshot.UncoveredMutationFamilies.Count != 0);
                Write(args[4], new SnapshotArtifact(database, binding, snapshot),
                    Summary(command, verdict, snapshot.Cut, issues, snapshot.UncoveredMutationFamilies));
                return Exit(verdict);
            }
            if (command == "verify-receipts")
            {
                if (!long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out long from) ||
                    !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out long through) || through < from)
                    return Usage();
                var ledger = EconomyLedgerReader.Read(database, catalog, snapshot.Cut.RealmId, from, through);
                // Receipt consistency alone never certifies opening + ledger = closing or realm coverage.
                var verdict = Verdict(ledger.Issues, !ledger.CompleteHistory || ledger.UncoveredMutationFamilies.Count != 0);
                Write(args[6], new
                {
                    Command = command, Database = database, Binding = binding, Ledger = ledger,
                    Verdict = verdict.ToString(), Scope = "Receipt and retained ledger consistency; no balance comparison."
                },
                    Summary(command, verdict, snapshot.Cut, ledger.Issues, ledger.UncoveredMutationFamilies) +
                    $"\nReceipts: {ledger.Receipts.Count}; legs: {ledger.Legs.Count}; range: ({from}, {through}].\n");
                return Exit(verdict);
            }
            var opening = JsonSerializer.Deserialize<SnapshotArtifact>(File.ReadAllBytes(args[4]),
                new JsonSerializerOptions { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true })?.Snapshot
                ?? throw new InvalidDataException("Opening input is not an EconomyProbe snapshot artifact.");
            EconomyLedgerWindow? window = opening.Cut.CommitSequence is { } start && snapshot.Cut.CommitSequence is { } end
                ? EconomyLedgerReader.Read(database, catalog, snapshot.Cut.RealmId, start, end) : null;
            if (command == "restore-check")
                snapshot = snapshot with { Issues = snapshot.Issues.Concat(Integrity(database)).ToArray() };
            var check = EconomyExactnessChecker.Check(opening, snapshot, window);
            var text = new StringBuilder(Summary(command, check.Verdict, snapshot.Cut, check.Issues, check.UncoveredMutationFamilies));
            text.AppendLine().AppendLine("| Asset | Opening | Closing | Sources | Sinks | Residual | Leg residual |")
                .AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var row in check.Assets)
                text.AppendLine($"| {row.Asset.Kind}/{row.Asset.Id}/{row.Asset.Epoch} | {row.Opening} | {row.Closing} | {row.Sources} | {row.Sinks} | {row.Residual} | {row.LegResidual} |");
            if (command == "restore-check") text.AppendLine("\nE01 checks retained receipts, database integrity and these cuts. Archive/backup lifecycle qualification belongs to E06.");
            Write(args[5], new
            {
                Command = command, Database = database, Binding = binding, Opening = opening,
                Closing = snapshot, Ledger = window, Check = check
            }, text.ToString());
            return Exit(check.Verdict);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            SqliteException or ArgumentException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"EconomyProbe: {error.Message}");
            return 1;
        }
    }

    private static IReadOnlyList<EconomyIssue> Integrity(string database)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA query_only=ON"; cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA integrity_check";
        using var rows = cmd.ExecuteReader();
        var issues = new List<EconomyIssue>();
        while (rows.Read())
            if (rows.GetString(0) != "ok") issues.Add(new(EconomyIssueKind.Corruption, "sqlite/integrity", rows.GetString(0)));
        return issues;
    }

    private static EconomyVerdict Verdict(IEnumerable<EconomyIssue> issues, bool incomplete) =>
        issues.Any(x => x.Kind == EconomyIssueKind.Corruption) ? EconomyVerdict.Fail :
        incomplete || issues.Any() ? EconomyVerdict.NotMeasured : EconomyVerdict.Pass;
    private static int Exit(EconomyVerdict verdict) => verdict switch { EconomyVerdict.Pass => 0, EconomyVerdict.Fail => 1, _ => 2 };

    private static string Summary(string command, EconomyVerdict verdict, EconomyCut cut, IEnumerable<EconomyIssue> issues,
        IEnumerable<string> uncovered)
    {
        var text = new StringBuilder($"# EconomyProbe {command}\n\nVerdict: **{verdict}**.\n\nRealm: `{cut.RealmId}`; sequence: {cut.CommitSequence?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}.\n\nCatalog: `{cut.CatalogVersion}`; coverage: `{cut.CoverageVersion}`.\n");
        text.AppendLine("\nUncovered mutation families:");
        foreach (string family in uncovered) text.AppendLine("- " + family);
        text.AppendLine("\nIssues:");
        foreach (var issue in issues) text.AppendLine($"- {issue.Kind}: `{issue.Key}` — {issue.Detail}");
        return text.ToString();
    }

    private static void Write(string prefix, object artifact, string markdown)
    {
        // CreateNew protects evidence and makes accidental input/output aliasing fail without overwriting input.
        string jsonPath = Path.GetFullPath(prefix + ".json"), markdownPath = Path.GetFullPath(prefix + ".md");
        if (File.Exists(jsonPath) || File.Exists(markdownPath)) throw new IOException("Output artifacts already exist; use a new prefix.");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, artifact, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(markdownPath, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream)) writer.Write(markdown);
        Console.WriteLine($"JSON: {jsonPath}\nMarkdown: {markdownPath}");
    }

    private static int LegacyProbe(string[] args)
    {
        if (args.Length is < 3 or > 4 || (args.Length == 4 && !int.TryParse(args[3], out _))) return Usage();
        int repeats = args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;
        if (repeats is < 1 or > 100) return Usage();
        var timer = Stopwatch.StartNew();
        var catalog = EconomyCatalog.Load(args[1]);
        double catalogMs = timer.Elapsed.TotalMilliseconds;
        EconomySnapshot? snapshot = null;
        var timings = new List<double>();
        for (int n = 0; n < repeats; n++)
        {
            snapshot = EconomySnapshotReader.Read(args[0], catalog, args[2]);
            timings.Add(snapshot.DecodeMilliseconds);
        }
        var result = EconomyExactnessChecker.Check(snapshot!, snapshot!, null);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Database = Path.GetFullPath(args[0]), DatabaseBytes = new FileInfo(args[0]).Length,
            WalBytes = File.Exists(args[0] + "-wal") ? new FileInfo(args[0] + "-wal").Length : 0,
            EconomySnapshotReader.FormatManifestHash, CatalogMilliseconds = catalogMs, snapshot!.Cut,
            HoldingRows = snapshot.Holdings.Count, AssetDenominations = snapshot.Holdings.Select(x => x.Asset).Distinct().Count(),
            snapshot.Metrics, DecodeMilliseconds = timings, snapshot.Issues, snapshot.UncoveredMutationFamilies,
            Verdict = result.Verdict.ToString(), CheckIssues = result.Issues
        }, new JsonSerializerOptions { WriteIndented = true }));
        return Exit(result.Verdict);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              EconomyProbe snapshot DATABASE GAME_DATA_DIR REALM_ID|auto OUTPUT_PREFIX
              EconomyProbe compare DATABASE GAME_DATA_DIR REALM_ID|auto OPENING_JSON OUTPUT_PREFIX
              EconomyProbe verify-receipts DATABASE GAME_DATA_DIR REALM_ID|auto FROM_EXCLUSIVE THROUGH_INCLUSIVE OUTPUT_PREFIX
              EconomyProbe restore-check DATABASE GAME_DATA_DIR REALM_ID|auto OPENING_JSON OUTPUT_PREFIX
              EconomyProbe DATABASE GAME_DATA_DIR REALM_ID|auto [REPETITIONS]
            Outputs: new OUTPUT_PREFIX.json and .md files. Database/WAL inputs are read-only.
            Exit 0: covered exactness; 1: corruption/error; 2: incomplete coverage; 64: usage error.
            E01 realm coverage remains incomplete. Equal totals alone cannot certify the realm.
            """);
        return 64;
    }

    private sealed record ToolBinding(string Version, string ServerAssemblySha256);
    private sealed record SnapshotArtifact(string Database, ToolBinding Binding, EconomySnapshot Snapshot);
}
