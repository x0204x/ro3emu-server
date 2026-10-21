using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class EscrowEconomyTransactionTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _sender, _receiver;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [1000000] = EconomyAssetKind.Item });

    public EscrowEconomyTransactionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "e03-core-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory, GameDataDir = SyntheticData.Dir }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _sender = _roles.Create("e03-sender", 1, "E03S" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        _receiver = _roles.Create("e03-receiver", 1, "E03R" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var purse = _roles.LoadProgress(_sender);
        purse.Currency[1] = 100;
        _roles.SaveProgress(_sender, purse);
    }

    [Fact]
    public void CrossOwnerTransferUsesActualLocationsAndBalancedEdge()
    {
        var debit = _roles.LoadProgress(_sender);
        var credit = _roles.LoadProgress(_receiver);
        debit.Currency[1] -= 25;
        credit.Currency[1] = credit.Currency.GetValueOrDefault(1) + 25;
        var transaction = _roles.NewTransaction();
        transaction.Progress(_sender, debit);
        transaction.Progress(_receiver, credit);
        var intent = new EscrowEconomyIntent("e03/test-transfer", "e03-sender", _sender)
            .Evidence(new { From = _sender, To = _receiver, Amount = 25 });
        transaction.WithEscrowEconomy(intent);

        _roles.Commit(transaction, "test/e03-transfer");

        Assert.Equal(75, _roles.LoadProgress(_sender).Currency[1]);
        Assert.Equal(25, _roles.LoadProgress(_receiver).Currency[1]);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        var pair = Assert.Single(ledger.Legs.GroupBy(x => x.EdgeNumber));
        Assert.Equal(2, pair.Count());
        Assert.Equal(0, pair.Sum(x => x.Delta));
        Assert.All(pair, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
    }

    [Theory]
    [InlineData("escrow", "family")]
    [InlineData("family", "escrow")]
    [InlineData("escrow", "semantic")]
    [InlineData("semantic", "escrow")]
    [InlineData("escrow", "event")]
    [InlineData("event", "escrow")]
    [InlineData("semantic", "event")]
    [InlineData("event", "semantic")]
    public void SharedOwnerPlanCannotBindTwoEconomicFamilies(string first, string second)
    {
        var transaction = _roles.NewTransaction();
        transaction.Progress(_sender, _roles.LoadProgress(_sender));
        void Bind(string family)
        {
            switch (family)
            {
                case "escrow":
                    transaction.WithEscrowEconomy(new EscrowEconomyIntent("market/test-bind", "e03-sender", _sender));
                    break;
                case "family":
                    transaction.WithEconomy(new EconomyMutation("bind-family", "e03-sender", _sender,
                        "cd13b/pet-neutral", "bind-family", "bind-family", 1_000, [], [], [1]));
                    break;
                case "semantic":
                    transaction.WithEconomy(new EconomyEnvelope("bind-semantic", "e03-sender", _sender,
                        "guild-test-bind", "bind-semantic", "bind-semantic", 1_000, [], []));
                    break;
                case "event":
                    transaction.WithEventEconomy(new EventEconomyMutation(Catalog, "bind-event", "e03-sender", _sender,
                        "event/test-bind", "bind-event", 1_000, [], [], [], [1]));
                    break;
                default: throw new ArgumentException(family);
            }
        }
        Bind(first);
        Assert.Throws<InvalidOperationException>(() => Bind(second));
        Assert.Equal(100, _roles.LoadProgress(_sender).Currency[1]);
    }

    [Fact]
    public void DuplicateFinancialMailAppendKeepsReceiptRevisionAtCommittedNoOpValue()
    {
        string deliveryKey = "e03-noop-" + Guid.NewGuid().ToString("N");
        var letter = new MailService.Letter { DeliveryKey = deliveryKey };
        var seed = _roles.NewTransaction();
        seed.Add(new FinancialMailAppendPlan(_receiver, deliveryKey, letter, 1_000));
        _roles.Commit(seed, "test/e03-mail-seed");
        var before = _roles.ReadRevisionVector(_receiver);

        var transaction = _roles.NewTransaction();
        transaction.Add(new FinancialMailAppendPlan(_receiver, deliveryKey, letter, 1_001));
        var intent = new EscrowEconomyIntent("e03/test-duplicate-mail", "e03-receiver", _receiver);
        transaction.WithEscrowEconomy(intent);
        var committed = _roles.Commit(transaction, "test/e03-duplicate-mail");

        var after = _roles.ReadRevisionVector(_receiver);
        var receipt = Assert.IsType<EconomyPersistedReceipt>(
            EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
        using var beforeJson = JsonDocument.Parse(receipt.BeforeRevisions);
        using var afterJson = JsonDocument.Parse(receipt.AfterRevisions);
        string roleKey = _receiver.ToString(System.Globalization.CultureInfo.InvariantCulture);
        long recordedBefore = beforeJson.RootElement.GetProperty(roleKey).GetProperty("parts")
            .GetProperty("mailbox").GetInt64();
        long recordedAfter = afterJson.RootElement.GetProperty(roleKey).GetProperty("parts")
            .GetProperty("mailbox").GetInt64();
        Assert.Equal(0, (int)Assert.Single(committed)!);
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(before.Revision("mailbox"), recordedBefore);
        Assert.Equal(after.Revision("mailbox"), recordedAfter);
        Assert.Equal(recordedBefore, recordedAfter);
        Assert.True(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);

        var forged = JsonNode.Parse(receipt.AfterRevisions)!;
        forged[roleKey]!["parts"]!["mailbox"] = recordedAfter + 1;
        FixtureSql.Execute(_accounts,
            $"UPDATE operation_receipt SET after_revisions='{forged.ToJsonString()}' " +
            $"WHERE operation_id='{intent.OperationId}'");
        var altered = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains(
            "E03 role revision advancement differs from typed child mutations", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckerRejectsSuppressedCommittedRoleRevision()
    {
        var before = _roles.ReadRevisionVector(_receiver);
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(_receiver, "bag_state", JsonSerializer.SerializeToUtf8Bytes(new { }));
        var intent = new EscrowEconomyIntent("e03/test-role-revision", "e03-receiver", _receiver);
        transaction.WithEscrowEconomy(intent);
        var committed = _roles.Commit(transaction, "test/e03-role-revision");

        var after = _roles.ReadRevisionVector(_receiver);
        var receipt = Assert.IsType<EconomyPersistedReceipt>(
            EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
        Assert.Equal(1, (int)Assert.Single(committed)!);
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(before.Revision("bag_state") + 1, after.Revision("bag_state"));
        Assert.True(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);

        FixtureSql.Execute(_accounts,
            $"UPDATE operation_receipt SET after_revisions=before_revisions " +
            $"WHERE operation_id='{intent.OperationId}'");
        var altered = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains(
            "E03 role revision advancement differs from typed child mutations", StringComparison.Ordinal));
    }

    [Fact]
    public void OmittedSourceCannotCreateItemAndRollsBackChildren()
    {
        var transaction = _roles.NewTransaction();
        transaction.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 2, Pack = 1, Sort = 1 }]);
        var intent = new EscrowEconomyIntent("e03/test-omitted", "e03-receiver", _receiver);
        transaction.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => _roles.Commit(transaction, "test/e03-omitted"));
        Assert.Empty(_items.Load(_receiver));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
    }

    [Fact]
    public void UnbalancedTransferRollsBackBothRolePurses()
    {
        var debit = _roles.LoadProgress(_sender);
        var credit = _roles.LoadProgress(_receiver);
        debit.Currency[1] -= 25;
        credit.Currency[1] = credit.Currency.GetValueOrDefault(1) + 24;
        var transaction = _roles.NewTransaction();
        transaction.Progress(_sender, debit);
        transaction.Progress(_receiver, credit);
        var intent = new EscrowEconomyIntent("e03/test-unbalanced", "e03-sender", _sender);
        transaction.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => _roles.Commit(transaction, "test/e03-unbalanced"));
        Assert.Equal(100, _roles.LoadProgress(_sender).Currency[1]);
        Assert.Equal(0, _roles.LoadProgress(_receiver).Currency.GetValueOrDefault(1));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalAuthorizationWithWrongOwnerOrCompartmentRollsBack(bool wrongOwner)
    {
        var intent = new EscrowEconomyIntent("e03/test-wrong-location", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward,
                wrongOwner ? EconomyCompartment.Inventory : EconomyCompartment.Mail,
                new(EconomyOwnerKind.Role, (wrongOwner ? _sender : _receiver).ToString()));
        var transaction = _roles.NewTransaction();
        transaction.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        transaction.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => _roles.Commit(transaction, "test/e03-wrong-location"));
        Assert.Empty(_items.Load(_receiver));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
    }

    [Fact]
    public void LostAcknowledgementResolvesTypedIdsAndReplayWithoutSecondGrant()
    {
        var intent = new EscrowEconomyIntent("e03/test-source", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 2, EconomyReason.QuestReward)
            .Evidence(new { Award = 1000000, Count = 2 });
        RoleItemPlanBuilder NewPlan()
        {
            var transaction = _roles.NewTransaction();
            transaction.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 2, Pack = 1, Sort = 1 }]);
            transaction.WithEscrowEconomy(intent);
            return transaction;
        }
        var first = NewPlan();
        _accounts.FailNextEconomyAcknowledgementForTest();
        var committed = _roles.Commit(first, "test/e03-lost-ack");
        var replay = _roles.Commit(NewPlan(), "test/e03-replay");

        Assert.Equal((long[])committed[0]!, (long[])replay[0]!);
        Assert.Equal(2, Assert.Single(_items.Load(_receiver)).Stack);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        Assert.Single(ledger.Receipts);
        Assert.Single(ledger.Legs);
    }

    [Fact]
    public void LedgerInsertFailureRollsBackItemReceiptAndWatermark()
    {
        var intent = new EscrowEconomyIntent("e03/test-ledger-fault", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward);
        var transaction = _roles.NewTransaction();
        transaction.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        transaction.WithEscrowEconomy(intent);
        FixtureSql.Execute(_accounts, "CREATE TRIGGER reject_e03_leg BEFORE INSERT ON economy_entry BEGIN SELECT RAISE(ABORT,'e03-last-leg'); END");
        Assert.Throws<SqliteException>(() => _roles.Commit(transaction, "test/e03-ledger-fault"));
        FixtureSql.Execute(_accounts, "DROP TRIGGER reject_e03_leg");

        Assert.Empty(_items.Load(_receiver));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
        Assert.Empty(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 0).Receipts);
    }

    [Fact]
    public void CheckerRejectsExternalLegMovedOutsideAuthorizedOwner()
    {
        var intent = new EscrowEconomyIntent("e03/test-owner-evidence", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward,
                EconomyCompartment.Inventory, new(EconomyOwnerKind.Role, _receiver.ToString()));
        var transaction = _roles.NewTransaction();
        transaction.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        transaction.WithEscrowEconomy(intent);
        _roles.Commit(transaction, "test/e03-owner-evidence");

        Assert.True(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);
        FixtureSql.Execute(_accounts,
            $"UPDATE economy_entry SET owner_id='{_sender}' WHERE operation_id='{intent.OperationId}'");
        var altered = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains("authorization cannot cover", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckerRejectsTamperedItemTransferFingerprint()
    {
        var grant = _roles.NewTransaction();
        grant.Items(_sender, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        grant.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-transfer-seed", "e03-sender", _sender)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward));
        _roles.Commit(grant, "test/e03-transfer-seed");

        var transfer = _roles.NewTransaction();
        transfer.Items(_sender, []);
        transfer.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        var intent = new EscrowEconomyIntent("e03/test-item-transfer", "e03-sender", _sender);
        transfer.WithEscrowEconomy(intent);
        _roles.Commit(transfer, "test/e03-item-transfer");

        Assert.True(EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2).CompleteHistory);
        FixtureSql.Execute(_accounts,
            $"UPDATE economy_entry SET after_fingerprint='{new string('A', 64)}' " +
            $"WHERE operation_id='{intent.OperationId}' AND delta>0");
        var altered = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains("opposite counterparty", StringComparison.Ordinal));
    }

    [Fact]
    public void AbstractMailAttachmentCanRealizeRolledEquipmentWithExactMetadataEdge()
    {
        var box = new MailService.Mailbox
        {
            Letters = [new MailService.Letter
        {
            Id = "realize-1", Attachments = [new MailService.Attachment(1000000, 1, 1)]
        }]
        };
        var delivery = _roles.NewTransaction();
        delivery.RoleBlob(_receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        delivery.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mail-abstract", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.GmGrant));
        _roles.Commit(delivery, "test/e03-mail-abstract");

        box.Letters[0].Claimed = true;
        var claim = _roles.NewTransaction();
        claim.RoleBlob(_receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        claim.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1,
            BindType = 1, Attrs = new() { [30] = 7 } }]);
        var intent = new EscrowEconomyIntent("e03/test-mail-realize", "e03-receiver", _receiver,
            transferReason: EconomyReason.MailClaim)
            .RealizeMailAttachment(new(EconomyAssetKind.Item, 1000000), 1, _receiver, "realize-1", 0, 1);
        claim.WithEscrowEconomy(intent);
        _roles.Commit(claim, "test/e03-mail-realize");

        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        var legs = ledger.Legs.Where(x => x.OperationId == intent.OperationId).ToArray();
        var source = Assert.Single(legs, x => x.Kind == EconomyLegKind.Transfer && x.Delta < 0);
        var destination = Assert.Single(legs, x => x.Kind == EconomyLegKind.Transfer && x.Delta > 0);
        var metadata = Assert.Single(legs, x => x.Kind == EconomyLegKind.Metadata);
        Assert.Equal(source.BeforeFingerprint, destination.AfterFingerprint);
        Assert.Equal(destination.AfterFingerprint, metadata.BeforeFingerprint);
        Assert.NotEqual(metadata.BeforeFingerprint, metadata.AfterFingerprint);
        Assert.Equal(7, Assert.Single(_items.Load(_receiver)).Attrs![30]);

        FixtureSql.Execute(_accounts,
            $"UPDATE economy_entry SET before_fingerprint='{new string('A', 64)}' " +
            $"WHERE operation_id='{intent.OperationId}' AND leg_kind={(int)EconomyLegKind.Metadata}");
        var altered = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains("mail attachment realization", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactMailItemCannotSilentlyChangeAttributesDuringClaim()
    {
        var box = new MailService.Mailbox
        {
            Letters = [new MailService.Letter
        {
            Id = "exact-1", Items = [new InvItem { Entry = 1000000, Stack = 1, Pack = 1,
                Attrs = new() { [30] = 3 } }]
        }]
        };
        var delivery = _roles.NewTransaction();
        delivery.RoleBlob(_receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        delivery.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mail-exact", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.GmGrant));
        _roles.Commit(delivery, "test/e03-mail-exact");

        box.Letters[0].Claimed = true;
        var claim = _roles.NewTransaction();
        claim.RoleBlob(_receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        claim.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1,
            Attrs = new() { [30] = 4 } }]);
        var intent = new EscrowEconomyIntent("e03/test-mail-exact-claim", "e03-receiver", _receiver,
            transferReason: EconomyReason.MailClaim);
        claim.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => _roles.Commit(claim, "test/e03-mail-exact-claim"));
        Assert.Empty(_items.Load(_receiver));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, intent.OperationId));
    }

    [Fact]
    public void MixedBindingMailClaimsExistingPlainStackAndNewRolledStack()
    {
        var seed = _roles.NewTransaction();
        seed.Items(_receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        seed.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mixed-seed", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.GmGrant));
        _roles.Commit(seed, "test/e03-mixed-seed");
        var existing = Assert.Single(_items.Load(_receiver));

        var box = new MailService.Mailbox
        {
            Letters = [new MailService.Letter
        {
            Id = "mixed-1", Attachments = [new(1000000, 1, 0), new(1000000, 1, 1)]
        }]
        };
        var delivery = _roles.NewTransaction();
        delivery.RoleBlob(_receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        delivery.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mixed-delivery", "e03-receiver", _receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 2, EconomyReason.GmGrant));
        _roles.Commit(delivery, "test/e03-mixed-delivery");

        box.Letters[0].Claimed = true;
        var claim = _roles.NewTransaction();
        claim.RoleBlob(_receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        claim.Items(_receiver, [new InvItem { Guid = existing.Guid, Entry = 1000000, Stack = 2,
            Pack = 1, Sort = 1 }, new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 2,
            BindType = 1, Attrs = new() { [30] = 11 } }]);
        var intent = new EscrowEconomyIntent("e03/test-mixed-claim", "e03-receiver", _receiver,
            transferReason: EconomyReason.MailClaim)
            .RealizeMailAttachment(new(EconomyAssetKind.Item, 1000000), 1, _receiver, "mixed-1", 0, 0)
            .RealizeMailAttachment(new(EconomyAssetKind.Item, 1000000), 1, _receiver, "mixed-1", 1, 1);
        claim.WithEscrowEconomy(intent);
        _roles.Commit(claim, "test/e03-mixed-claim");

        var stored = _items.Load(_receiver);
        Assert.Equal(2, stored.Single(x => x.BindType == 0).Stack);
        Assert.Equal(11, stored.Single(x => x.BindType == 1).Attrs![30]);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 3);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
    }

    public void Dispose()
    {
        _accounts.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
