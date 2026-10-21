using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>All four envelope families must coexist in one retained receipt prefix after the merge.</summary>
public sealed class MergedEconomyEnvelopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..")), ".loadtest-temp", "merged-economy-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public MergedEconomyEnvelopeTests()
    {
        _accounts = new(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new(_accounts);
        _items = new(_accounts);
        _role = _roles.Create("merged-account", 1, "MergedActor", 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourFamiliesRetainOneOutcomeEachAcrossReplayAndLostAcknowledgements(bool loseAcknowledgement)
    {
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        string[] kinds = ["npc-shop-buy", "cd13b/pet-feed", "monster-kill", "event/test-draw"];
        foreach (string kind in kinds)
        {
            var first = Plan(kind);
            var replay = Plan(kind); // frozen before the original, including identical event child values
            if (loseAcknowledgement) _accounts.FailNextEconomyAcknowledgementForTest();
            if (kind == "npc-shop-buy")
            {
                // 13a gave NPC purchases asynchronous owner admission (covered by EconomyTransactionTests); this
                // test keeps the synchronous commit and replay of the E01 receipt.
                _roles.Commit(first, "test/merged");
                _roles.Commit(replay, "test/merged-replay");
            }
            else
            {
                var receipt = await _roles.CommitAsync(first, "test/merged");
                var repeated = await _roles.CommitAsync(replay, "test/merged-replay");
                Assert.Equal((long[])receipt[0]!, (long[])repeated[0]!);
            }
        }

        Assert.Equal(84, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
        Assert.Equal(5, _items.Load(_role).Sum(item => item.Stack));
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 4);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        Assert.Equal(4, closing.Cut.CommitSequence);
        Assert.Equal(kinds, ledger.Receipts.Select(receipt => receipt.CommandKind));
        Assert.Equal(4, ledger.Receipts.SelectMany(receipt => receipt.SourceKeys!).Distinct().Count());
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
    }

    private RoleItemPlanBuilder Plan(string kind)
    {
        var tx = _roles.NewTransaction();
        bool reward = kind == "monster-kill", family = kind.StartsWith("cd13b/"), isEvent = kind.StartsWith("event/");
        int cost = reward ? 0 : family ? 3 : 10;
        int itemCount = reward ? 0 : family ? 1 : 2;
        var before = _items.Load(_role);
        var after = before.Select(item => item.Clone()).ToList();
        if (itemCount != 0) after.Add(new InvItem { Entry = 3, Stack = itemCount, Pack = 1, Sort = after.Count + 1 });
        tx.ItemDelta(_role, before, after);
        var progress = _roles.LoadProgress(_role);
        long beforeExp = progress.Exp;
        progress.Currency[1] += reward ? 7 : -cost;
        if (reward) progress.Exp += 10;
        tx.Progress(_role, progress);
        if (family) tx.RoleBlob(_role, "pets", new DBPetInfo { CurrentPlan = 1 }.ToByteArray());
        else if (isEvent) tx.RoleBlob(_role, "lottery", JsonSerializer.SerializeToUtf8Bytes(new { Data = Array.Empty<byte>(), Day = 1L }));
        else if (!reward) tx.RoleBlob(_role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState
        {
            Bought = new() { [118] = new ShopService.Counter { Count = 1, Period = "" } }
        }));
        EconomyQuantity[] costs = cost == 0 ? [] : [new(new(EconomyAssetKind.Currency, 1), cost)];
        EconomyQuantity[] goods = reward ? [new(new(EconomyAssetKind.Currency, 1), 7)] : [new(new(EconomyAssetKind.Item, 3), itemCount)];
        if (isEvent)
        {
            var owner = new EconomyOwner(EconomyOwnerKind.Role, _role.ToString(CultureInfo.InvariantCulture));
            tx.WithEventEconomy(new(Catalog, kind, "merged-account", _role, kind, "merged/" + kind, 1_000_000,
                costs, goods, [new(owner, costs[0].Asset, -cost), new(owner, goods[0].Asset, itemCount)], [7]));
        }
        else tx.WithEconomy(new EconomyMutation(kind, "merged-account", _role, kind, kind, "merged/" + kind, 1_000_000,
            costs, goods, [7], reward ? new EconomyRewardEvidence(progress.Level, beforeExp, progress.Level, progress.Exp,
                progress.JobLevel, progress.JobExp, progress.JobLevel, progress.JobExp, 10, 0) : null));
        return tx;
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
