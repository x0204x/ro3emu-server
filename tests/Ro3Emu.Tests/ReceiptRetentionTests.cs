using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>
/// Receipt growth (issue from a host: 22 MB to 497 MB in two days): intent v2 no longer embeds the outcome, and
/// ReceiptRetention compacts old receipts without breaking replay, idempotency or the ledger audit.
/// </summary>
public sealed class ReceiptRetentionTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly long _role;
    private readonly ServerOptions _options;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public ReceiptRetentionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "receipt-retention-" + Guid.NewGuid().ToString("N"));
        _options = new ServerOptions { DataDir = _directory };
        _options.ReceiptRetention.Days = 1;
        _options.ReceiptRetention.BatchSize = 2;
        _accounts = new AccountStore(Options.Create(_options));
        _roles = new RoleStore(_accounts);
        _role = _roles.Create("retention-account", 1, "Ret" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private EconomyMutation Kill(string operation, long exp) => new(operation, "retention-account", _role, "monster-kill",
        "kill/roll/empty", "monster:" + operation, 1_000_000, [], [], Encoding.UTF8.GetBytes("{\"drops\":[],\"exp\":" + exp + "}"),
        new EconomyRewardEvidence(1, exp - 10, 1, exp, 1, 0, 1, 0, 10, 0));

    private RoleItemPlanBuilder KillTransaction(EconomyMutation mutation, long exp)
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        var progress = _roles.LoadProgress(_role);
        progress.Exp = exp;
        tx.Progress(_role, progress);
        tx.WithEconomy(mutation);
        return tx;
    }

    private string Commit(long exp)
    {
        string operation = Guid.NewGuid().ToString("N");
        _roles.Commit(KillTransaction(Kill(operation, exp), exp), "test/retention-kill");
        return operation;
    }

    private void Run(string sql, params (string Name, object? Value)[] values) => FixtureSql.Execute(_accounts, sql, values);

    private (byte[] Payload, byte[] Outcome) Blobs(string operation)
    {
        using var db = _accounts.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT intent_payload,outcome FROM operation_receipt WHERE operation_id=$op";
        command.Parameters.AddWithValue("$op", operation);
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        return ((byte[])row.GetValue(0), (byte[])row.GetValue(1));
    }

    private ReceiptRetention Retention() => new(Options.Create(_options), _accounts, TimeProvider.System,
        NullLogger<ReceiptRetention>.Instance);

    [Fact]
    public void IntentV2BindsTheOutcomeByHashInsteadOfEmbeddingIt()
    {
        var mutation = Kill("v2-intent", 10);
        string json = Encoding.UTF8.GetString(mutation.IntentPayload);
        Assert.Contains("\"OutcomeHash\"", json);
        Assert.DoesNotContain("\"Outcome\"", json);
        var legacy = mutation.LegacyIntent();
        Assert.Contains("\"Outcome\"", Encoding.UTF8.GetString(legacy.Payload));
        Assert.NotEqual(mutation.IntentHash, legacy.Hash);
    }

    [Fact]
    public async Task ACompactedKillStillReplaysAndTheLedgerCertifiesItsSequenceLegsAndSource()
    {
        string operation = Guid.NewGuid().ToString("N");
        var mutation = Kill(operation, 10);
        await _roles.CommitAsync(KillTransaction(mutation, 10), "test/retention-first");
        string recent = Commit(20);
        Run("UPDATE operation_receipt SET recorded_utc_ms=1 WHERE operation_id=$op", ("$op", operation));

        Assert.Equal(1, await Retention().CompactAsync());
        var (payload, outcome) = Blobs(operation);
        Assert.True(ReceiptBlobs.IsCompacted(payload));
        Assert.Empty(outcome);
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(recent).Payload)); // recent receipts keep their bytes

        // The same operation again is recognised as the committed one (by its hashes), not a conflict nor a second grant.
        await _roles.CommitAsync(KillTransaction(Kill(operation, 10), 10), "test/retention-replay");
        Assert.Equal(20, _roles.LoadProgress(_role).Exp);

        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Empty(ledger.Issues);
        Assert.Equal(1, ledger.CompactedReceipts);
        Assert.Equal(0, await Retention().CompactAsync()); // nothing left to do
    }

    [Fact]
    public async Task AReceiptWrittenWithTheV1IntentStillReplaysAndVerifies()
    {
        string operation = Guid.NewGuid().ToString("N");
        var mutation = Kill(operation, 10);
        await _roles.CommitAsync(KillTransaction(mutation, 10), "test/retention-v1");
        var (hash, payload) = mutation.LegacyIntent();
        Run("UPDATE operation_receipt SET intent_hash=$hash,intent_payload=$payload WHERE operation_id=$op",
            ("$hash", hash), ("$payload", payload), ("$op", operation));

        await _roles.CommitAsync(KillTransaction(Kill(operation, 10), 10), "test/retention-v1-replay");
        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.Empty(ledger.Issues);
        Assert.Equal(0, ledger.CompactedReceipts);
    }

    [Fact]
    public async Task MailDeliveriesAreNeverCompactedAndRetentionCanBeOff()
    {
        string mail = Commit(10), kill = Commit(20);
        Run("UPDATE operation_receipt SET recorded_utc_ms=1");
        // MailDeliveryReceiptReadPlan re-reads a delivery's payload whenever its delivery key comes back.
        Run("UPDATE operation_receipt SET command_kind='mail/deliver' WHERE operation_id=$op", ("$op", mail));

        _options.ReceiptRetention.Days = 0;
        Assert.Equal(0, await Retention().CompactAsync());
        _options.ReceiptRetention.Days = 1;
        Assert.Equal(1, await Retention().CompactAsync());
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(mail).Payload));
        Assert.True(ReceiptBlobs.IsCompacted(Blobs(kill).Payload));
    }

    [Fact]
    public void BlobComparisonAcceptsTheCurrentLegacyOrCompactedIntentOnly()
    {
        byte[] current = "{\"v\":2}"u8.ToArray(), legacy = "{\"v\":1}"u8.ToArray(), other = "{\"v\":3}"u8.ToArray();
        (string, byte[]) Legacy() => ("LEGACY", legacy);
        Assert.True(ReceiptBlobs.SameIntent("NEW", current, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameIntent("LEGACY", legacy, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameIntent("NEW", ReceiptBlobs.CompactedPayload, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameIntent("LEGACY", ReceiptBlobs.CompactedPayload, "NEW", current, Legacy));
        Assert.False(ReceiptBlobs.SameIntent("NEW", other, "NEW", current, Legacy));
        Assert.False(ReceiptBlobs.SameIntent("OTHER", ReceiptBlobs.CompactedPayload, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameOutcome([], ReceiptBlobs.CompactedPayload, [1, 2]));
        Assert.False(ReceiptBlobs.SameOutcome([], current, [1, 2]));
        Assert.True(ReceiptBlobs.SameOutcome([1, 2], current, [1, 2]));
    }
}
