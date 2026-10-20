using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>E05e adversarial accounting at the shared event boundary, independent of gameplay eligibility fixtures.</summary>
public sealed class EventEconomyTransactionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..")), ".loadtest-temp", "e05e-core-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly ItemStore items;
    private readonly long role, other;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public EventEconomyTransactionTests()
    {
        accounts = new(Options.Create(new ServerOptions { DataDir = directory }));
        roles = new(accounts); items = new(accounts);
        role = roles.Create("event-account", 1, "EventActor", 1, 1000, 1)!.Id;
        other = roles.Create("other-account", 1, "EventOther", 1, 1000, 1)!.Id;
        var progress = roles.LoadProgress(role); progress.Currency[1] = 100; roles.SaveProgress(role, progress);
    }

    private static EconomyOwner Owner(long role) => new(EconomyOwnerKind.Role, role.ToString(CultureInfo.InvariantCulture));

    private RoleItemPlanBuilder Draw(string operation, string source, long? recipient = null, long declaredCost = 10,
        long day = 1, int bindType = 0)
    {
        long target = recipient ?? role;
        var builder = roles.NewTransaction();
        var before = items.Load(target);
        var after = before.Select(i => i.Clone()).ToList();
        after.Add(new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = after.Count + 1, BindType = bindType });
        builder.ItemDelta(target, before, after);
        var progress = roles.LoadProgress(role); progress.Currency[1] -= 10; builder.Progress(role, progress);
        builder.RoleBlob(role, "lottery", JsonSerializer.SerializeToUtf8Bytes(new { Data = Array.Empty<byte>(), Day = day }));
        builder.WithEventEconomy(new(Catalog, operation, "event-account", role, "event/test-draw", source, 1_000_000,
            [new(new(EconomyAssetKind.Currency, 1), declaredCost)], [new(new(EconomyAssetKind.Item, 3), 2)],
            [new(Owner(role), new(EconomyAssetKind.Currency, 1), -declaredCost), new(Owner(role), new(EconomyAssetKind.Item, 3), 2)], [7, 8]));
        return builder;
    }

    [Fact]
    public async Task HeldCommitHasNoEarlyPaymentPityItemReceiptOrApply()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = async label => { reached.TrySetResult(); await release.Task; };
        bool applied = false;
        var commit = roles.CommitAsync(Draw("held", "draw/held"), "test/held", _ => applied = true);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(applied); Assert.False(commit.IsCompleted);
            Assert.Equal(100, roles.LoadProgress(role).Currency[1]); Assert.Empty(items.Load(role));
            Assert.Null(roles.LoadLottery(role)); Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, "held"));
        }
        finally { release.TrySetResult(); }
        await commit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(applied); Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
        Assert.Equal((3, 2), (Assert.Single(items.Load(role)).Entry, Assert.Single(items.Load(role)).Stack));
        Assert.NotNull(roles.LoadLottery(role));
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        Assert.Equal(2, ledger.Legs.Count);
        Assert.Contains(ledger.Legs, l => l.Reason == EconomyReason.ItemUse && l.Delta == -10 && l.Asset.Id == 1);
        Assert.Contains(ledger.Legs, l => l.Reason == EconomyReason.ActivityReward && l.Delta == 2 && l.Asset.Id == 3);
    }

    [Fact]
    public async Task LostAcknowledgementReplaysOriginalIdsWithoutAnotherCostAndRejectsChangedIntent()
    {
        var first = Draw("draw", "draw/source"); var replay = Draw("draw", "draw/source");
        var altered = Draw("draw", "draw/source", declaredCost: 11);
        accounts.FailNextEconomyAcknowledgementForTest();
        var result = await roles.CommitAsync(first, "test/draw");
        var again = await roles.CommitAsync(replay, "test/replay");
        Assert.Equal((long[])result[0]!, (long[])again[0]!);
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => roles.CommitAsync(altered, "test/altered"));
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => roles.CommitAsync(Draw("new-op", "draw/source"), "test/duplicate-source"));
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]); Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
        Assert.Single(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1).Receipts);
    }

    [Fact]
    public async Task SameTotalWrongRecipientAndUnexplainedOneUnitCostRollBack()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.CommitAsync(Draw("wrong", "draw/wrong", other), "test/wrong-owner"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.CommitAsync(Draw("amount", "draw/amount", declaredCost: 11), "test/wrong-amount"));
        Assert.Empty(items.Load(role)); Assert.Empty(items.Load(other)); Assert.Equal(100, roles.LoadProgress(role).Currency[1]);
        Assert.Null(roles.LoadLottery(role)); Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, "wrong"));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public async Task ReplayRejectsChangedChildStateAndItemMetadataBeforeApply(long day, int bindType)
    {
        var first = Draw("child", "draw/child");
        var altered = Draw("child", "draw/child", day: day, bindType: bindType);
        await roles.CommitAsync(first, "test/child");
        bool applied = false;
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            roles.CommitAsync(altered, "test/altered-child", _ => applied = true));
        Assert.False(applied);
        Assert.Equal(0, Assert.Single(items.Load(role)).BindType);
        using var blob = JsonDocument.Parse(roles.LoadLottery(role)!);
        Assert.Equal(1, blob.RootElement.GetProperty("Day").GetInt64());
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
    }

    [Theory]
    [InlineData("UPDATE operation_receipt SET realm_id='foreign'")]
    [InlineData("UPDATE operation_source SET realm_id='foreign'")]
    [InlineData("UPDATE operation_receipt SET leg_count=leg_count+1")]
    public async Task ReplayRefusesCorruptRealmSourceOrLedgerEvidence(string corrupt)
    {
        var first = Draw("realm", "draw/realm"); var replay = Draw("realm", "draw/realm");
        await roles.CommitAsync(first, "test/realm");
        FixtureSql.Execute(accounts, corrupt);
        bool applied = false;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            roles.CommitAsync(replay, "test/corrupt-replay", _ => applied = true));
        Assert.False(applied); Assert.Equal(90, roles.LoadProgress(role).Currency[1]);
    }

    [Fact]
    public async Task ExactFrozenChildrenReplayWithFreshGuardAfterUnrelatedRevisionAdvances()
    {
        RoleItemPlanBuilder Frozen()
        {
            var builder = roles.NewTransaction();
            builder.Guard(new RoleBlobGuardPlan(role, "settings",
                accounts.ReadSync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(role, "settings"))));
            builder.ItemDelta(role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
            var progress = roles.LoadProgress(role); progress.Currency[1] = 90;
            builder.Progress(role, progress);
            builder.WithEventEconomy(new(Catalog, "fresh-guard", "event-account", role, "event/test-draw",
                "draw/fresh-guard", 1_000_000,
                [new(new(EconomyAssetKind.Currency, 1), 10)], [new(new(EconomyAssetKind.Item, 3), 2)],
                [new(Owner(role), new(EconomyAssetKind.Currency, 1), -10), new(Owner(role), new(EconomyAssetKind.Item, 3), 2)], [7, 8]));
            return builder;
        }
        var first = await roles.CommitAsync(Frozen(), "test/frozen");
        var unrelated = roles.NewTransaction(); unrelated.RoleBlob(role, "training", []);
        await roles.CommitAsync(unrelated, "test/unrelated");
        var replay = await roles.CommitAsync(Frozen(), "test/recaptured");
        Assert.Equal((long[])first[0]!, (long[])replay[0]!);
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]); Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
    }

    [Fact]
    public async Task FinalLedgerFailureRollsBackResultSourceAndAssetsThenRetryUsesSameSource()
    {
        FixtureSql.Execute(accounts, "CREATE TRIGGER fail_e05e BEFORE INSERT ON economy_entry WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'event-last-leg'); END");
        await Assert.ThrowsAsync<SqliteException>(() => roles.CommitAsync(Draw("failure", "draw/failure"), "test/failure"));
        Assert.Equal(100, roles.LoadProgress(role).Currency[1]); Assert.Empty(items.Load(role));
        Assert.Null(roles.LoadLottery(role)); Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, "failure"));
        FixtureSql.Execute(accounts, "DROP TRIGGER fail_e05e");
        await roles.CommitAsync(Draw("retry", "draw/failure"), "test/retry");
        Assert.Equal(90, roles.LoadProgress(role).Currency[1]); Assert.Equal(2, Assert.Single(items.Load(role)).Stack);
    }

    [Fact]
    public async Task RestartSameNaturalSourceCannotGrantAgain()
    {
        await roles.CommitAsync(Draw("original", "draw/restart"), "test/first");
        accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
        var store = new RoleStore(reopened);
        var builder = store.NewTransaction(); var progress = store.LoadProgress(role);
        progress.Currency[1] += 1; builder.Progress(role, progress);
        builder.WithEventEconomy(new(Catalog, "after-restart", "event-account", role, "event/test-draw", "draw/restart", 2_000_000,
            [], [new(new(EconomyAssetKind.Currency, 1), 1)], [new(Owner(role), new(EconomyAssetKind.Currency, 1), 1)], [9]));
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => store.CommitAsync(builder, "test/restart"));
        Assert.Equal(90, store.LoadProgress(role).Currency[1]);
    }

    [Fact]
    public async Task OfflineReaderRejectsWrongBeneficiaryEvenWhenRealmTotalsAgree()
    {
        await roles.CommitAsync(Draw("proof", "draw/proof"), "test/proof");
        FixtureSql.Execute(accounts, "UPDATE economy_entry SET owner_id=$other WHERE asset_id=3", ("$other", other.ToString(CultureInfo.InvariantCulture)));
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(ledger.CompleteHistory);
        Assert.Contains(ledger.Issues, i => i.Key == "event/beneficiary/proof");
    }

    [Fact]
    public async Task IndependentSnapshotAndLedgerAgreeAtEveryOwnerAndItemLocation()
    {
        var opening = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto");
        await roles.CommitAsync(Draw("snapshot", "draw/snapshot"), "test/snapshot");
        var closing = EconomySnapshotReader.Read(accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(check.Locations, location => Assert.Equal("0", location.Residual));
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    public void Dispose()
    {
        accounts.Dispose();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".loadtest-temp"));
        if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture escaped local worktree.");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
