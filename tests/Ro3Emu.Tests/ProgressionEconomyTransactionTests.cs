using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class ProgressionEconomyTransactionTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });
    private static byte[] LifeBlob() => new DBLifeJobInfo { ActiveLifejob = { [1] = 3001 } }.ToByteArray();

    public ProgressionEconomyTransactionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "e05c-core-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _role = _roles.Create("progression-account", 1, "E05" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
    }

    private RoleItemPlanBuilder Progression(string operation, string kind = "cd13c/life-feed", long cost = 0,
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
        tx.RoleBlob(_role, "lifejob", LifeBlob());
        tx.WithEconomy(new EconomyMutation(operation, "progression-account", _role, kind, "progression:" + operation,
            "progression/" + operation, 1_000_000,
            cost == 0 ? [] : [new(new(EconomyAssetKind.Currency, 1), cost)],
            goods == 0 ? [] : [new(new(EconomyAssetKind.Item, 3), goods)], [7]));
        return tx;
    }

    [Fact]
    public async Task ZeroLegStateOnlyCommitHasReceiptAndReplays()
    {
        string operation = Guid.NewGuid().ToString("N");
        var first = Progression(operation);
        var duplicate = Progression(operation);
        long cut = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        await _roles.CommitAsync(first, "test/progression-neutral");
        var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, operation)!;
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(new[] { "inventory", "lifejob" },
            _roles.ReadRevisionVector(_role).Parts.Keys.Where(x => x is "inventory" or "lifejob").Order().ToArray());
        await _roles.CommitAsync(duplicate, "test/progression-neutral-replay");
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
            _roles.CommitAsync(Progression(rejected, cost: 10, goods: 2, applyCost: false), "test/progression-mismatch"));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, rejected));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        string accepted = Guid.NewGuid().ToString("N");
        long cut = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        await _roles.CommitAsync(Progression(accepted, cost: 10, goods: 2), "test/progression-cost-goods");
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
        tx.RoleBlob(_role, "lifejob", LifeBlob());
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(new EconomyMutation(operation, "progression-account", _role, "cd13c/life-neutral",
            operation, "progression/" + operation, 1_000_000, [], [], [8]));
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        long cut = opening.Cut.CommitSequence!.Value;
        await _roles.CommitAsync(tx, "test/progression-relocation");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(4, ledger.Legs.Count);
        Assert.All(ledger.Legs, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
        Assert.Equal(2, ledger.Legs.Select(x => x.EdgeNumber).Distinct().Count());
        Assert.Equal(2, _items.Load(_role).Sum(x => x.Stack));
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
    }

    [Fact]
    public async Task SameOperationCannotReplaceFrozenPlanEvenWithSameTotals()
    {
        string op = Guid.NewGuid().ToString("N");
        var first = Progression(op);
        var changed = Progression(op);
        // A second child changes the exact state even though no denomination moved.
        changed.RoleBlob(_role, "life_runtime", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        { EnergyDay = 1, EnergySpent = 0, PotionDay = 1, FoodBook = 101 }));
        await _roles.CommitAsync(first, "test/progression-original");
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => _roles.CommitAsync(changed, "test/progression-altered"));
        Assert.Null(_roles.LoadLifeRuntime(_role));
    }

    [Fact]
    public async Task ForeignBlobCannotUseProgressionEnvelope()
    {
        string op = Guid.NewGuid().ToString("N");
        var tx = Progression(op);
        tx.RoleBlob(_role, "pets", new DBPetInfo().ToByteArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(tx, "test/progression-foreign"));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
    }

    [Fact]
    public async Task LostAcknowledgementResolvesReceiptAndDuplicateSourceCannotPayOrGrantAgain()
    {
        string op = Guid.NewGuid().ToString("N");
        var accepted = Progression(op, cost: 10, goods: 2);
        var replay = Progression(op, cost: 10, goods: 2);
        _accounts.FailNextEconomyAcknowledgementForTest();
        await _roles.CommitAsync(accepted, "test/progression-lost-ack");
        await _roles.CommitAsync(replay, "test/progression-replay");
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
        Assert.Equal(1, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
    }

    [Fact]
    public async Task LostAcknowledgementWithCorruptChildReceiptFailsClosed()
    {
        string op = Guid.NewGuid().ToString("N");
        _accounts.FailNextEconomyAcknowledgementForTest();
        _accounts.BeforeEconomyResolveForTest = _ =>
        {
            FixtureSql.Execute(_accounts, "UPDATE operation_receipt SET child_receipts=$c WHERE operation_id=$o",
                ("$c", EconomyTransactionPlan.EncodeChildReceipts(new object?[] { Array.Empty<long>() })), ("$o", op));
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<EconomyOutcomeUnknownException>(() => _roles.CommitAsync(Progression(op), "test/progression-corrupt"));
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
