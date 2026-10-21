using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class EconomyTransactionTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public EconomyTransactionTests()
    {
        // Exact, unique worktree-local fixture path. Never touch a shared data/client junction.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "e01-core-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _role = _roles.Create("e01-account", 1, "E01" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
    }

    private RoleItemPlanBuilder Purchase(string operation, string source, string fingerprint = "purchase:3:2:10",
        long cost = 10, long goods = 2, bool applyCost = true, bool applyGoods = true,
        string actorAccount = "e01-account", byte[]? outcome = null, string commandKind = "npc-shop-buy")
    {
        var tx = _roles.NewTransaction();
        var before = _items.Load(_role);
        var after = before.Select(x => x.Clone()).ToList();
        if (applyGoods) after.Add(new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 });
        tx.ItemDelta(_role, before, after);
        var progress = _roles.LoadProgress(_role);
        if (applyCost) progress.Currency[1] -= 10;
        tx.Progress(_role, progress);
        tx.RoleBlob(_role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState
        {
            Bought = new() { [118] = new ShopService.Counter { Count = 1, Period = "" } }
        }));
        tx.WithEconomy(new EconomyMutation(operation, actorAccount, _role, commandKind, fingerprint,
            source, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), cost)],
            [new(new(EconomyAssetKind.Item, 3), goods)], outcome ?? [7, 8, 9]));
        return tx;
    }

    [Fact]
    public async Task NpcEconomyEnvelopeSupportsAsyncCommitAndSyncReplayWithoutDuplicateMovement()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var tx = Purchase(op, source);
        var replay = Purchase(op, source);
        long opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        int ownedCommit = 0, ownedApply = 0, writerCommit = 0, published = 0;
        _accounts.BeforeOwnedCommitForTest = _ =>
        {
            ownedCommit++;
            Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
            Assert.Empty(_items.Load(_role));
            Assert.Null(_roles.LoadShop(_role));
            Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
            return Task.CompletedTask;
        };
        _accounts.BeforeOwnedApplyForTest = _ =>
        {
            ownedApply++;
            Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
            Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
            Assert.NotNull(_roles.LoadShop(_role));
            Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
            Assert.Equal(0, published);
            return Task.CompletedTask;
        };
        _accounts.BeforeCommitForTest = _ => writerCommit++;

        object?[] first = await _roles.CommitAsync(tx, "test/e01-async", _ => published++);
        Assert.Equal((1, 1, 1, 1), (ownedCommit, ownedApply, writerCommit, published));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(tx, "test/e01-resubmit"));
        var committedRevisions = _roles.ReadRevisionVector(_role);
        _accounts.BeforeCommitForTest = null;
        object?[] duplicate = _roles.Commit(replay, "test/e01-sync-replay");
        Assert.Equal((long[])first[0]!, (long[])duplicate[0]!);
        Assert.Equal(committedRevisions.Parts.OrderBy(p => p.Key), _roles.ReadRevisionVector(_role).Parts.OrderBy(p => p.Key));
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
        var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, op)!;
        Assert.NotNull(receipt);
        Assert.Equal(source, receipt.SourceKey);
        Assert.Equal((long[])first[0]!, receipt.ChildReceipts[0].GeneratedIds!.ToArray());
        Assert.Equal(opening + 1, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
    }

    [Fact]
    public async Task UnconvertedEconomyEnvelopeRejectsAsyncCommitBeforeSubmission()
    {
        string op = Guid.NewGuid().ToString("N"), source = "unsupported:" + Guid.NewGuid().ToString("N");
        var tx = Purchase(op, source, commandKind: "unconverted-command");
        long opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        int ownedCommit = 0, ownedApply = 0, writerCommit = 0, published = 0;
        _accounts.BeforeOwnedCommitForTest = _ => { ownedCommit++; return Task.CompletedTask; };
        _accounts.BeforeOwnedApplyForTest = _ => { ownedApply++; return Task.CompletedTask; };
        _accounts.BeforeCommitForTest = _ => writerCommit++;
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _roles.CommitAsync(tx, "test/unconverted-async-reject", _ => published++));
        Assert.Equal((0, 0, 0, 0), (ownedCommit, ownedApply, writerCommit, published));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        Assert.Empty(_items.Load(_role));
        Assert.Null(_roles.LoadShop(_role));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
        Assert.Equal(opening, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
    }

    [Fact]
    public void PurchaseReplayUsesOriginalIdsAndPreservesCAndDRevisions()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var first = Purchase(op, source);
        var duplicate = Purchase(op, source); // same captured old revisions, deliberately committed later
        var beforeRevision = _roles.ReadRevisionVector(_role);
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        object?[] firstResults = _roles.Commit(first, "test/e01-first");
        var committedRevision = _roles.ReadRevisionVector(_role);
        object?[] replayResults = _roles.Commit(duplicate, "test/e01-replay");
        var replayRevision = _roles.ReadRevisionVector(_role);
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);

        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
        Assert.Equal((long[])firstResults[0]!, (long[])replayResults[0]!);
        Assert.Equal(committedRevision.Epoch, replayRevision.Epoch);
        Assert.Equal(committedRevision.Parts.OrderBy(x => x.Key), replayRevision.Parts.OrderBy(x => x.Key));
        Assert.Equal(beforeRevision.Revision("inventory") + 1, committedRevision.Revision("inventory"));
        Assert.Equal(beforeRevision.Revision("progress") + 1, committedRevision.Revision("progress"));
        Assert.Equal(beforeRevision.Revision("shop") + 1, committedRevision.Revision("shop"));
        Assert.Equal(opening.Cut.CommitSequence + 1, closing.Cut.CommitSequence);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(2, ledger.Legs.Count);
        Assert.Equal(new long[] { -10, 2 }, ledger.Legs.Select(x => x.Delta).Order().ToArray());
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(check.Locations, location => Assert.Equal("0", location.Residual));
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
        Assert.Equal((long[])firstResults[0]!, EconomyReceiptReader.Read(_accounts.DatabasePath, op)!.ChildReceipts[0].GeneratedIds!.ToArray());
    }

    [Fact]
    public void ChangedIntentOrActorAndReusedSourceConflictBeforeStaleRevision()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var differentPayload = Purchase(op, source, "different-intent");
        var changedCostSameFingerprint = Purchase(op, source, cost: 11);
        var changedOutcomeSameFingerprint = Purchase(op, source, outcome: [1, 2, 3]);
        var differentSource = Purchase(Guid.NewGuid().ToString("N"), source);
        var wrongActor = Purchase(op, source, actorAccount: "wrong-account");
        _roles.Commit(Purchase(op, source), "test/e01-original");
        Assert.Throws<EconomyOperationConflictException>(() => _roles.Commit(differentPayload, "test/e01-payload"));
        Assert.Throws<EconomyOperationConflictException>(() => _roles.Commit(changedCostSameFingerprint, "test/e01-cost"));
        Assert.Throws<EconomyOperationConflictException>(() => _roles.Commit(changedOutcomeSameFingerprint, "test/e01-outcome"));
        Assert.Throws<EconomyOperationConflictException>(() => _roles.Commit(differentSource, "test/e01-source"));
        Assert.Throws<EconomyOperationConflictException>(() => _roles.Commit(wrongActor, "test/e01-actor"));
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
    }

    [Fact]
    public void LastLedgerInsertFailureRollsBackAssetsQuotaReceiptSourceAndSequence()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        long cut = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        FixtureSql.Execute(_accounts, "CREATE TRIGGER reject_last_e01_leg BEFORE INSERT ON economy_entry WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'last-leg'); END");
        Assert.Throws<SqliteException>(() => _roles.Commit(Purchase(op, source), "test/e01-last-leg"));
        FixtureSql.Execute(_accounts, "DROP TRIGGER reject_last_e01_leg");
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        Assert.Empty(_items.Load(_role));
        Assert.Null(_roles.LoadShop(_role));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
        Assert.Equal(cut, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        _roles.Commit(Purchase(op, source), "test/e01-retry-after-rollback");
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
    }

    [Fact]
    public void WriterRejectsCostGoodsThatDisagreeWithCommittedAssetDelta()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => _roles.Commit(Purchase(op, source, cost: 11), "test/e01-mismatch"));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        Assert.Empty(_items.Load(_role));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
    }

    [Fact]
    public void LostAcknowledgementAfterCommitResolvesFromReceiptWithoutSecondExecution()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var first = Purchase(op, source);
        var retry = Purchase(op, source);
        long before = _roles.ReadRevisionVector(_role).Revision("inventory");
        _accounts.FailNextEconomyAcknowledgementForTest();
        object?[] committedResult = _roles.Commit(first, "test/e01-lost-ack");
        var committed = EconomyReceiptReader.Read(_accounts.DatabasePath, op)!;
        Assert.NotNull(committed);
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(before + 1, _roles.ReadRevisionVector(_role).Revision("inventory"));
        Assert.Equal((long[])committedResult[0]!, committed.ChildReceipts[0].GeneratedIds!.ToArray());
        var replay = _roles.Commit(retry, "test/e01-resolve");
        Assert.Equal(committed.ChildReceipts[0].GeneratedIds!.ToArray(), (long[])replay[0]!);
        Assert.Equal(before + 1, _roles.ReadRevisionVector(_role).Revision("inventory"));
        Assert.Equal(2, EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1).Legs.Count);
    }

    [Fact]
    public void ExplicitSameAssetCostAndGoodsRetainBothLegsWhenNetBalanceIsZero()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        tx.Progress(_role, _roles.LoadProgress(_role));
        tx.RoleBlob(_role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        tx.WithEconomy(new EconomyMutation(op, "e01-account", _role, "npc-shop-buy", "currency-exchange",
            source, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Currency, 1), 10)], [1]));
        _roles.Commit(tx, "test/e01-net-zero");
        var legs = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1).Legs;
        Assert.Equal(new long[] { -10, 10 }, legs.Select(x => x.Delta).ToArray());
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
    }

    [Fact]
    public void ReplayAfterStoreReopenReadsDurableOriginalGeneratedIds()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var retry = Purchase(op, source);
        object?[] first = _roles.Commit(Purchase(op, source), "test/e01-before-reopen");
        string realm = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.RealmId;
        _accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        var reopenedRoles = new RoleStore(reopened);
        _ = new ItemStore(reopened);
        object?[] replay = reopenedRoles.Commit(retry, "test/e01-after-reopen");
        Assert.Equal((long[])first[0]!, (long[])replay[0]!);
        Assert.Equal(realm, EconomySnapshotReader.Read(reopened.DatabasePath, Catalog, "auto").Cut.RealmId);
        Assert.Equal(1, EconomySnapshotReader.Read(reopened.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        Assert.True(EconomyLedgerReader.Read(reopened.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);
    }

    [Fact]
    public async Task ConcurrentIdenticalOperationHasOneCommitAndOneSourceClaim()
    {
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var left = Purchase(op, source);
        var right = Purchase(op, source);
        var results = await Task.WhenAll(Task.Run(() => _roles.Commit(left, "test/e01-left")),
            Task.Run(() => _roles.Commit(right, "test/e01-right")));
        Assert.Equal((long[])results[0][0]!, (long[])results[1][0]!);
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(1, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory);
        Assert.Single(ledger.Receipts);
        Assert.Equal(2, ledger.Legs.Count);
    }

    [Fact]
    public void RestackingCannotSmuggleBindingChangeThroughQuantityLeg()
    {
        var owned = new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 };
        _items.Insert(_role, owned);
        string op = Guid.NewGuid().ToString("N"), source = "npc:" + Guid.NewGuid().ToString("N");
        var tx = _roles.NewTransaction();
        var next = owned.Clone();
        next.Stack = 3;
        next.BindType = 1;
        tx.ItemDelta(_role, [owned], [next]);
        tx.Progress(_role, _roles.LoadProgress(_role));
        tx.RoleBlob(_role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        tx.WithEconomy(new EconomyMutation(op, "e01-account", _role, "npc-shop-buy", "restack",
            source, 1_000_000, [], [new(new(EconomyAssetKind.Item, 3), 1)], [1]));
        Assert.Throws<InvalidOperationException>(() => _roles.Commit(tx, "test/e01-rebind"));
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
        Assert.Equal(0, Assert.Single(_items.Load(_role)).BindType);
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
    }

    [Theory]
    [InlineData("prior-leg")]
    [InlineData("receipt-gap")]
    [InlineData("command")]
    [InlineData("owner")]
    [InlineData("revision-role")]
    [InlineData("intent")]
    [InlineData("child-type")]
    [InlineData("watermark")]
    public void PersistedReaderRejectsCorruptedReceiptManifestIncludingHistoryBeforeOpening(string damage)
    {
        _roles.Commit(Purchase("first", "source-first"), "test/reader-first");
        _roles.Commit(Purchase("second", "source-second"), "test/reader-second");
        Assert.True(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 1, 2).CompleteHistory);
        switch (damage)
        {
            case "prior-leg": FixtureSql.Execute(_accounts, "DELETE FROM economy_entry WHERE commit_seq=1 AND leg_no=1"); break;
            case "receipt-gap": FixtureSql.Execute(_accounts, """
                DELETE FROM economy_entry WHERE commit_seq=1;
                DELETE FROM operation_source WHERE operation_id='first';
                DELETE FROM operation_receipt WHERE commit_seq=1
                """); break;
            case "command": FixtureSql.Execute(_accounts, "UPDATE operation_receipt SET command_kind='gm-mint' WHERE commit_seq=1"); break;
            case "owner": FixtureSql.Execute(_accounts, "UPDATE economy_entry SET owner_id='999999' WHERE commit_seq=1"); break;
            case "revision-role":
                var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, "first")!;
                FixtureSql.Execute(_accounts, "UPDATE operation_receipt SET before_revisions=$before,after_revisions=$after WHERE commit_seq=1",
                    ("$before", receipt.BeforeRevisions.Replace("\"roleId\":" + _role, "\"roleId\":999999", StringComparison.Ordinal)),
                    ("$after", receipt.AfterRevisions.Replace("\"roleId\":" + _role, "\"roleId\":999999", StringComparison.Ordinal)));
                break;
            case "intent": FixtureSql.Execute(_accounts, "UPDATE operation_receipt SET intent_payload=$bad WHERE commit_seq=1", ("$bad", new byte[] { 123, 125 })); break;
            case "child-type":
                using (var bytes = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(3);
                        for (int i = 0; i < 3; i++) { writer.Write((byte)1); writer.Write(1); }
                    }
                    FixtureSql.Execute(_accounts, "UPDATE operation_receipt SET child_receipts=$bad WHERE commit_seq=1", ("$bad", bytes.ToArray()));
                }
                break;
            case "watermark": FixtureSql.Execute(_accounts, "UPDATE economy_meta SET next_sequence=1"); break;
        }
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", damage == "watermark" ? 0 : 1,
            damage == "watermark" ? 1 : 2);
        Assert.False(ledger.CompleteHistory);
        Assert.Contains(ledger.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".loadtest-temp"));
        if (!Path.GetFullPath(_directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture directory escaped the worktree temp root.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
