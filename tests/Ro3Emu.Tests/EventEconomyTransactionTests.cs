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
    private readonly string _directory = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..")), ".loadtest-temp", "e05e-core-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role, _other;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public EventEconomyTransactionTests()
    {
        _accounts = new(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new(_accounts); _items = new(_accounts);
        _role = _roles.Create("event-account", 1, "EventActor", 1, 1000, 1)!.Id;
        _other = _roles.Create("other-account", 1, "EventOther", 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role); progress.Currency[1] = 100; _roles.SaveProgress(_role, progress);
    }

    private static EconomyOwner Owner(long role) => new(EconomyOwnerKind.Role, role.ToString(CultureInfo.InvariantCulture));

    private RoleItemPlanBuilder Draw(string operation, string source, long? recipient = null, long declaredCost = 10,
        long day = 1, int bindType = 0)
    {
        long target = recipient ?? _role;
        var builder = _roles.NewTransaction();
        var before = _items.Load(target);
        var after = before.Select(i => i.Clone()).ToList();
        after.Add(new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = after.Count + 1, BindType = bindType });
        builder.ItemDelta(target, before, after);
        var progress = _roles.LoadProgress(_role); progress.Currency[1] -= 10; builder.Progress(_role, progress);
        builder.RoleBlob(_role, "lottery", JsonSerializer.SerializeToUtf8Bytes(new { Data = Array.Empty<byte>(), Day = day }));
        builder.WithEventEconomy(new(Catalog, operation, "event-account", _role, "event/test-draw", source, 1_000_000,
            [new(new(EconomyAssetKind.Currency, 1), declaredCost)], [new(new(EconomyAssetKind.Item, 3), 2)],
            [new(Owner(_role), new(EconomyAssetKind.Currency, 1), -declaredCost), new(Owner(_role), new(EconomyAssetKind.Item, 3), 2)], [7, 8]));
        return builder;
    }

    [Fact]
    public async Task HeldCommitHasNoEarlyPaymentPityItemReceiptOrApply()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _accounts.BeforeOwnedCommitForTest = async label => { reached.TrySetResult(); await release.Task; };
        bool applied = false;
        var commit = _roles.CommitAsync(Draw("held", "draw/held"), "test/held", _ => applied = true);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(applied); Assert.False(commit.IsCompleted);
            Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]); Assert.Empty(_items.Load(_role));
            Assert.Null(_roles.LoadLottery(_role)); Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, "held"));
        }
        finally { release.TrySetResult(); }
        await commit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(applied); Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal((3, 2), (Assert.Single(_items.Load(_role)).Entry, Assert.Single(_items.Load(_role)).Stack));
        Assert.NotNull(_roles.LoadLottery(_role));
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
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
        _accounts.FailNextEconomyAcknowledgementForTest();
        var result = await _roles.CommitAsync(first, "test/draw");
        var again = await _roles.CommitAsync(replay, "test/replay");
        Assert.Equal((long[])result[0]!, (long[])again[0]!);
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => _roles.CommitAsync(altered, "test/altered"));
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => _roles.CommitAsync(Draw("new-op", "draw/source"), "test/duplicate-source"));
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]); Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
        Assert.Single(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1).Receipts);
    }

    [Fact]
    public async Task SameTotalWrongRecipientAndUnexplainedOneUnitCostRollBack()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(Draw("wrong", "draw/wrong", _other), "test/wrong-owner"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(Draw("amount", "draw/amount", declaredCost: 11), "test/wrong-amount"));
        Assert.Empty(_items.Load(_role)); Assert.Empty(_items.Load(_other)); Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        Assert.Null(_roles.LoadLottery(_role)); Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, "wrong"));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public async Task ReplayRejectsChangedChildStateAndItemMetadataBeforeApply(long day, int bindType)
    {
        var first = Draw("child", "draw/child");
        var altered = Draw("child", "draw/child", day: day, bindType: bindType);
        await _roles.CommitAsync(first, "test/child");
        bool applied = false;
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            _roles.CommitAsync(altered, "test/altered-child", _ => applied = true));
        Assert.False(applied);
        Assert.Equal(0, Assert.Single(_items.Load(_role)).BindType);
        using var blob = JsonDocument.Parse(_roles.LoadLottery(_role)!);
        Assert.Equal(1, blob.RootElement.GetProperty("Day").GetInt64());
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
    }

    [Theory]
    [InlineData("UPDATE operation_receipt SET realm_id='foreign'")]
    [InlineData("UPDATE operation_source SET realm_id='foreign'")]
    [InlineData("UPDATE operation_receipt SET leg_count=leg_count+1")]
    public async Task ReplayRefusesCorruptRealmSourceOrLedgerEvidence(string corrupt)
    {
        var first = Draw("realm", "draw/realm"); var replay = Draw("realm", "draw/realm");
        await _roles.CommitAsync(first, "test/realm");
        FixtureSql.Execute(_accounts, corrupt);
        bool applied = false;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _roles.CommitAsync(replay, "test/corrupt-replay", _ => applied = true));
        Assert.False(applied); Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
    }

    [Fact]
    public async Task ExactFrozenChildrenReplayWithFreshGuardAfterUnrelatedRevisionAdvances()
    {
        RoleItemPlanBuilder Frozen()
        {
            var builder = _roles.NewTransaction();
            builder.Guard(new RoleBlobGuardPlan(_role, "settings",
                _accounts.ReadSync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(_role, "settings"))));
            builder.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
            var progress = _roles.LoadProgress(_role); progress.Currency[1] = 90;
            builder.Progress(_role, progress);
            builder.WithEventEconomy(new(Catalog, "fresh-guard", "event-account", _role, "event/test-draw",
                "draw/fresh-guard", 1_000_000,
                [new(new(EconomyAssetKind.Currency, 1), 10)], [new(new(EconomyAssetKind.Item, 3), 2)],
                [new(Owner(_role), new(EconomyAssetKind.Currency, 1), -10), new(Owner(_role), new(EconomyAssetKind.Item, 3), 2)], [7, 8]));
            return builder;
        }
        var first = await _roles.CommitAsync(Frozen(), "test/frozen");
        var unrelated = _roles.NewTransaction(); unrelated.RoleBlob(_role, "training", []);
        await _roles.CommitAsync(unrelated, "test/unrelated");
        var replay = await _roles.CommitAsync(Frozen(), "test/recaptured");
        Assert.Equal((long[])first[0]!, (long[])replay[0]!);
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]); Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
    }

    [Fact]
    public async Task FinalLedgerFailureRollsBackResultSourceAndAssetsThenRetryUsesSameSource()
    {
        FixtureSql.Execute(_accounts, "CREATE TRIGGER fail_e05e BEFORE INSERT ON economy_entry WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'event-last-leg'); END");
        await Assert.ThrowsAsync<SqliteException>(() => _roles.CommitAsync(Draw("failure", "draw/failure"), "test/failure"));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]); Assert.Empty(_items.Load(_role));
        Assert.Null(_roles.LoadLottery(_role)); Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, "failure"));
        FixtureSql.Execute(_accounts, "DROP TRIGGER fail_e05e");
        await _roles.CommitAsync(Draw("retry", "draw/failure"), "test/retry");
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]); Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
    }

    [Fact]
    public async Task RestartSameNaturalSourceCannotGrantAgain()
    {
        await _roles.CommitAsync(Draw("original", "draw/restart"), "test/first");
        _accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        var store = new RoleStore(reopened);
        var builder = store.NewTransaction(); var progress = store.LoadProgress(_role);
        progress.Currency[1] += 1; builder.Progress(_role, progress);
        builder.WithEventEconomy(new(Catalog, "after-restart", "event-account", _role, "event/test-draw", "draw/restart", 2_000_000,
            [], [new(new(EconomyAssetKind.Currency, 1), 1)], [new(Owner(_role), new(EconomyAssetKind.Currency, 1), 1)], [9]));
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() => store.CommitAsync(builder, "test/restart"));
        Assert.Equal(90, store.LoadProgress(_role).Currency[1]);
    }

    [Fact]
    public async Task OfflineReaderRejectsWrongBeneficiaryEvenWhenRealmTotalsAgree()
    {
        await _roles.CommitAsync(Draw("proof", "draw/proof"), "test/proof");
        FixtureSql.Execute(_accounts, "UPDATE economy_entry SET owner_id=$other WHERE asset_id=3", ("$other", _other.ToString(CultureInfo.InvariantCulture)));
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(ledger.CompleteHistory);
        Assert.Contains(ledger.Issues, i => i.Key == "event/beneficiary/proof");
    }

    [Fact]
    public async Task IndependentSnapshotAndLedgerAgreeAtEveryOwnerAndItemLocation()
    {
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        await _roles.CommitAsync(Draw("snapshot", "draw/snapshot"), "test/snapshot");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(check.Locations, location => Assert.Equal("0", location.Residual));
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".loadtest-temp"));
        if (!Path.GetFullPath(_directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture escaped local worktree.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
