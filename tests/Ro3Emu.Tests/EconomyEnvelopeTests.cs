using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class EconomyEnvelopeTests : IDisposable
{
    private readonly string _directory;
    private readonly string _gameData;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly long _role;
    private readonly EconomyCatalog _catalog;

    public EconomyEnvelopeTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "e05d-envelope-" + Guid.NewGuid().ToString("N"));
        _gameData = SyntheticData.Dir;
        _catalog = EconomyCatalog.Load(_gameData);
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory, GameDataDir = _gameData }));
        _roles = new RoleStore(_accounts);
        _role = _roles.Create("e05d-account", 1, "E05" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
    }

    private RoleItemPlanBuilder Spend(string operation, string source, long declared = 10, bool debit = true,
        int shopCount = 0, Action? onApply = null)
    {
        var tx = _roles.NewTransaction();
        var progress = _roles.LoadProgress(_role);
        if (debit) progress.Currency[1] -= 10;
        tx.Progress(_role, progress);
        if (shopCount > 0)
        {
            byte[] shop = JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState
            {
                Bought = new() { [118] = new ShopService.Counter { Count = shopCount, Period = "" } }
            });
            tx.Add(new RoleBlobWritePlan(_role, "shop", shop), _ => onApply?.Invoke());
        }
        tx.WithEconomy(new EconomyEnvelope(operation, "e05d-account", _role, "e05d-test-spend",
            "spend:10", source, 1_000_000,
            declared == 0 ? [] : [new(new(EconomyAssetKind.Currency, 1),
                new(EconomyOwnerKind.Role, _role.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                EconomyCompartment.Currency, -declared, EconomyLegKind.Sink, EconomyReason.FriendGiftCost)],
            [1]));
        return tx;
    }

    private EconomySnapshot Cut() => EconomySnapshotReader.Read(_accounts.DatabasePath, _catalog, "auto");
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) => EconomyLedgerReader.Read(
        _accounts.DatabasePath, _catalog, "auto", a.Cut.CommitSequence!.Value, b.Cut.CommitSequence!.Value);

    [Fact]
    public async Task TransactionSnapshotIncludesDungeonPendingAlongsideSemanticEnvelopeHoldings()
    {
        var obligations = new DungeonObligationStore(_accounts);
        byte[] dungeonState = JsonSerializer.SerializeToUtf8Bytes(new DungeonService.State());
        await obligations.FreezeAsync([new DungeonRewardObligation
        {
            Key = "dungeon:snapshot:" + Guid.NewGuid().ToString("N"), RoleId = _role,
            RoomId = 77, SceneEntry = 10111, Success = true, DurationMs = 1000, NewRecord = false,
            BaseState = dungeonState, NextState = dungeonState,
            Goods = [new(1004000, 2, 0, EconomyAssetKind.Item), new(1, 5, 0, EconomyAssetKind.Currency)],
            GuildId = 0, AuctionSourceType = 0, PlayBook = 0, AuctionTiers = [],
            RequiresKill = false, KillSourceKey = ""
        }]);
        var opening = Cut();
        Assert.Equal(2, opening.Holdings.Count(x => x.Location.Compartment == EconomyCompartment.Pending));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _accounts.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            db.Open();
            using var tx = db.BeginTransaction(deferred: true);
            var inside = EconomySnapshotReader.ReadTransaction(db, tx, _catalog);
            Assert.Empty(inside.Issues);
            Assert.Equal(opening.Holdings.ToHashSet(), inside.Holdings.ToHashSet());
        }
        await _roles.CommitAsync(Spend(Guid.NewGuid().ToString("N"), "spend-with-pending"), "test/e05d-with-e04-pending");
        var closing = Cut();
        Assert.Equal(opening.Holdings.Where(x => x.Location.Compartment == EconomyCompartment.Pending),
            closing.Holdings.Where(x => x.Location.Compartment == EconomyCompartment.Pending));
        var ledger = Window(opening, closing);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.All(EconomyExactnessChecker.Check(opening, closing, ledger).Locations,
            location => Assert.Equal("0", location.Residual));
    }

    [Fact]
    public void EnvelopeClonesOutcomeAndCanonicalIntentBytes()
    {
        byte[] input = [3, 4];
        var envelope = new EconomyEnvelope("immutable-test", "e05d-account", _role,
            "e05d-immutable", "immutable-v1", "immutable-source", 1_000_000, [], input);
        string hash = envelope.IntentHash;
        byte[] canonical = envelope.IntentPayload;
        input[0] = 9;
        envelope.Outcome[0] = 8;
        envelope.IntentPayload[0] = 0;
        Assert.Equal(new byte[] { 3, 4 }, envelope.Outcome);
        Assert.Equal(canonical, envelope.IntentPayload);
        Assert.Equal(hash, envelope.IntentHash);
    }

    [Fact]
    public async Task AsyncEnvelopeCommitsOnceAndReplaysBeforeStaleRevision()
    {
        string op = Guid.NewGuid().ToString("N"), source = "friend-gift:" + Guid.NewGuid().ToString("N");
        var first = Spend(op, source);
        var retry = Spend(op, source);
        var opening = Cut();
        await _roles.CommitAsync(first, "test/e05d-spend");
        await _roles.CommitAsync(retry, "test/e05d-spend-retry");
        var closing = Cut();
        var ledger = Window(opening, closing);
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(opening.Cut.CommitSequence + 1, closing.Cut.CommitSequence);
        Assert.Single(ledger.Receipts);
        Assert.Equal(-10, Assert.Single(ledger.Legs).Delta);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Locations, x => Assert.Equal("0", x.Residual));
    }

    [Fact]
    public async Task SameOperationWithChangedMetadataChildConflictsBeforeApply()
    {
        string op = Guid.NewGuid().ToString("N"), source = "child:" + Guid.NewGuid().ToString("N");
        int firstApply = 0, replayApply = 0, changedApply = 0;
        var first = Spend(op, source, shopCount: 1, onApply: () => firstApply++);
        var sameStale = Spend(op, source, shopCount: 1, onApply: () => replayApply++);
        var changedStale = Spend(op, source, shopCount: 2, onApply: () => changedApply++);
        var opening = Cut();
        await _roles.CommitAsync(first, "test/e05d-child-first");
        await _roles.CommitAsync(sameStale, "test/e05d-child-same-replay");
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            _roles.CommitAsync(changedStale, "test/e05d-child-changed"));
        Assert.Equal((1, 1, 0), (firstApply, replayApply, changedApply));
        Assert.Equal(1, JsonSerializer.Deserialize<ShopService.ShopState>(_roles.LoadShop(_role)!)!.Bought[118].Count);
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Single(Window(opening, Cut()).Receipts);
    }

    [Fact]
    public async Task GenericOwnerReceiptCannotReplayAnAbsentEconomicReceipt()
    {
        string operation = Guid.NewGuid().ToString("N");
        FixtureSql.Execute(_accounts, "INSERT INTO owner_operation_receipt(operation_id,family,result) " +
            "VALUES('" + operation + "','decoy',X'00')");
        var opening = Cut();
        await _roles.CommitAsync(Spend(operation, "decoy:" + operation), "test/e05d-generic-decoy");
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Single(Window(opening, Cut()).Receipts);
        Assert.NotNull(EconomyReceiptReader.Read(_accounts.DatabasePath, operation));
    }

    [Fact]
    public async Task LaterWindowValidatesEarlierTransferWithoutReturningItsLegs()
    {
        long other = _roles.Create("e05d-other", 1, "E05" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var transfer = _roles.NewTransaction();
        var sender = _roles.LoadProgress(_role);
        sender.Currency[1] -= 10;
        transfer.Progress(_role, sender);
        var recipient = _roles.LoadProgress(other);
        recipient.Currency[1] = 10;
        transfer.Progress(other, recipient);
        string operation = Guid.NewGuid().ToString("N");
        transfer.WithEconomy(new EconomyEnvelope(operation, "e05d-account", _role, "e05d-transfer",
            "transfer-10", "transfer:" + operation, 1_000_000,
            [new(new(EconomyAssetKind.Currency, 1), new(EconomyOwnerKind.Role, _role.ToString()),
                EconomyCompartment.Currency, -10, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, 1),
             new(new(EconomyAssetKind.Currency, 1), new(EconomyOwnerKind.Role, other.ToString()),
                EconomyCompartment.Currency, 10, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, 1)], [1]));
        await _roles.CommitAsync(transfer, "test/e05d-earlier-transfer");
        var afterTransfer = Cut();
        var metadata = _roles.NewTransaction();
        metadata.RoleBlob(_role, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        operation = Guid.NewGuid().ToString("N");
        metadata.WithEconomy(new EconomyEnvelope(operation, "e05d-account", _role, "e05d-later-metadata",
            "later-metadata", "metadata:" + operation, 1_000_001, [], [2]));
        await _roles.CommitAsync(metadata, "test/e05d-later-metadata");
        var window = Window(afterTransfer, Cut());
        Assert.True(window.CompleteHistory, string.Join("; ", window.Issues.Select(x => x.Detail)));
        Assert.Single(window.Receipts);
        Assert.Empty(window.Legs);
    }

    [Fact]
    public async Task GeneratedAuctionAliasBindsToClaimedTypedLotAndConcreteLedgerOwner()
    {
        var auction = new AuctionStore(_accounts);
        string batchSource = "challenge-lot:" + Guid.NewGuid().ToString("N");
        string lotSource = batchSource + ":0";
        string operation = Guid.NewGuid().ToString("N");
        var lot = new AuctionStore.Item { SourceKey = lotSource, Entry = 1004000, Currency = 1 };
        var plan = _roles.NewTransaction();
        auction.EnlistSource(batchSource, plan);
        auction.EnlistSave(lot, plan);
        plan.WithEconomy(new EconomyEnvelope(operation, "e05d-account", _role,
            "guild-challenge-auction-source", "auction-lot-v1", batchSource, 1_000_000,
            [new(new(EconomyAssetKind.Item, 1004000),
                new(EconomyOwnerKind.Escrow, "auction-source:" + lotSource),
                EconomyCompartment.AuctionLot, 1, EconomyLegKind.Source, EconomyReason.ActivityReward)], []));
        var opening = Cut();
        await _roles.CommitAsync(plan, "test/e05d-auction-lot");
        var closing = Cut();
        Assert.True(lot.Id > 0);
        var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, operation);
        Assert.NotNull(receipt);
        Assert.Equal(lot.Id, Assert.Single(receipt.ChildReceipts, x => x.Type == "auction-lot").Number);
        var ledger = Window(opening, closing);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        var leg = Assert.Single(ledger.Legs);
        Assert.Equal("auction:" + lot.Id, leg.Location.Owner.Id);
        Assert.All(EconomyExactnessChecker.Check(opening, closing, ledger).Locations,
            location => Assert.Equal("0", location.Residual));
    }

    [Fact]
    public async Task UndeclaredAndWrongAmountMovementRollsBack()
    {
        var opening = Cut();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(
            Spend(Guid.NewGuid().ToString("N"), "undeclared:" + Guid.NewGuid().ToString("N"), declared: 0),
            "test/e05d-undeclared"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(
            Spend(Guid.NewGuid().ToString("N"), "wrong:" + Guid.NewGuid().ToString("N"), declared: 9),
            "test/e05d-wrong"));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(opening.Cut.CommitSequence, Cut().Cut.CommitSequence);
        Assert.Empty(Window(opening, Cut()).Receipts);
    }

    [Fact]
    public async Task LastLedgerInsertFailureRollsBackOwnerReceiptAndSource()
    {
        string op = Guid.NewGuid().ToString("N"), source = "fault:" + Guid.NewGuid().ToString("N");
        var opening = Cut();
        FixtureSql.Execute(_accounts, "CREATE TRIGGER reject_e05d_leg BEFORE INSERT ON economy_entry BEGIN SELECT RAISE(ABORT,'e05d-last-leg'); END");
        await Assert.ThrowsAsync<SqliteException>(() => _roles.CommitAsync(Spend(op, source), "test/e05d-fault"));
        Assert.Equal(100, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(opening.Cut.CommitSequence, Cut().Cut.CommitSequence);
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, op));
        FixtureSql.Execute(_accounts, "DROP TRIGGER reject_e05d_leg");
        await _roles.CommitAsync(Spend(op, source), "test/e05d-retry");
        Assert.Equal(90, _roles.LoadProgress(_role).Currency[1]);
        Assert.Single(Window(opening, Cut()).Receipts);
    }

    [Fact]
    public async Task MetadataReceiptHasNoLegsAndTracksExistingAndCreatedGuildRevisions()
    {
        var guilds = new GuildStore(_accounts);
        var existing = new GuildStore.State { Name = "E05-existing-" + Guid.NewGuid().ToString("N")[..8] };
        Assert.True(guilds.Save(existing));
        existing.Notice = "new notice";
        var created = new GuildStore.State { Name = "E05-created-" + Guid.NewGuid().ToString("N")[..8] };
        var plan = _roles.NewTransaction();
        guilds.EnlistSave(existing, plan);
        guilds.EnlistSave(created, plan);
        string operation = Guid.NewGuid().ToString("N");
        plan.WithEconomy(new EconomyEnvelope(operation, "e05d-account", _role, "e05d-guild-metadata",
            "guild-metadata-v1", "guild-metadata:" + operation, 1_000_000, [], [1]));
        var opening = Cut();
        await _roles.CommitAsync(plan, "test/e05d-guild-metadata");
        var closing = Cut();
        var ledger = Window(opening, closing);
        Assert.Single(ledger.Receipts);
        Assert.Empty(ledger.Legs);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        using var db = new SqliteConnection("Data Source=" + _accounts.DatabasePath);
        db.Open();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT before_revisions,after_revisions FROM operation_receipt WHERE operation_id=$op";
        read.Parameters.AddWithValue("$op", operation);
        using var row = read.ExecuteReader();
        Assert.True(row.Read());
        using var before = JsonDocument.Parse(row.GetString(0));
        using var after = JsonDocument.Parse(row.GetString(1));
        var oldEntities = before.RootElement.GetProperty("entities").EnumerateArray().ToArray();
        var newEntities = after.RootElement.GetProperty("entities").EnumerateArray().ToArray();
        Assert.Equal(2, oldEntities.Length);
        Assert.Equal(2, newEntities.Length);
        Assert.Contains(oldEntities, x => x.GetProperty("Key").GetProperty("Id").GetString() == existing.Id.ToString() &&
            x.GetProperty("Exists").GetBoolean() && x.GetProperty("Revision").GetInt64() == 1);
        Assert.Contains(newEntities, x => x.GetProperty("Key").GetProperty("Id").GetString() == existing.Id.ToString() &&
            x.GetProperty("Exists").GetBoolean() && x.GetProperty("Revision").GetInt64() == 2);
        Assert.Contains(oldEntities, x => x.GetProperty("Key").GetProperty("Id").GetString() == created.Id.ToString() &&
            !x.GetProperty("Exists").GetBoolean() && x.GetProperty("Revision").GetInt64() == 0);
        Assert.Contains(newEntities, x => x.GetProperty("Key").GetProperty("Id").GetString() == created.Id.ToString() &&
            x.GetProperty("Exists").GetBoolean() && x.GetProperty("Revision").GetInt64() == 1);
        row.Close();
        using var damage = db.CreateCommand();
        damage.CommandText = "UPDATE operation_receipt SET after_revisions=json_set(after_revisions,'$.entities[0].Revision',99) WHERE operation_id=$op";
        damage.Parameters.AddWithValue("$op", operation);
        Assert.Equal(1, damage.ExecuteNonQuery());
        Assert.Contains(Window(opening, closing).Issues, x => x.Kind == EconomyIssueKind.Corruption);
    }

    [Fact]
    public async Task ExistingE01ReceiptSchemaUpgradesWithoutLosingReceiptSourceOrLegs()
    {
        string upgradeDir = Path.Combine(_directory, "upgrade-" + Guid.NewGuid().ToString("N"));
        string database;
        long oldRole;
        using (var old = new AccountStore(Options.Create(new ServerOptions { DataDir = upgradeDir, GameDataDir = _gameData })))
        {
            database = old.DatabasePath;
            var oldRoles = new RoleStore(old);
            var oldItems = new ItemStore(old);
            oldRole = oldRoles.Create("upgrade-account", 1, "UP" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
            var opening = oldRoles.LoadProgress(oldRole);
            opening.Currency[1] = 100;
            oldRoles.SaveProgress(oldRole, opening);
            var purchase = oldRoles.NewTransaction();
            purchase.ItemDelta(oldRole, oldItems.Load(oldRole),
                [new InvItem { Entry = 1004000, Stack = 1, Pack = 1, Sort = 1 }]);
            var paid = oldRoles.LoadProgress(oldRole);
            paid.Currency[1] -= 10;
            purchase.Progress(oldRole, paid);
            purchase.RoleBlob(oldRole, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState
            {
                Bought = new() { [118] = new ShopService.Counter { Count = 1, Period = "" } }
            }));
            purchase.WithEconomy(new EconomyMutation("upgrade-purchase", "upgrade-account", oldRole,
                "npc-shop-buy", "upgrade-cost10-goods1", "upgrade-source", 1_000_000,
                [new(new(EconomyAssetKind.Currency, 1), 10)],
                [new(new(EconomyAssetKind.Item, 1004000), 1)], [1]));
            oldRoles.Commit(purchase, "test/e01-before-upgrade");
        }
        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection("Data Source=" + database + ";Foreign Keys=False"))
        {
            db.Open();
            using var schema = db.CreateCommand();
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='operation_receipt'";
            string definition = (string)schema.ExecuteScalar()!;
            string legacy = definition.Replace("operation_receipt", "operation_receipt_legacy", StringComparison.Ordinal)
                .Replace("CHECK(leg_count>=0 AND (command_kind<>'npc-shop-buy' OR leg_count>0))",
                    "CHECK(leg_count>0)", StringComparison.Ordinal);
            Assert.NotEqual(definition.Replace("operation_receipt", "operation_receipt_legacy", StringComparison.Ordinal), legacy);
            using var rebuild = db.CreateCommand();
            rebuild.CommandText = legacy + "; INSERT INTO operation_receipt_legacy SELECT * FROM operation_receipt; " +
                "DROP TABLE operation_receipt; ALTER TABLE operation_receipt_legacy RENAME TO operation_receipt;";
            rebuild.ExecuteNonQuery();
        }
        using (var migrated = new AccountStore(Options.Create(new ServerOptions { DataDir = upgradeDir, GameDataDir = _gameData })))
        {
            var migratedRoles = new RoleStore(migrated);
            var oldLedger = EconomyLedgerReader.Read(database, _catalog, "auto", 0, 1);
            Assert.True(oldLedger.CompleteHistory, string.Join("; ", oldLedger.Issues.Select(x => x.Detail)));
            Assert.Single(oldLedger.Receipts);
            Assert.Equal(2, oldLedger.Legs.Count);
            var metadata = migratedRoles.NewTransaction();
            metadata.RoleBlob(oldRole, "shop", JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState
            {
                Bought = new() { [118] = new ShopService.Counter { Count = 2, Period = "" } }
            }));
            metadata.WithEconomy(new EconomyEnvelope("upgrade-metadata", "upgrade-account", oldRole,
                "e05d-upgrade-metadata", "upgrade-metadata-v1", "upgrade-metadata-source", 1_000_001, [], [2]));
            await migratedRoles.CommitAsync(metadata, "test/e05d-after-upgrade");
            var full = EconomyLedgerReader.Read(database, _catalog, "auto", 0, 2);
            Assert.True(full.CompleteHistory, string.Join("; ", full.Issues.Select(x => x.Detail)));
            Assert.Equal(2, full.Receipts.Count);
            Assert.Equal(2, full.Legs.Count);
            using var db = new SqliteConnection("Data Source=" + database);
            db.Open();
            using var foreignKeys = db.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var broken = foreignKeys.ExecuteReader();
            Assert.False(broken.Read());
            using var invalid = db.CreateCommand();
            invalid.CommandText = "UPDATE operation_receipt SET leg_count=0 WHERE operation_id='upgrade-purchase'";
            Assert.Throws<SqliteException>(() => invalid.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
