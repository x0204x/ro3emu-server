using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class FamilyEconomyTransactionTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });
    private static byte[] PetBlob() => new DBPetInfo { CurrentPlan = 1 }.ToByteArray();

    public FamilyEconomyTransactionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "e05b-core-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _role = _roles.Create("family-account", 1, "E05" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
    }

    private RoleItemPlanBuilder Family(string operation, string kind = "cd13b/pet-feed", long cost = 0,
        long goods = 0, bool applyCost = true, bool applyGoods = true, InvItem? replacement = null)
    {
        var tx = _roles.NewTransaction();
        var before = _items.Load(_role);
        var after = replacement is null ? before.Select(x => x.Clone()).ToList() : new List<InvItem> { replacement };
        if (applyGoods && goods > 0) after.Add(new InvItem { Entry = 3, Stack = checked((int)goods), Pack = 1 });
        tx.ItemDelta(_role, before, after);
        if (cost > 0)
        {
            var progress = _roles.LoadProgress(_role);
            if (applyCost) progress.Currency[1] -= cost;
            tx.Progress(_role, progress);
        }
        tx.RoleBlob(_role, "pets", PetBlob());
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, kind, "family:" + operation,
            "family/" + operation, 1_000_000,
            cost == 0 ? [] : [new(new(EconomyAssetKind.Currency, 1), cost)],
            goods == 0 ? [] : [new(new(EconomyAssetKind.Item, 3), goods)], [7]));
        return tx;
    }

    [Fact]
    public async Task ZeroLegStateOnlyCommitHasReceiptAndReplays()
    {
        string operation = Guid.NewGuid().ToString("N");
        var first = Family(operation);
        var duplicate = Family(operation);
        long cut = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        await _roles.CommitAsync(first, "test/family-neutral");
        var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, operation)!;
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(new[] { "inventory", "pets" },
            _roles.ReadRevisionVector(_role).Parts.Keys.Where(x => x is "inventory" or "pets").Order().ToArray());
        await _roles.CommitAsync(duplicate, "test/family-neutral-replay");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Empty(ledger.Legs);
        Assert.Single(ledger.Receipts);
        Assert.Equal(cut + 1, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
    }

    [Fact]
    public async Task CostAndGoodsMustMatchCommittedHoldingsAndReaderSeesExactLegs()
    {
        string rejected = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _roles.CommitAsync(Family(rejected, cost: 10, goods: 2, applyCost: false), "test/family-mismatch"));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, rejected));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        string accepted = Guid.NewGuid().ToString("N");
        long cut = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        await _roles.CommitAsync(Family(accepted, cost: 10, goods: 2), "test/family-cost-goods");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(new long[] { -10, 2 }, ledger.Legs.Select(x => x.Delta).Order().ToArray());
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
    }

    [Fact]
    public async Task MetadataAndInventoryRelocationUseNeutralExactLegs()
    {
        var original = new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 };
        _items.Insert(_role, original);
        var moved = original.Clone();
        moved.Stack = 1;
        moved.Pack = 2;
        moved.Attrs = new Dictionary<int, long> { [30] = 4 };
        var extra = new InvItem { Entry = 3, Stack = 1, Pack = 1, Sort = 2 };
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [original], [moved, extra]);
        tx.RoleBlob(_role, "pets", PetBlob());
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, "cd13b/pet-neutral",
            operation, "family/" + operation, 1_000_000, [], [], [8]));
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        long cut = opening.Cut.CommitSequence!.Value;
        await _roles.CommitAsync(tx, "test/family-relocation");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(4, ledger.Legs.Count);
        Assert.All(ledger.Legs, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
        Assert.Equal(2, ledger.Legs.Select(x => x.EdgeNumber).Distinct().Count());
        Assert.Contains(ledger.Legs.Where(x => x.Delta < 0), debit =>
            ledger.Legs.Any(credit => credit.EdgeNumber == debit.EdgeNumber && credit.Delta == -debit.Delta &&
                debit.BeforeFingerprint != credit.AfterFingerprint));
        Assert.Equal(2, _items.Load(_role).Sum(x => x.Stack));
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
    }

    [Fact]
    public async Task FamilyIntentRetainsMoreThanThirtyTwoDistinctDenominations()
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        var progress = _roles.LoadProgress(_role);
        for (int id = 2; id <= 41; id++) progress.Currency[id] = 1;
        tx.Progress(_role, progress);
        tx.RoleBlob(_role, "pets", PetBlob());
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, "cd13b/pet-lottery",
            operation, "family/" + operation, 1_000_000, [],
            Enumerable.Range(2, 40).Select(id => new EconomyQuantity(new(EconomyAssetKind.Currency, id), 1)).ToArray(),
            [9]));
        await _roles.CommitAsync(tx, "test/family-many-denominations");
        var catalog = new EconomyCatalog(Enumerable.Range(1, 41)
            .ToDictionary(id => (long)id, _ => EconomyAssetKind.Currency));
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(40, ledger.Legs.Count);
        Assert.All(ledger.Legs, leg => Assert.Equal(EconomyReason.PetGoods, leg.Reason));
    }

    [Fact]
    public async Task SameDenominationCostAndGoodRemainTwoSemanticLegs()
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        tx.RoleBlob(_role, "pets", PetBlob());
        string operation = Guid.NewGuid().ToString("N");
        var quantity = new EconomyQuantity(new(EconomyAssetKind.Currency, 1), 10);
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, "cd13b/pet-rebirth",
            operation, "family/" + operation, 1_000_000, [quantity], [quantity], [1]));
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        await _roles.CommitAsync(tx, "test/family-overlap");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(new[] { EconomyLegKind.Sink, EconomyLegKind.Source }, ledger.Legs.Select(x => x.Kind));
        Assert.Equal(new[] { EconomyReason.PetCost, EconomyReason.PetRefund }, ledger.Legs.Select(x => x.Reason));
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset => Assert.Equal("0", asset.Residual));
    }

    [Fact]
    public async Task TotemUpgradeOverflowUsesRefundReason()
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1 }]);
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] -= 10;
        tx.Progress(_role, progress);
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, "cd13b/totem-upgrade",
            operation, "family/" + operation, 1_000_000,
            [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Item, 3), 2)], [1]));
        await _roles.CommitAsync(tx, "test/totem-overflow-reason");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(2, ledger.Legs.Count);
        var payment = Assert.Single(ledger.Legs, leg => leg.Kind == EconomyLegKind.Sink);
        Assert.Equal((EconomyReason.TotemCost, EconomyAssetKind.Currency, 1L, -10L),
            (payment.Reason, payment.Asset.Kind, payment.Asset.Id, payment.Delta));
        var refund = Assert.Single(ledger.Legs, leg => leg.Kind == EconomyLegKind.Source);
        Assert.Equal((EconomyReason.TotemRefund, EconomyAssetKind.Item, 3L, 2L),
            (refund.Reason, refund.Asset.Kind, refund.Asset.Id, refund.Delta));
    }

    [Fact]
    public async Task SameAssetCostAndGoodDoNotDoubleCountRelocation()
    {
        var original = new InvItem { Entry = 3, Stack = 1, Pack = 1 };
        _items.Insert(_role, original);
        var moved = original.Clone();
        moved.Pack = 2;
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [original], [moved]);
        tx.RoleBlob(_role, "pets", PetBlob());
        string operation = Guid.NewGuid().ToString("N");
        var quantity = new EconomyQuantity(new(EconomyAssetKind.Item, 3), 1);
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, "cd13b/pet-combine",
            operation, "family/" + operation, 1_000_000, [quantity], [quantity], [1]));
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        await _roles.CommitAsync(tx, "test/family-overlap-relocation");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(2, ledger.Legs.Count);
        Assert.All(ledger.Legs, leg => Assert.NotEqual(EconomyLegKind.Transfer, leg.Kind));
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
    }

    [Fact]
    public async Task LostAcknowledgementResolvesAndFailedResolutionQuarantinesOutcome()
    {
        string resolved = Guid.NewGuid().ToString("N");
        _accounts.FailNextEconomyAcknowledgementForTest();
        await _roles.CommitAsync(Family(resolved), "test/family-lost-ack");
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, resolved));
        string unresolved = Guid.NewGuid().ToString("N");
        _accounts.FailNextEconomyAcknowledgementForTest();
        _accounts.FailNextEconomyResolutionForTest();
        await Assert.ThrowsAsync<EconomyOutcomeUnknownException>(() =>
            _roles.CommitAsync(Family(unresolved), "test/family-resolution-fault"));
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, unresolved));
    }

    [Fact]
    public void SynchronousFamilyLostAcknowledgementResolvesCompleteChildReceipt()
    {
        string operation = Guid.NewGuid().ToString("N");
        _accounts.FailNextEconomyAcknowledgementForTest();
        _roles.Commit(Family(operation), "test/family-sync-lost-ack");
        Assert.Equal(2, EconomyTransactionPlan.DecodeChildReceipts(ReadChildReceipt(operation)).Length);
    }

    private byte[] ReadChildReceipt(string operation)
    {
        using var db = _accounts.Open();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT child_receipts FROM operation_receipt WHERE operation_id=$op";
        read.Parameters.AddWithValue("$op", operation);
        return (byte[])read.ExecuteScalar()!;
    }

    [Fact]
    public async Task LostAcknowledgementRejectsStructurallyValidButTruncatedChildReceipt()
    {
        string operation = Guid.NewGuid().ToString("N");
        _accounts.FailNextEconomyAcknowledgementForTest();
        _accounts.BeforeEconomyResolveForTest = committed =>
        {
            Assert.Equal(operation, committed);
            FixtureSql.Execute(_accounts,
                "UPDATE operation_receipt SET child_receipts=$children WHERE operation_id=$op",
                ("$children", EconomyTransactionPlan.EncodeChildReceipts(new object?[] { Array.Empty<long>() })),
                ("$op", operation));
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<EconomyOutcomeUnknownException>(() =>
            _roles.CommitAsync(Family(operation), "test/family-truncated-lost-ack"));
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, operation));
        Assert.Single(EconomyTransactionPlan.DecodeChildReceipts(ReadChildReceipt(operation)));
    }

    [Fact]
    public async Task CorruptFamilyReplayReceiptFailsClosed()
    {
        string operation = Guid.NewGuid().ToString("N");
        var duplicate = Family(operation);
        await _roles.CommitAsync(Family(operation), "test/family-original");
        FixtureSql.Execute(_accounts,
            "UPDATE operation_receipt SET child_receipts=X'00' WHERE operation_id=$op", ("$op", operation));
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(ledger.CompleteHistory);
        Assert.Contains(ledger.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        await Assert.ThrowsAsync<EconomyOutcomeUnknownException>(() =>
            _roles.CommitAsync(duplicate, "test/family-corrupt-replay"));
    }

    [Fact]
    public void PopulatedE01SchemaMigratesWithoutPruningReceiptSourceOrLeg()
    {
        // Recreate the exact E01 leg-count constraint before populating its receipt. The production
        // EconomySchemaPlan must rebuild this table in place when the database reopens.
        FixtureSql.Execute(_accounts, """
            CREATE TABLE operation_receipt_old (
                operation_id TEXT PRIMARY KEY, realm_id TEXT NOT NULL, commit_seq INTEGER NOT NULL UNIQUE,
                actor_account TEXT NOT NULL, actor_role INTEGER NOT NULL, command_kind TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL, source_key TEXT NOT NULL UNIQUE,
                recorded_utc_ms INTEGER NOT NULL, effective_utc_ms INTEGER NOT NULL,
                outcome BLOB NOT NULL, child_receipts BLOB NOT NULL, before_revisions TEXT NOT NULL,
                after_revisions TEXT NOT NULL, leg_count INTEGER NOT NULL CHECK(leg_count>0),
                intent_hash TEXT NOT NULL DEFAULT '', intent_payload BLOB NOT NULL DEFAULT X''
            );
            INSERT INTO operation_receipt_old(operation_id,realm_id,commit_seq,actor_account,actor_role,
                command_kind,request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,
                effective_utc_ms,outcome,child_receipts,before_revisions,after_revisions,leg_count)
            SELECT operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,
                outcome,child_receipts,before_revisions,after_revisions,leg_count FROM operation_receipt;
            DROP TABLE operation_receipt;
            ALTER TABLE operation_receipt_old RENAME TO operation_receipt;
            """);
        string operation = Guid.NewGuid().ToString("N");
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1 }]);
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] -= 10;
        tx.Progress(_role, progress);
        tx.RoleBlob(_role, "shop", [1]);
        tx.WithEconomy(new EconomyMutation(operation, "family-account", _role, "npc-shop-buy", operation,
            "npc/" + operation, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Item, 3), 2)], [7]));
        _roles.Commit(tx, "test/old-e01-purchase");
        var original = EconomyReceiptReader.Read(_accounts.DatabasePath, operation)!;
        _accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _ = new RoleStore(reopened);
        _ = new ItemStore(reopened);
        var migrated = EconomyReceiptReader.Read(reopened.DatabasePath, operation)!;
        Assert.Equal(original.IntentHash, migrated.IntentHash);
        Assert.Equal(original.CommitSequence, migrated.CommitSequence);
        Assert.Equal(original.LegCount, migrated.LegCount);
        using var db = reopened.Open();
        using var check = db.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        using var row = check.ExecuteReader();
        Assert.False(row.Read());
        var ledger = EconomyLedgerReader.Read(reopened.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(2, ledger.Legs.Count);
        // The migrated table accepts a true zero-leg family state-only receipt.
        var secondRoles = new RoleStore(reopened);
        var secondItems = new ItemStore(reopened);
        var neutral = secondRoles.NewTransaction();
        neutral.ItemDelta(_role, secondItems.Load(_role), secondItems.Load(_role));
        neutral.RoleBlob(_role, "pets", PetBlob());
        string familyOperation = Guid.NewGuid().ToString("N");
        neutral.WithEconomy(new EconomyMutation(familyOperation, "family-account", _role, "cd13b/pet-neutral",
            familyOperation, "family/" + familyOperation, 1_000_001, [], [], [8]));
        secondRoles.Commit(neutral, "test/migrated-zero-leg");
        Assert.Equal(0, EconomyReceiptReader.Read(reopened.DatabasePath, familyOperation)!.LegCount);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".loadtest-temp"));
        if (!Path.GetFullPath(_directory).StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped temp root.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
