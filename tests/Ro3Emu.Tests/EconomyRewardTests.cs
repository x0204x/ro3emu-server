using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class EconomyRewardTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public EconomyRewardTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "e04-economy-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _role = _roles.Create("reward-account", 1, "E04" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
    }

    private (string Operation, EconomySnapshot Opening, EconomySnapshot Closing) CommitEmptyKill(
        long baseExp = 10, EconomySnapshot? openingOverride = null)
    {
        string operation = Guid.NewGuid().ToString("N");
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var progress = _roles.LoadProgress(_role);
        var beforeExp = progress.Exp;
        progress.Exp += baseExp;
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        tx.Progress(_role, progress);
        tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "monster-kill", "kill/roll/empty",
            "monster:" + operation, 1_000_000, [], [], [1],
            new EconomyRewardEvidence(progress.Level, beforeExp, progress.Level, progress.Exp,
                progress.JobLevel, progress.JobExp, progress.JobLevel, progress.JobExp, baseExp, 0)));
        _roles.Commit(tx, "test/e04-empty-kill");
        return (operation, openingOverride ?? opening, EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto"));
    }

    [Fact]
    public void ExpOnlyRewardCommitsZeroLegReceiptAndExactProgressChain()
    {
        Assert.Equal(EconomySnapshotReader.ExpectedFormatManifestHash, EconomySnapshotReader.FormatManifestHash);
        var (operation, opening, closing) = CommitEmptyKill();
        var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, operation)!;
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);

        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
        Assert.Equal(0, receipt.LegCount);
        Assert.Empty(ledger.Legs);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.Equal(10, Assert.Single(ledger.Receipts).RewardEvidence!.ResolvedBaseExp);
    }

    [Fact]
    public void StarterItemAndMarkerBuilderRollsBackTogetherWithoutLoginActivation()
    {
        // E02 proves the common builder boundary; concurrent first-login activation is E06's owner.
        Assert.False(_roles.LoadProgress(_role).StarterKit);
        RoleItemPlanBuilder Build()
        {
            var progress = _roles.LoadProgress(_role);
            progress.StarterKit = true;
            progress.Currency[1] = 1000;
            var builder = _roles.NewTransaction();
            builder.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
            builder.Progress(_role, progress);
            return builder;
        }

        FixtureSql.Execute(_accounts, """
            CREATE TRIGGER e02_starter_fail BEFORE UPDATE OF starter_kit ON role
            WHEN NEW.starter_kit=1 BEGIN SELECT RAISE(ABORT, 'starter marker rollback'); END
            """);
        Assert.Throws<SqliteException>(() => _roles.Commit(Build(), "test/e02-starter-abort"));
        Assert.Empty(_items.Load(_role));
        Assert.False(_roles.LoadProgress(_role).StarterKit);
        Assert.Equal(0, _roles.LoadProgress(_role).Currency.GetValueOrDefault(1));
        FixtureSql.Execute(_accounts, "DROP TRIGGER e02_starter_fail");

        _roles.Commit(Build(), "test/e02-starter-builder");
        Assert.True(_roles.LoadProgress(_role).StarterKit);
        Assert.Equal(1000, _roles.LoadProgress(_role).Currency[1]);
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("between")]
    [InlineData("after")]
    public void UncoveredGmExpWriteCreatesProgressCoverageGap(string position)
    {
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        if (position != "before") CommitEmptyKill();
        if (position == "between") CommitEmptyKill();
        var gm = _roles.LoadProgress(_role);
        gm.Exp += 4;
        _roles.SaveProgress(_role, gm);
        if (position != "after") CommitEmptyKill(openingOverride: opening);
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.Equal(EconomyVerdict.NotMeasured, check.Verdict);
        Assert.Contains(check.Issues, x => x.Kind == EconomyIssueKind.Coverage && x.Key.StartsWith("progress/"));
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
    }

    [Fact]
    public void ExpTamperWithoutProgressRevisionIsCorruption()
    {
        var (_, opening, _) = CommitEmptyKill();
        FixtureSql.Execute(_accounts, $"UPDATE role SET exp=exp+4 WHERE id={_role}");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.Equal(EconomyVerdict.Fail, check.Verdict);
        Assert.Contains(check.Issues, x => x.Kind == EconomyIssueKind.Corruption && x.Key.StartsWith("progress/"));
    }

    [Fact]
    public void GatherYieldCanCommitFirstCardSightAfterLifeSourceBlobs()
    {
        string operation = Guid.NewGuid().ToString("N");
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var progress = _roles.LoadProgress(_role);
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        tx.Progress(_role, progress);
        tx.RoleBlob(_role, "lifejob", []);
        tx.RoleBlob(_role, "life_restrict", []);
        tx.RoleBlob(_role, "life_runtime", Encoding.UTF8.GetBytes("{}"));
        tx.RoleBlob(_role, "library", []);
        tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "gather-yield", "card-gather",
            "gather:" + operation, 1_000_000, [], [], [1],
            new EconomyRewardEvidence(progress.Level, progress.Exp, progress.Level, progress.Exp,
                progress.JobLevel, progress.JobExp, progress.JobLevel, progress.JobExp, 0, 0)));
        _roles.Commit(tx, "test/e04-gather-card-sight");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.DoesNotContain(EconomyExactnessChecker.Check(opening, closing, ledger).Issues,
            issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    [Fact]
    public void WriterRejectsProgressThatDoesNotMatchFrozenEvidence()
    {
        string operation = Guid.NewGuid().ToString("N");
        var progress = _roles.LoadProgress(_role);
        progress.Exp = 10;
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        tx.Progress(_role, progress);
        tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "monster-kill", "wrong-after",
            "monster:" + operation, 1_000_000, [], [], [1],
            new EconomyRewardEvidence(1, 0, 1, 9, 1, 0, 1, 0, 9, 0)));
        Assert.Throws<InvalidOperationException>(() => _roles.Commit(tx, "test/e04-wrong-progress"));
        Assert.Equal(0, _roles.LoadProgress(_role).Exp);
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, operation));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    public void ReaderRejectsMissingOrWrongExpEvidenceEvenWithMatchingAssetLegs(string alteration)
    {
        var (operation, opening, closing) = CommitEmptyKill();
        var receipt = EconomyReceiptReader.Read(_accounts.DatabasePath, operation)!;
        var json = JsonNode.Parse(receipt.IntentPayload)!.AsObject();
        if (alteration == "missing") json["RewardEvidence"] = null;
        else json["RewardEvidence"]!["ResolvedBaseExp"] = 11;
        byte[] payload = Encoding.UTF8.GetBytes(json.ToJsonString());
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        FixtureSql.Execute(_accounts, $"UPDATE operation_receipt SET intent_payload=X'{Convert.ToHexString(payload)}',intent_hash='{hash}' WHERE operation_id='{operation}'");

        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.False(ledger.CompleteHistory);
        Assert.Equal(EconomyVerdict.Fail, check.Verdict);
        Assert.Contains(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption && issue.Key.Contains(operation));
    }

    [Fact]
    public void ReaderRejectsChangedNaturalSourceClaim()
    {
        var (operation, opening, closing) = CommitEmptyKill();
        FixtureSql.Execute(_accounts,
            $"UPDATE operation_source SET source_key='altered:{operation}' WHERE operation_id='{operation}'");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.False(ledger.CompleteHistory);
        Assert.Contains(ledger.Issues, issue => issue.Kind == EconomyIssueKind.Corruption && issue.Key.Contains("source"));
    }

    [Fact]
    public void ConversionEvidenceMustExplainTheFinalAuthorizedGoods()
    {
        var item = new EconomyAsset(EconomyAssetKind.Item, 3);
        var coin = new EconomyAsset(EconomyAssetKind.Currency, 1);
        var valid = new EconomyRewardEvidence(1, 0, 1, 0, 1, 0, 1, 0, 0, 0,
            OriginalGoods: [new(item, 3)], ConvertedInputs: [new(item, 1)],
            ConvertedOutputs: [new(coin, 2)]);
        var finalGoods = new EconomyQuantity[] { new(item, 2), new(coin, 2) };
        _ = new EconomyMutation("conversion-good", "reward-account", _role, "monster-kill", "conversion",
            "monster:conversion-good", 1_000_000, [], finalGoods, [1], valid);
        Assert.Throws<ArgumentException>(() => new EconomyMutation("conversion-bad", "reward-account", _role,
            "monster-kill", "conversion", "monster:conversion-bad", 1_000_000, [],
            [new(item, 2)], [1], valid));
    }

    [Fact]
    public async Task OwnedRewardLostAcknowledgementAppliesOnceAndReplaysItsEconomicReceipt()
    {
        string operation = Guid.NewGuid().ToString("N");
        string source = "monster:lost-ack:" + operation;
        RoleItemPlanBuilder Build()
        {
            var tx = _roles.NewTransaction();
            tx.ItemDelta(_role, [], []);
            var progress = _roles.LoadProgress(_role);
            progress.Exp = 10;
            tx.Progress(_role, progress);
            tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "monster-kill", "owned-lost-ack",
                source, 1_000_000, [], [], [1],
                new EconomyRewardEvidence(1, 0, 1, 10, 1, 0, 1, 0, 10, 0)));
            return tx;
        }
        var first = Build();
        var duplicate = Build();
        _accounts.FailNextEconomyAcknowledgementForTest();
        int applied = 0;
        await _roles.CommitAsync(first, "test/e04-owned-lost-ack", _ => applied++);
        Assert.Equal(1, applied);
        await _roles.CommitAsync(duplicate, "test/e04-owned-lost-ack-replay");
        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Equal(operation, Assert.Single(ledger.Receipts).OperationId);
    }

    [Fact]
    public async Task OwnedReplayCannotUseGenericReceiptAfterNaturalSourceIsLost()
    {
        string operation = Guid.NewGuid().ToString("N");
        string source = "monster:" + operation;
        RoleItemPlanBuilder Build()
        {
            var tx = _roles.NewTransaction();
            tx.ItemDelta(_role, [], []);
            var progress = _roles.LoadProgress(_role);
            progress.Exp = 10;
            tx.Progress(_role, progress);
            tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "monster-kill", "owned-replay",
                source, 1_000_000, [], [], [1],
                new EconomyRewardEvidence(1, 0, 1, 10, 1, 0, 1, 0, 10, 0)));
            return tx;
        }
        var first = Build();
        var duplicate = Build();
        await _roles.CommitAsync(first, "test/e04-owned-first");
        FixtureSql.Execute(_accounts, $"DELETE FROM operation_source WHERE operation_id='{operation}'");

        await Assert.ThrowsAsync<InvalidDataException>(() => _roles.CommitAsync(duplicate, "test/e04-owned-replay"));
        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
    }

    [Fact]
    public async Task DungeonEarnAndDeliveryTransferAcrossCommittedCuts()
    {
        var obligations = new DungeonObligationStore(_accounts);
        string source = "dungeon:test:" + Guid.NewGuid().ToString("N");
        byte[] dungeonState = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DungeonService.State());
        var frozen = new DungeonRewardObligation
        {
            Key = source, RoleId = _role, RoomId = 77, SceneEntry = 10111, Success = true,
            DurationMs = 1000, NewRecord = false, BaseState = dungeonState, NextState = dungeonState,
            Goods = [new(3, 2, 0, EconomyAssetKind.Item), new(1, 5, 0, EconomyAssetKind.Currency),
                new(100, 10, 0, EconomyAssetKind.Item),
                new(101, 3, 0, EconomyAssetKind.Item)],
            GuildId = 0, AuctionSourceType = 0, PlayBook = 0, AuctionTiers = [],
            RequiresKill = false, KillSourceKey = ""
        };
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        await obligations.FreezeAsync([frozen]);
        var pending = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var earned = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, pending.Cut.CommitSequence!.Value);
        var earnCheck = EconomyExactnessChecker.Check(opening, pending, earned);
        Assert.Equal(1, pending.Cut.CommitSequence);
        Assert.Equal(2, pending.Holdings.Count(h => h.Location.Compartment == EconomyCompartment.Pending));
        Assert.Equal(2, earned.Legs.Count);
        Assert.All(earned.Legs, leg => Assert.Equal(EconomyCompartment.Pending, leg.Location.Compartment));
        Assert.DoesNotContain(earnCheck.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);

        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, _items.Load(_role), [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
        var progress = _roles.LoadProgress(_role);
        progress.Exp = 10;
        progress.JobExp = 3;
        progress.Currency[1] = 5;
        tx.Progress(_role, progress);
        tx.RoleBlob(_role, "dungeons", dungeonState);
        tx.Add(new DungeonObligationCompletePlan(source));
        tx.RoleBlob(_role, "library", []); // Card sight is staged after the dungeon completion child.
        string operation = Guid.NewGuid().ToString("N");
        var good = new EconomyQuantity(new(EconomyAssetKind.Item, 3), 2);
        var currency = new EconomyQuantity(new(EconomyAssetKind.Currency, 1), 5);
        tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "dungeon-reward", "frozen-test",
            source, 1_000_000, [], [good, currency], [1],
            new EconomyRewardEvidence(1, 0, 1, 10, 1, 0, 1, 3, 10, 3,
                OriginalGoods: [good, currency])));
        _roles.Commit(tx, "test/e04-dungeon-delivery");
        var delivered = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var delivery = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            pending.Cut.CommitSequence!.Value, delivered.Cut.CommitSequence!.Value);
        var deliveryCheck = EconomyExactnessChecker.Check(pending, delivered, delivery);
        Assert.Equal(2, delivered.Cut.CommitSequence);
        Assert.DoesNotContain(delivered.Holdings, h => h.Location.Compartment == EconomyCompartment.Pending);
        Assert.Equal(2, Assert.Single(_items.Load(_role)).Stack);
        Assert.Equal(new long[] { -5, -2, 2, 5 }, delivery.Legs.Select(x => x.Delta).Order().ToArray());
        Assert.All(delivery.Legs, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
        Assert.DoesNotContain(deliveryCheck.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
    }

    [Fact]
    public async Task PreparedDungeonEntitlementBecomesPendingOnlyWithTerminalKill()
    {
        var obligations = new DungeonObligationStore(_accounts);
        string killSource = "monster:terminal:" + Guid.NewGuid().ToString("N");
        string source = "dungeon:prepared:" + Guid.NewGuid().ToString("N");
        byte[] dungeonState = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DungeonService.State());
        await obligations.FreezeAsync([new DungeonRewardObligation
        {
            Key = source, RoleId = _role, RoomId = 87, SceneEntry = 10111, Success = true,
            DurationMs = 1000, NewRecord = false, BaseState = dungeonState, NextState = dungeonState,
            Goods = [new(3, 2, 0, EconomyAssetKind.Item)], GuildId = 0,
            AuctionSourceType = 0, PlayBook = 0, AuctionTiers = [],
            RequiresKill = true, KillSourceKey = killSource
        }]);
        var opening = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        Assert.Equal(0, opening.Cut.CommitSequence);
        Assert.DoesNotContain(opening.Holdings, h => h.Location.Compartment == EconomyCompartment.Pending);

        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        var progress = _roles.LoadProgress(_role);
        tx.Progress(_role, progress);
        tx.Add(new DungeonObligationActivatePlan(87, killSource));
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(new EconomyMutation(operation, "reward-account", _role, "monster-kill", "prepared-kill",
            killSource, 1_000_000, [], [], [1],
            new EconomyRewardEvidence(progress.Level, progress.Exp, progress.Level, progress.Exp,
                progress.JobLevel, progress.JobExp, progress.JobLevel, progress.JobExp, 0, 0)));
        _roles.Commit(tx, "test/e04-activate-pending");
        var closing = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.Equal(2, closing.Cut.CommitSequence);
        Assert.Equal(2, Assert.Single(closing.Holdings, h => h.Location.Compartment == EconomyCompartment.Pending).Quantity);
        Assert.Equal(2, ledger.Receipts.Count);
        Assert.Single(ledger.Legs);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);

        // Remove only the terminal receipt from a copied committed state. The earned entitlement cannot stand alone.
        FixtureSql.Execute(_accounts, $"DELETE FROM operation_source WHERE operation_id='{operation}'; " +
            $"DELETE FROM operation_receipt WHERE operation_id='{operation}'; " +
            "UPDATE economy_meta SET next_sequence=1 WHERE singleton=1");
        var orphaned = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto");
        var orphanedLedger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto",
            opening.Cut.CommitSequence!.Value, orphaned.Cut.CommitSequence!.Value);
        var orphanedCheck = EconomyExactnessChecker.Check(opening, orphaned, orphanedLedger);
        Assert.Equal(EconomyVerdict.Fail, orphanedCheck.Verdict);
        Assert.Contains(orphanedCheck.Issues, x => x.Kind == EconomyIssueKind.Corruption &&
            x.Detail.Contains("terminal kill", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingE01ReceiptAndForeignKeysSurviveZeroLegSchemaUpgrade()
    {
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
        string purchase = Guid.NewGuid().ToString("N");
        var buy = _roles.NewTransaction();
        buy.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
        progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 90;
        buy.Progress(_role, progress);
        buy.RoleBlob(_role, "shop", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        buy.WithEconomy(new EconomyMutation(purchase, "reward-account", _role, "npc-shop-buy", "migration-buy",
            "npc:" + purchase, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Item, 3), 2)], [1]));
        _roles.Commit(buy, "test/e04-old-e01");
        FixtureSql.Execute(_accounts, """
            CREATE TABLE operation_source_saved AS SELECT realm_id,source_key,operation_id FROM operation_source;
            CREATE TABLE economy_entry_saved AS SELECT entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key FROM economy_entry;
            CREATE TABLE operation_receipt_e01 (
                operation_id TEXT PRIMARY KEY, realm_id TEXT NOT NULL, commit_seq INTEGER NOT NULL UNIQUE,
                actor_account TEXT NOT NULL, actor_role INTEGER NOT NULL, command_kind TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL, intent_hash TEXT NOT NULL, intent_payload BLOB NOT NULL,
                source_key TEXT NOT NULL UNIQUE, recorded_utc_ms INTEGER NOT NULL, effective_utc_ms INTEGER NOT NULL,
                outcome BLOB NOT NULL, child_receipts BLOB NOT NULL, before_revisions TEXT NOT NULL,
                after_revisions TEXT NOT NULL, leg_count INTEGER NOT NULL CHECK(leg_count>0));
            INSERT INTO operation_receipt_e01(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,child_receipts,before_revisions,after_revisions,leg_count)
            SELECT operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                   request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,
                   outcome,child_receipts,before_revisions,after_revisions,leg_count
            FROM operation_receipt;
            DROP TABLE operation_source;
            DROP TABLE economy_entry;
            DROP TABLE operation_receipt;
            ALTER TABLE operation_receipt_e01 RENAME TO operation_receipt;
            CREATE TABLE operation_source (
                realm_id TEXT NOT NULL, source_key TEXT NOT NULL, operation_id TEXT NOT NULL,
                PRIMARY KEY(realm_id,source_key),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id));
            INSERT INTO operation_source(realm_id,source_key,operation_id) SELECT realm_id,source_key,operation_id FROM operation_source_saved;
            DROP TABLE operation_source_saved;
            CREATE TABLE economy_entry (
                entry_id INTEGER PRIMARY KEY AUTOINCREMENT, commit_seq INTEGER NOT NULL,
                operation_id TEXT NOT NULL, leg_no INTEGER NOT NULL, recorded_utc_ms INTEGER NOT NULL,
                effective_utc_ms INTEGER NOT NULL, owner_kind INTEGER NOT NULL, owner_id TEXT NOT NULL,
                compartment_kind INTEGER NOT NULL, compartment_key TEXT NOT NULL, instance_key TEXT NOT NULL,
                asset_kind INTEGER NOT NULL, asset_id INTEGER NOT NULL, asset_epoch TEXT NOT NULL,
                delta INTEGER NOT NULL, reason_code INTEGER NOT NULL, leg_kind INTEGER NOT NULL,
                edge_no INTEGER NOT NULL, counterparty_owner_kind INTEGER, counterparty_owner_id TEXT,
                counterparty_compartment_kind INTEGER, counterparty_compartment_key TEXT,
                counterparty_instance_key TEXT, before_fingerprint TEXT, after_fingerprint TEXT,
                source_key TEXT NOT NULL, UNIQUE(operation_id,leg_no),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id));
            INSERT INTO economy_entry(entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key) SELECT entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key FROM economy_entry_saved;
            DROP TABLE economy_entry_saved;
            CREATE INDEX economy_entry_cut ON economy_entry(commit_seq,entry_id);
            """);
        _accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        var reopenedRoles = new RoleStore(reopened);
        var reopenedItems = new ItemStore(reopened);
        Assert.NotNull(EconomyReceiptReader.Read(reopened.DatabasePath, purchase));
        string kill = Guid.NewGuid().ToString("N");
        var tx = reopenedRoles.NewTransaction();
        var current = reopenedItems.Load(_role);
        tx.ItemDelta(_role, current, current);
        progress = reopenedRoles.LoadProgress(_role);
        progress.Exp = 10;
        tx.Progress(_role, progress);
        tx.WithEconomy(new EconomyMutation(kill, "reward-account", _role, "monster-kill", "migration-kill",
            "monster:" + kill, 1_000_001, [], [], [1],
            new EconomyRewardEvidence(1, 0, 1, 10, 1, 0, 1, 0, 10, 0)));
        reopenedRoles.Commit(tx, "test/e04-zero-after-upgrade");
        Assert.Equal(0, EconomyReceiptReader.Read(reopened.DatabasePath, kill)!.LegCount);
        var ledger = EconomyLedgerReader.Read(reopened.DatabasePath, Catalog, "auto", 0, 2);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = reopened.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var foreignKeys = db.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        using var violations = foreignKeys.ExecuteReader();
        Assert.False(violations.Read());
    }

    [Fact]
    public void RealE01LedgerLayoutKeepsEveryLegColumnThroughTheUpgrade()
    {
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);
        string purchase = Guid.NewGuid().ToString("N");
        var buy = _roles.NewTransaction();
        buy.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
        progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 90;
        buy.Progress(_role, progress);
        buy.RoleBlob(_role, "shop", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ShopService.ShopState()));
        buy.WithEconomy(new EconomyMutation(purchase, "reward-account", _role, "npc-shop-buy", "migration-buy",
            "npc:" + purchase, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Item, 3), 2)], [1]));
        _roles.Commit(buy, "test/e04-old-e01");
        var legsBefore = Legs(_accounts.DatabasePath, purchase);
        Assert.NotEmpty(legsBefore);
        Assert.Contains(legsBefore, leg => leg.Split('|')[5].Length == 64 || leg.Split('|')[6].Length == 64); // an item leg with a fingerprint
        FixtureSql.Execute(_accounts, """
            CREATE TABLE operation_source_saved AS SELECT realm_id,source_key,operation_id FROM operation_source;
            CREATE TABLE economy_entry_saved AS SELECT entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,counterparty_compartment_key,counterparty_instance_key,before_fingerprint,after_fingerprint,source_key FROM economy_entry;
            CREATE TABLE operation_receipt_e01 (
                operation_id TEXT PRIMARY KEY, realm_id TEXT NOT NULL, commit_seq INTEGER NOT NULL UNIQUE,
                actor_account TEXT NOT NULL, actor_role INTEGER NOT NULL, command_kind TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL, intent_hash TEXT NOT NULL, intent_payload BLOB NOT NULL,
                source_key TEXT NOT NULL UNIQUE, recorded_utc_ms INTEGER NOT NULL, effective_utc_ms INTEGER NOT NULL,
                outcome BLOB NOT NULL, child_receipts BLOB NOT NULL, before_revisions TEXT NOT NULL,
                after_revisions TEXT NOT NULL, leg_count INTEGER NOT NULL CHECK(leg_count>0));
            INSERT INTO operation_receipt_e01(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,child_receipts,before_revisions,after_revisions,leg_count)
            SELECT operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                   request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,
                   outcome,child_receipts,before_revisions,after_revisions,leg_count
            FROM operation_receipt;
            DROP TABLE operation_source;
            DROP TABLE economy_entry;
            DROP TABLE operation_receipt;
            ALTER TABLE operation_receipt_e01 RENAME TO operation_receipt;
            CREATE TABLE operation_source (
                realm_id TEXT NOT NULL, source_key TEXT NOT NULL, operation_id TEXT NOT NULL,
                PRIMARY KEY(realm_id,source_key),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id));
            INSERT INTO operation_source(realm_id,source_key,operation_id) SELECT realm_id,source_key,operation_id FROM operation_source_saved;
            DROP TABLE operation_source_saved;
            CREATE TABLE economy_entry (
                entry_id INTEGER PRIMARY KEY AUTOINCREMENT, commit_seq INTEGER NOT NULL,
                operation_id TEXT NOT NULL, leg_no INTEGER NOT NULL, recorded_utc_ms INTEGER NOT NULL,
                effective_utc_ms INTEGER NOT NULL, owner_kind INTEGER NOT NULL, owner_id TEXT NOT NULL,
                compartment_kind INTEGER NOT NULL, compartment_key TEXT NOT NULL, instance_key TEXT NOT NULL,
                asset_kind INTEGER NOT NULL, asset_id INTEGER NOT NULL, asset_epoch TEXT NOT NULL,
                delta INTEGER NOT NULL, reason_code INTEGER NOT NULL, leg_kind INTEGER NOT NULL,
                edge_no INTEGER NOT NULL, before_fingerprint TEXT, after_fingerprint TEXT,
                source_key TEXT NOT NULL, UNIQUE(operation_id,leg_no),
                FOREIGN KEY(operation_id) REFERENCES operation_receipt(operation_id));
            INSERT INTO economy_entry(entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,before_fingerprint,after_fingerprint,source_key) SELECT entry_id,commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,before_fingerprint,after_fingerprint,source_key FROM economy_entry_saved;
            DROP TABLE economy_entry_saved;
            CREATE INDEX economy_entry_cut ON economy_entry(commit_seq,entry_id);
            """);
        _accounts.Dispose();
        using var reopened = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        var reopenedRoles = new RoleStore(reopened);
        var reopenedItems = new ItemStore(reopened);
        Assert.NotNull(EconomyReceiptReader.Read(reopened.DatabasePath, purchase));
        Assert.Equal(legsBefore, Legs(reopened.DatabasePath, purchase));
        string kill = Guid.NewGuid().ToString("N");
        var tx = reopenedRoles.NewTransaction();
        var current = reopenedItems.Load(_role);
        tx.ItemDelta(_role, current, current);
        progress = reopenedRoles.LoadProgress(_role);
        progress.Exp = 10;
        tx.Progress(_role, progress);
        tx.WithEconomy(new EconomyMutation(kill, "reward-account", _role, "monster-kill", "migration-kill",
            "monster:" + kill, 1_000_001, [], [], [1],
            new EconomyRewardEvidence(1, 0, 1, 10, 1, 0, 1, 0, 10, 0)));
        reopenedRoles.Commit(tx, "test/e04-zero-after-upgrade");
        Assert.Equal(0, EconomyReceiptReader.Read(reopened.DatabasePath, kill)!.LegCount);
        var ledger = EconomyLedgerReader.Read(reopened.DatabasePath, Catalog, "auto", 0, 2);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = reopened.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var foreignKeys = db.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check";
        using var violations = foreignKeys.ExecuteReader();
        Assert.False(violations.Read());
    }

    private static List<string> Legs(string path, string operation)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT leg_no,delta,reason_code,leg_kind,asset_id,COALESCE(before_fingerprint,''),COALESCE(after_fingerprint,''),
                   source_key,owner_id,compartment_key,instance_key
            FROM economy_entry WHERE operation_id=$op ORDER BY leg_no
            """;
        command.Parameters.AddWithValue("$op", operation);
        using var row = command.ExecuteReader();
        var legs = new List<string>();
        while (row.Read()) legs.Add(string.Join("|", Enumerable.Range(0, row.FieldCount).Select(i => Convert.ToString(row.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return legs;
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
