using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>Explicit E01 paired microprobe, excluded from fast gates. No latency assertion or load server.</summary>
public sealed class EconomyMeasurementTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "Slow")]
    public void PairedTypedPurchaseMeasuresAddedWriterWorkAndLedgerBytes()
    {
        string root = RepoRoot();
        string artifact = Path.Combine(root, ".loadtest", "e01", "probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifact);
        using var plain = new Probe(Path.Combine(artifact, "control"), false);
        using var covered = new Probe(Path.Combine(artifact, "ledger"), true);
        const int warmup = 20, operations = 200;
        for (int i = 0; i < warmup; i++) { plain.Buy(); covered.Buy(); }
        var plainBefore = plain.State(); var coveredBefore = covered.State();
        var plainElapsed = new List<double>(); var coveredElapsed = new List<double>();
        var plainWriter = new List<double>(); var coveredWriter = new List<double>();
        for (int i = 0; i < operations; i++)
        {
            // Alternate which profile runs first to reduce a fixed warm-cache/order bias.
            if (i % 2 == 0) { Measure(plain, plainElapsed, plainWriter); Measure(covered, coveredElapsed, coveredWriter); }
            else { Measure(covered, coveredElapsed, coveredWriter); Measure(plain, plainElapsed, plainWriter); }
        }
        var plainAfter = plain.State(); var coveredAfter = covered.State();
        Assert.Equal(operations, plainAfter.Transactions - plainBefore.Transactions);
        Assert.Equal(operations, coveredAfter.Transactions - coveredBefore.Transactions);
        Assert.Equal(operations, coveredAfter.Receipts - coveredBefore.Receipts);
        Assert.Equal(2 * operations, coveredAfter.Legs - coveredBefore.Legs);
        Assert.Equal(0, plainAfter.Receipts);
        Assert.Equal(0, plainAfter.Legs);
        var report = new
        {
            Kind = "E01 paired typed purchase microprobe", WarmupPerProfile = warmup, OperationsPerProfile = operations,
            Runtime = Environment.Version.ToString(), CpuCount = Environment.ProcessorCount,
            ControlBefore = plainBefore, ControlAfter = plainAfter, LedgerBefore = coveredBefore, LedgerAfter = coveredAfter,
            ControlWriterMs = Summary(plainWriter), LedgerWriterMs = Summary(coveredWriter),
            AddedMeanWriterMs = coveredWriter.Average() - plainWriter.Average(),
            ControlCommitCallMs = Summary(plainElapsed), LedgerCommitCallMs = Summary(coveredElapsed),
            ReceiptAndLedgerPayloadBytesPerOperation = (coveredAfter.PayloadBytes - coveredBefore.PayloadBytes) / (double)operations,
            AddedAllocatedDatabaseBytesPerOperation = ((coveredAfter.AllocatedBytes - coveredBefore.AllocatedBytes) -
                (plainAfter.AllocatedBytes - plainBefore.AllocatedBytes)) / (double)operations,
            Notes = "Writer duration is dequeue-to-commit, excluding queue and staging. Payload counts UTF-8 TEXT/BLOB and 8 bytes per INTEGER, not SQLite physical encodings. Allocated bytes are page_count*page_size; WAL bytes are separate. Shared-host observation, not S3/load qualification."
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(artifact, "measurement.json"), json);
        output.WriteLine(artifact); output.WriteLine(json);
    }

    private static void Measure(Probe probe, List<double> call, List<double> writer)
    {
        double before = probe.WriterMs();
        call.Add(probe.Buy());
        writer.Add(probe.WriterMs() - before);
    }
    private static object Summary(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return new
        {
            Count = values.Count, Mean = values.Average(), Median = sorted[sorted.Length / 2],
            P95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], Max = sorted[^1]
        };
    }

    private sealed record ProbeState(long Transactions, long Receipts, long Legs, long PayloadBytes,
        long AllocatedBytes, long WalBytes, string JournalMode, long Synchronous);
    private sealed class Probe : IDisposable
    {
        private readonly AccountStore _accounts;
        private readonly RoleStore _roles;
        private readonly ItemStore _items;
        private readonly string _directory;
        private readonly bool _ledger;
        private readonly long _role;
        private int _purchases;
        internal Probe(string directory, bool ledger)
        {
            this._directory = directory; this._ledger = ledger;
            _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
            _roles = new RoleStore(_accounts); _items = new ItemStore(_accounts);
            _role = _roles.Create("probe", 1, "Probe", 1, 1000, 1)!.Id;
            var progress = _roles.LoadProgress(_role); progress.Currency[1] = 1_000_000;
            _roles.SaveProgress(_role, progress);
            _items.Insert(_role, new InvItem { Entry = 1000000, Stack = 20, Pack = 1, Sort = 1 });
        }
        internal double WriterMs() => JsonSerializer.SerializeToElement(_accounts.AccessMetrics.Snapshot())
            .GetProperty("writerExecutionMs").GetDouble();
        internal double Buy()
        {
            int next = ++_purchases;
            var progress = _roles.LoadProgress(_role); progress.Currency[1] -= 100;
            var before = _items.Load(_role); var after = before.Select(i => i.Clone()).ToList(); after[0].Stack++;
            var builder = _roles.NewTransaction();
            builder.ItemDelta(_role, before, after); builder.Progress(_role, progress);
            builder.RoleBlob(_role, "shop", Encoding.UTF8.GetBytes($"{{\"Bought\":{{\"118\":{{\"Count\":{next},\"Period\":\"\"}}}},\"Favorites\":[]}}"));
            if (_ledger)
            {
                string op = Guid.NewGuid().ToString("N");
                builder.WithEconomy(new EconomyMutation(op, "probe", _role, "npc-shop-buy", "118/1/100", "npc-shop/" + op,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [new(new(EconomyAssetKind.Currency, 1), 100)],
                    [new(new(EconomyAssetKind.Item, 1000000), 1)], Encoding.UTF8.GetBytes($"{{\"BuyTimes\":{next}}}")));
            }
            long started = Stopwatch.GetTimestamp();
            _roles.Commit(builder, "test/e01-control");
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        internal ProbeState State()
        {
            using var db = _accounts.Open();
            long Scalar(string sql)
            {
                using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar());
            }
            long bytes = 0;
            foreach (string table in new[] { "operation_receipt", "operation_source", "economy_entry" })
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table;
                using var rows = command.ExecuteReader();
                while (rows.Read()) for (int i = 0; i < rows.FieldCount; i++)
                    bytes += rows.GetValue(i) switch
                    {
                        string value => Encoding.UTF8.GetByteCount(value),
                        byte[] value => value.Length,
                        long => 8,
                        DBNull => 0,
                        _ => throw new InvalidDataException("Unexpected probe storage type.")
                    };
            }
            using var mode = db.CreateCommand(); mode.CommandText = "PRAGMA journal_mode";
            string wal = Path.Combine(_directory, "ro3emu.db-wal");
            return new(JsonSerializer.SerializeToElement(_accounts.AccessMetrics.Snapshot()).GetProperty("writerCompleted").GetInt64(),
                Scalar("SELECT count(*) FROM operation_receipt"), Scalar("SELECT count(*) FROM economy_entry"), bytes,
                Scalar("PRAGMA page_count") * Scalar("PRAGMA page_size"), File.Exists(wal) ? new FileInfo(wal).Length : 0,
                (string)mode.ExecuteScalar()!, Scalar("PRAGMA synchronous"));
        }
        public void Dispose() => _accounts.Dispose();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
