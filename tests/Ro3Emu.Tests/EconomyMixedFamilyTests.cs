using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class EconomyMixedFamilyTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _shopRole;
    private readonly long _familyRole;
    private readonly long _rewardRole;
    private readonly long _semanticRole;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public EconomyMixedFamilyTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "mixed-economy-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(_directory, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "ItemConfig.json"), """{"1":{"_iType":1},"3":{"_iType":0}}""");
        foreach (string name in new[] { "AuctionConfig.json", "GuildSettingConfig.json",
                     "GuildTransportConfig.json", "GVGConfig.json" })
            File.WriteAllText(Path.Combine(config, name), "{}");
        File.WriteAllText(Path.Combine(config, "PublicParamConfig.json"), """{"AUCTION_CURRENCY":1}""");
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory, GameDataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _accounts.WriteSync<int>(new MarketSchemaPlan());
        _accounts.WriteSync<int>(new AuctionSchemaPlan());
        _accounts.WriteSync<int>(new GuildSchemaPlan());
        _accounts.WriteSync<int>(new TerritorySchemaPlan(RealmStateKind.TerritoryWars));
        _accounts.WriteSync<int>(new TerritorySchemaPlan(RealmStateKind.TerritoryEscort));
        _accounts.WriteSync<int>(new FestivalSchemaPlan());
        _accounts.WriteSync<int>(new GuildChallengeSchemaPlan());
        _accounts.WriteSync<int>(new DungeonObligationSchemaPlan());
        // These two production schema plans are private to their services; the empty tables have
        // the same definitions and make the fresh fixture a complete snapshot schema.
        FixtureSql.Execute(_accounts, """
            CREATE TABLE IF NOT EXISTS guild_caravan_member
                (role_id INTEGER PRIMARY KEY, state BLOB NOT NULL, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS guild_caravan_version
                (guild_id INTEGER PRIMARY KEY, day INTEGER NOT NULL, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS guild_campfire
                (guild_id INTEGER PRIMARY KEY, state BLOB NOT NULL);
            """);
        _shopRole = CreateRole("mixed-shop");
        _familyRole = CreateRole("mixed-family");
        _rewardRole = CreateRole("mixed-reward");
        _semanticRole = CreateRole("mixed-semantic");
        foreach (long role in new[] { _shopRole, _familyRole })
        {
            var progress = _roles.LoadProgress(role);
            progress.Currency[1] = 100;
            _roles.SaveProgress(role, progress);
        }
    }

    private long CreateRole(string account) => _roles.Create(account, 1,
        "M" + Guid.NewGuid().ToString("N")[..10], 1, 1000, 1)!.Id;

    [Theory]
    [InlineData("e01")]
    [InlineData("e05b")]
    [InlineData("e04")]
    [InlineData("e05d")]
    [InlineData("e03")]
    [InlineData("e05e")]
    public async Task OneRetainedLedgerRoutesSixKindsAndRejectsEachKindsCorruption(string damagedKind)
    {
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        const string shopOp = "mixed-shop-op", familyOp = "mixed-family-op";
        const string rewardOp = "mixed-reward-op", semanticOp = "mixed-semantic-op";

        var shop = _roles.NewTransaction();
        shop.ItemDelta(_shopRole, [], [new InvItem { Entry = 3, Stack = 1, Pack = 1 }]);
        var shopProgress = _roles.LoadProgress(_shopRole);
        shopProgress.Currency[1] -= 10;
        shop.Progress(_shopRole, shopProgress);
        shop.RoleBlob(_shopRole, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        shop.WithEconomy(new EconomyMutation(shopOp, "mixed-shop", _shopRole, "npc-shop-buy", "mixed/shop/1",
            "mixed/shop/source", 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Item, 3), 1)], [1]));
        _roles.Commit(shop, "test/mixed-shop");

        var family = _roles.NewTransaction();
        family.ItemDelta(_familyRole, [], []);
        var familyProgress = _roles.LoadProgress(_familyRole);
        familyProgress.Currency[1] -= 5;
        family.Progress(_familyRole, familyProgress);
        family.RoleBlob(_familyRole, "pets", new DBPetInfo { CurrentPlan = 1 }.ToByteArray());
        family.WithEconomy(new EconomyMutation(familyOp, "mixed-family", _familyRole, "cd13b/pet-feed",
            "mixed/family/1", "mixed/family/source", 1_000_001,
            [new(new(EconomyAssetKind.Currency, 1), 5)], [], [2]));
        _roles.Commit(family, "test/mixed-family");

        var reward = _roles.NewTransaction();
        reward.ItemDelta(_rewardRole, [], []);
        var rewardProgress = _roles.LoadProgress(_rewardRole);
        rewardProgress.Exp += 10;
        reward.Progress(_rewardRole, rewardProgress);
        reward.WithEconomy(new EconomyMutation(rewardOp, "mixed-reward", _rewardRole, "monster-kill",
            "mixed/reward/1", "mixed/reward/source", 1_000_002, [], [], [3],
            new EconomyRewardEvidence(1, 0, 1, 10, 1, 0, 1, 0, 10, 0)));
        _roles.Commit(reward, "test/mixed-reward");

        var semantic = _roles.NewTransaction();
        semantic.RoleBlob(_semanticRole, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        semantic.WithEconomy(new EconomyEnvelope(semanticOp, "mixed-semantic", _semanticRole,
            "e05d-shop-metadata", "mixed/semantic/1", "mixed/semantic/source", 1_000_003, [], []));
        _roles.Commit(semantic, "test/mixed-semantic");

        // E03's JSON children must coexist with every master's binary receipt, even when
        // the same role participates in both. A lost ACK resolves through the E03 reader.
        var escrow = _roles.NewTransaction();
        var escrowDebit = _roles.LoadProgress(_familyRole);
        var escrowCredit = _roles.LoadProgress(_semanticRole);
        escrowDebit.Currency[1] -= 7;
        escrowCredit.Currency[1] = 7;
        escrow.Progress(_familyRole, escrowDebit);
        escrow.Progress(_semanticRole, escrowCredit);
        var escrowIntent = new EscrowEconomyIntent("market/mixed-transfer", "mixed-family", _familyRole);
        escrow.WithEscrowEconomy(escrowIntent);
        _accounts.FailNextEconomyAcknowledgementForTest();
        await _roles.CommitAsync(escrow, "test/mixed-escrow");
        string escrowOp = escrowIntent.OperationId;

        const string eventOp = "mixed-event-op";
        var eventPlan = _roles.NewTransaction();
        var eventProgress = _roles.LoadProgress(_semanticRole);
        eventProgress.Currency[1] += 3;
        eventPlan.Progress(_semanticRole, eventProgress);
        eventPlan.WithEventEconomy(new EventEconomyMutation(Catalog, eventOp, "mixed-semantic", _semanticRole,
            "event/mixed-grant", "mixed/event/source", 1_000_005, [],
            [new(new(EconomyAssetKind.Currency, 1), 3)],
            [new(new(EconomyOwnerKind.Role, _semanticRole.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(EconomyAssetKind.Currency, 1), 3)], [7, 8]));
        await _roles.CommitAsync(eventPlan, "test/mixed-event");

        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        EconomyLedgerWindow Read() => EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        var ledger = Read();
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(new[] { "npc-shop-buy", "cd13b/pet-feed", "monster-kill", "e05d-shop-metadata",
                "market/mixed-transfer", "event/mixed-grant" },
            ledger.Receipts.Select(x => x.CommandKind));
        foreach (var committed in ledger.Receipts)
            Assert.Equal(committed.CommandKind,
                EconomyReceiptReader.Read(_accounts.DatabasePath, committed.OperationId)!.CommandKind);
        Assert.Equal(2, ledger.Legs.Count(x => x.OperationId == escrowOp));
        Assert.Single(ledger.Legs, x => x.OperationId == eventOp);
        Assert.Equal(2, ledger.Legs.Count(x => x.OperationId == shopOp));
        Assert.Single(ledger.Legs, x => x.OperationId == familyOp);
        Assert.DoesNotContain(ledger.Legs, x => x.OperationId is rewardOp or semanticOp);
        Assert.Equal(10, ledger.Receipts.Single(x => x.OperationId == rewardOp).RewardEvidence!.ResolvedBaseExp);
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.Empty(exact.Issues);
        Assert.All(exact.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(exact.Locations, x => Assert.Equal("0", x.Residual));
        // Only these six controlled writers run in this fresh database. The production reader retains
        // its global uncovered-family marker; remove that marker solely to certify this scoped fixture.
        var scoped = EconomyExactnessChecker.Check(
            opening with { UncoveredMutationFamilies = [] },
            closing with { UncoveredMutationFamilies = [] },
            ledger with { UncoveredMutationFamilies = [] });
        Assert.Equal(EconomyVerdict.Pass, scoped.Verdict);

        using var db = new SqliteConnection("Data Source=" + _accounts.DatabasePath);
        db.Open();
        using var change = db.CreateCommand();
        switch (damagedKind)
        {
            case "e01":
                change.CommandText = "UPDATE economy_entry SET reason_code=$reason WHERE operation_id=$op AND leg_kind=$kind";
                change.Parameters.AddWithValue("$op", shopOp);
                change.Parameters.AddWithValue("$kind", (int)EconomyLegKind.Source);
                change.Parameters.AddWithValue("$reason", (int)EconomyReason.MonsterDrop);
                break;
            case "e05b":
                change.CommandText = "UPDATE economy_entry SET reason_code=$reason WHERE operation_id=$op";
                change.Parameters.AddWithValue("$op", familyOp);
                change.Parameters.AddWithValue("$reason", (int)EconomyReason.GuildServiceCost);
                break;
            case "e04":
                var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, rewardOp)!;
                var intent = JsonNode.Parse(receipt.IntentPayload)!.AsObject();
                intent["RewardEvidence"] = null;
                byte[] payload = Encoding.UTF8.GetBytes(intent.ToJsonString());
                change.CommandText = "UPDATE operation_receipt SET intent_payload=$payload,intent_hash=$hash WHERE operation_id=$op";
                change.Parameters.AddWithValue("$op", rewardOp);
                change.Parameters.AddWithValue("$payload", payload);
                change.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(payload)));
                break;
            case "e05d":
                change.CommandText = "UPDATE operation_receipt SET after_revisions=json_set(after_revisions,'$.roles[0].parts.shop',99) WHERE operation_id=$op";
                change.Parameters.AddWithValue("$op", semanticOp);
                break;
            case "e03":
                change.CommandText = "UPDATE economy_entry SET reason_code=$reason WHERE operation_id=$op AND delta>0";
                change.Parameters.AddWithValue("$op", escrowOp);
                change.Parameters.AddWithValue("$reason", (int)EconomyReason.InventoryRelocation);
                break;
            case "e05e":
                change.CommandText = "UPDATE economy_entry SET owner_id=$owner WHERE operation_id=$op";
                change.Parameters.AddWithValue("$op", eventOp);
                change.Parameters.AddWithValue("$owner", _shopRole.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
        Assert.Equal(1, change.ExecuteNonQuery());
        var corrupted = Read();
        Assert.False(corrupted.CompleteHistory);
        Assert.Contains(corrupted.Issues, x => x.Kind == EconomyIssueKind.Corruption &&
            x.Key.Contains(damagedKind switch
            {
                "e01" => shopOp,
                "e05b" => familyOp,
                "e04" => rewardOp,
                "e03" => escrowOp,
                "e05e" => eventOp,
                _ => semanticOp
            }, StringComparison.Ordinal));
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(opening, closing, corrupted).Verdict);
    }

    [Fact]
    public void BrokenTransferPairReturnsAnExactCounterpartyIssue()
    {
        _items.Insert(_familyRole, new InvItem { Entry = 3, Stack = 1, Pack = 1, Sort = 1 });
        var before = _items.Load(_familyRole);
        var moved = before.Select(x => x.Clone()).ToList();
        moved[0].Pack = 2;
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_familyRole, before, moved);
        tx.RoleBlob(_familyRole, "pets", new DBPetInfo { CurrentPlan = 1 }.ToByteArray());
        const string operation = "mixed-relocation-op";
        tx.WithEconomy(new EconomyMutation(operation, "mixed-family", _familyRole,
            "cd13b/pet-neutral", "mixed/relocation/1", "mixed/relocation/source", 1_000_004,
            [], [], [4]));
        _roles.Commit(tx, "test/mixed-relocation");
        EconomyLedgerWindow Read() => EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        var original = Read();
        Assert.True(original.CompleteHistory, string.Join("; ", original.Issues.Select(x => x.Detail)));
        Assert.Equal(2, original.Legs.Count(x => x.Kind == EconomyLegKind.Transfer));
        using var db = new SqliteConnection("Data Source=" + _accounts.DatabasePath);
        db.Open();
        using var damage = db.CreateCommand();
        damage.CommandText = "UPDATE economy_entry SET delta=-delta WHERE operation_id=$op AND delta>0";
        damage.Parameters.AddWithValue("$op", operation);
        Assert.Equal(1, damage.ExecuteNonQuery());
        var corrupted = Read();
        Assert.False(corrupted.CompleteHistory);
        Assert.Contains(corrupted.Issues, x => x.Kind == EconomyIssueKind.Corruption &&
            x.Key.StartsWith("edge/", StringComparison.Ordinal) &&
            x.Detail == "Relocation edge lacks one exact opposite counterparty.");
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
