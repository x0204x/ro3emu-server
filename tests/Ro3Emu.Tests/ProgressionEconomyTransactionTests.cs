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
    private readonly string directory;
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly ItemStore items;
    private readonly long role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });
    private static byte[] LifeBlob() => new DBLifeJobInfo { ActiveLifejob = { [1] = 3001 } }.ToByteArray();

    public ProgressionEconomyTransactionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        directory = Path.Combine(root, ".loadtest-temp", "e05c-core-" + Guid.NewGuid().ToString("N"));
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new RoleStore(accounts);
        items = new ItemStore(accounts);
        role = roles.Create("progression-account", 1, "E05" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = roles.LoadProgress(role);
        progress.Currency[1] = 100;
        roles.SaveProgress(role, progress);
    }

    private RoleItemPlanBuilder Progression(string operation, string kind = "cd13c/life-feed", long cost = 0,
        long goods = 0, bool applyCost = true, bool applyGoods = true, InvItem? replacement = null)
    {
        var tx = roles.NewTransaction();
        var before = items.Load(role);
        var after = replacement is null ? before.Select(x => x.Clone()).ToList() : new List<InvItem> { replacement };
        if (applyGoods && goods > 0) after.Add(new InvItem { Entry = 3, Stack = checked((int)goods), Pack = 1 });
        tx.ItemDelta(role, before, after);
        if (cost > 0)
        {
            var progress = roles.LoadProgress(role);
            if (applyCost) progress.Currency[1] -= cost;
            tx.Progress(role, progress);
        }
        tx.RoleBlob(role, "lifejob", LifeBlob());
        tx.WithEconomy(new EconomyMutation(operation, "progression-account", role, kind, "progression:" + operation,
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
        long cut = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        await roles.CommitAsync(first, "test/progression-neutral");
        var receipt = EconomyReceiptReader.Read(accounts.DatabasePath, operation)!;
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(new[] { "inventory", "lifejob" },
            roles.ReadRevisionVector(role).Parts.Keys.Where(x => x is "inventory" or "lifejob").Order().ToArray());
        await roles.CommitAsync(duplicate, "test/progression-neutral-replay");
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Empty(ledger.Legs);
        Assert.Single(ledger.Receipts);
        Assert.Equal(cut + 1, EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
    }

    [Fact]
    public async Task CostAndGoodsMustMatchCommittedHoldingsAndReaderSeesExactLegs()
    {
        string rejected = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roles.CommitAsync(Progression(rejected, cost: 10, goods: 2, applyCost: false), "test/progression-mismatch"));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, rejected));
        Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
        string accepted = Guid.NewGuid().ToString("N");
        long cut = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        await roles.CommitAsync(Progression(accepted, cost: 10, goods: 2), "test/progression-cost-goods");
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(new long[] { -10, 2 }, ledger.Legs.Select(x => x.Delta).Order().ToArray());
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
    }

    [Fact]
    public async Task MetadataAndInventoryRelocationUseNeutralExactLegs()
    {
        var original = new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 };
        items.Insert(role, original);
        var moved = original.Clone();
        moved.Stack = 1;
        moved.Pack = 2;
        moved.Attrs = new Dictionary<int, long> { [30] = 4 };
        var extra = new InvItem { Entry = 3, Stack = 1, Pack = 1, Sort = 2 };
        var tx = roles.NewTransaction();
        tx.ItemDelta(role, [original], [moved, extra]);
        tx.RoleBlob(role, "lifejob", LifeBlob());
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(new EconomyMutation(operation, "progression-account", role, "cd13c/life-neutral",
            operation, "progression/" + operation, 1_000_000, [], [], [8]));
        var opening = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto");
        long cut = opening.Cut.CommitSequence!.Value;
        await roles.CommitAsync(tx, "test/progression-relocation");
        var closing = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(4, ledger.Legs.Count);
        Assert.All(ledger.Legs, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
        Assert.Equal(2, ledger.Legs.Select(x => x.EdgeNumber).Distinct().Count());
        Assert.Equal(2, items.Load(role).Sum(x => x.Stack));
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
        changed.RoleBlob(role, "life_runtime", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        { EnergyDay = 1, EnergySpent = 0, PotionDay = 1, FoodBook = 101 }));
        await roles.CommitAsync(first, "test/progression-original");
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => roles.CommitAsync(changed, "test/progression-altered"));
        Assert.Null(roles.LoadLifeRuntime(role));
    }

    [Fact]
    public async Task ForeignBlobCannotUseProgressionEnvelope()
    {
        string op = Guid.NewGuid().ToString("N");
        var tx = Progression(op);
        tx.RoleBlob(role, "pets", new DBPetInfo().ToByteArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.CommitAsync(tx, "test/progression-foreign"));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, op));
    }

    [Fact]
    public async Task LostAcknowledgementResolvesReceiptAndDuplicateSourceCannotPayOrGrantAgain()
    {
        string op = Guid.NewGuid().ToString("N");
        var accepted = Progression(op, cost: 10, goods: 2);
        var replay = Progression(op, cost: 10, goods: 2);
        accounts.FailNextEconomyAcknowledgementForTest();
        await roles.CommitAsync(accepted, "test/progression-lost-ack");
        await roles.CommitAsync(replay, "test/progression-replay");
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
        Assert.Equal(1, EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence);
        Assert.NotNull(EconomyReceiptReader.Read(accounts.DatabasePath, op));
    }

    [Fact]
    public async Task LostAcknowledgementWithCorruptChildReceiptFailsClosed()
    {
        string op = Guid.NewGuid().ToString("N");
        accounts.FailNextEconomyAcknowledgementForTest();
        accounts.BeforeEconomyResolveForTest = _ =>
        {
            FixtureSql.Execute(accounts, "UPDATE operation_receipt SET child_receipts=$c WHERE operation_id=$o",
                ("$c", EconomyTransactionPlan.EncodeChildReceipts(new object?[] { Array.Empty<long>() })), ("$o", op));
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<EconomyOutcomeUnknownException>(() => roles.CommitAsync(Progression(op), "test/progression-corrupt"));
        Assert.NotNull(EconomyReceiptReader.Read(accounts.DatabasePath, op));
    }

    public void Dispose()
    {
        accounts.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
