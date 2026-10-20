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
    private readonly string directory;
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly ItemStore items;
    private readonly long sender, receiver;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [1000000] = EconomyAssetKind.Item });

    public EscrowEconomyTransactionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        directory = Path.Combine(root, ".loadtest-temp", "e03-core-" + Guid.NewGuid().ToString("N"));
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory, GameDataDir = SyntheticData.Dir }));
        roles = new RoleStore(accounts);
        items = new ItemStore(accounts);
        sender = roles.Create("e03-sender", 1, "E03S" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        receiver = roles.Create("e03-receiver", 1, "E03R" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var purse = roles.LoadProgress(sender);
        purse.Currency[1] = 100;
        roles.SaveProgress(sender, purse);
    }

    [Fact]
    public void CrossOwnerTransferUsesActualLocationsAndBalancedEdge()
    {
        var debit = roles.LoadProgress(sender);
        var credit = roles.LoadProgress(receiver);
        debit.Currency[1] -= 25;
        credit.Currency[1] = credit.Currency.GetValueOrDefault(1) + 25;
        var transaction = roles.NewTransaction();
        transaction.Progress(sender, debit);
        transaction.Progress(receiver, credit);
        var intent = new EscrowEconomyIntent("e03/test-transfer", "e03-sender", sender)
            .Evidence(new { From = sender, To = receiver, Amount = 25 });
        transaction.WithEscrowEconomy(intent);

        roles.Commit(transaction, "test/e03-transfer");

        Assert.Equal(75, roles.LoadProgress(sender).Currency[1]);
        Assert.Equal(25, roles.LoadProgress(receiver).Currency[1]);
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
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
        var transaction = roles.NewTransaction();
        transaction.Progress(sender, roles.LoadProgress(sender));
        void Bind(string family)
        {
            switch (family)
            {
                case "escrow":
                    transaction.WithEscrowEconomy(new EscrowEconomyIntent("market/test-bind", "e03-sender", sender));
                    break;
                case "family":
                    transaction.WithEconomy(new EconomyMutation("bind-family", "e03-sender", sender,
                        "cd13b/pet-neutral", "bind-family", "bind-family", 1_000, [], [], [1]));
                    break;
                case "semantic":
                    transaction.WithEconomy(new EconomyEnvelope("bind-semantic", "e03-sender", sender,
                        "guild-test-bind", "bind-semantic", "bind-semantic", 1_000, [], []));
                    break;
                case "event":
                    transaction.WithEventEconomy(new EventEconomyMutation(Catalog, "bind-event", "e03-sender", sender,
                        "event/test-bind", "bind-event", 1_000, [], [], [], [1]));
                    break;
                default: throw new ArgumentException(family);
            }
        }
        Bind(first);
        Assert.Throws<InvalidOperationException>(() => Bind(second));
        Assert.Equal(100, roles.LoadProgress(sender).Currency[1]);
    }

    [Fact]
    public void DuplicateFinancialMailAppendKeepsReceiptRevisionAtCommittedNoOpValue()
    {
        string deliveryKey = "e03-noop-" + Guid.NewGuid().ToString("N");
        var letter = new MailService.Letter { DeliveryKey = deliveryKey };
        var seed = roles.NewTransaction();
        seed.Add(new FinancialMailAppendPlan(receiver, deliveryKey, letter, 1_000));
        roles.Commit(seed, "test/e03-mail-seed");
        var before = roles.ReadRevisionVector(receiver);

        var transaction = roles.NewTransaction();
        transaction.Add(new FinancialMailAppendPlan(receiver, deliveryKey, letter, 1_001));
        var intent = new EscrowEconomyIntent("e03/test-duplicate-mail", "e03-receiver", receiver);
        transaction.WithEscrowEconomy(intent);
        var committed = roles.Commit(transaction, "test/e03-duplicate-mail");

        var after = roles.ReadRevisionVector(receiver);
        var receipt = Assert.IsType<EconomyPersistedReceipt>(
            EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
        using var beforeJson = JsonDocument.Parse(receipt.BeforeRevisions);
        using var afterJson = JsonDocument.Parse(receipt.AfterRevisions);
        string roleKey = receiver.ToString(System.Globalization.CultureInfo.InvariantCulture);
        long recordedBefore = beforeJson.RootElement.GetProperty(roleKey).GetProperty("parts")
            .GetProperty("mailbox").GetInt64();
        long recordedAfter = afterJson.RootElement.GetProperty(roleKey).GetProperty("parts")
            .GetProperty("mailbox").GetInt64();
        Assert.Equal(0, (int)Assert.Single(committed)!);
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(before.Revision("mailbox"), recordedBefore);
        Assert.Equal(after.Revision("mailbox"), recordedAfter);
        Assert.Equal(recordedBefore, recordedAfter);
        Assert.True(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);

        var forged = JsonNode.Parse(receipt.AfterRevisions)!;
        forged[roleKey]!["parts"]!["mailbox"] = recordedAfter + 1;
        FixtureSql.Execute(accounts,
            $"UPDATE operation_receipt SET after_revisions='{forged.ToJsonString()}' " +
            $"WHERE operation_id='{intent.OperationId}'");
        var altered = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains(
            "E03 role revision advancement differs from typed child mutations", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckerRejectsSuppressedCommittedRoleRevision()
    {
        var before = roles.ReadRevisionVector(receiver);
        var transaction = roles.NewTransaction();
        transaction.RoleBlob(receiver, "bag_state", JsonSerializer.SerializeToUtf8Bytes(new { }));
        var intent = new EscrowEconomyIntent("e03/test-role-revision", "e03-receiver", receiver);
        transaction.WithEscrowEconomy(intent);
        var committed = roles.Commit(transaction, "test/e03-role-revision");

        var after = roles.ReadRevisionVector(receiver);
        var receipt = Assert.IsType<EconomyPersistedReceipt>(
            EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
        Assert.Equal(1, (int)Assert.Single(committed)!);
        Assert.Equal(0, receipt.LegCount);
        Assert.Equal(before.Revision("bag_state") + 1, after.Revision("bag_state"));
        Assert.True(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);

        FixtureSql.Execute(accounts,
            $"UPDATE operation_receipt SET after_revisions=before_revisions " +
            $"WHERE operation_id='{intent.OperationId}'");
        var altered = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains(
            "E03 role revision advancement differs from typed child mutations", StringComparison.Ordinal));
    }

    [Fact]
    public void OmittedSourceCannotCreateItemAndRollsBackChildren()
    {
        var transaction = roles.NewTransaction();
        transaction.Items(receiver, [new InvItem { Entry = 1000000, Stack = 2, Pack = 1, Sort = 1 }]);
        var intent = new EscrowEconomyIntent("e03/test-omitted", "e03-receiver", receiver);
        transaction.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => roles.Commit(transaction, "test/e03-omitted"));
        Assert.Empty(items.Load(receiver));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
    }

    [Fact]
    public void UnbalancedTransferRollsBackBothRolePurses()
    {
        var debit = roles.LoadProgress(sender);
        var credit = roles.LoadProgress(receiver);
        debit.Currency[1] -= 25;
        credit.Currency[1] = credit.Currency.GetValueOrDefault(1) + 24;
        var transaction = roles.NewTransaction();
        transaction.Progress(sender, debit);
        transaction.Progress(receiver, credit);
        var intent = new EscrowEconomyIntent("e03/test-unbalanced", "e03-sender", sender);
        transaction.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => roles.Commit(transaction, "test/e03-unbalanced"));
        Assert.Equal(100, roles.LoadProgress(sender).Currency[1]);
        Assert.Equal(0, roles.LoadProgress(receiver).Currency.GetValueOrDefault(1));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalAuthorizationWithWrongOwnerOrCompartmentRollsBack(bool wrongOwner)
    {
        var intent = new EscrowEconomyIntent("e03/test-wrong-location", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward,
                wrongOwner ? EconomyCompartment.Inventory : EconomyCompartment.Mail,
                new(EconomyOwnerKind.Role, (wrongOwner ? sender : receiver).ToString()));
        var transaction = roles.NewTransaction();
        transaction.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        transaction.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => roles.Commit(transaction, "test/e03-wrong-location"));
        Assert.Empty(items.Load(receiver));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
    }

    [Fact]
    public void LostAcknowledgementResolvesTypedIdsAndReplayWithoutSecondGrant()
    {
        var intent = new EscrowEconomyIntent("e03/test-source", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 2, EconomyReason.QuestReward)
            .Evidence(new { Award = 1000000, Count = 2 });
        RoleItemPlanBuilder NewPlan()
        {
            var transaction = roles.NewTransaction();
            transaction.Items(receiver, [new InvItem { Entry = 1000000, Stack = 2, Pack = 1, Sort = 1 }]);
            transaction.WithEscrowEconomy(intent);
            return transaction;
        }
        var first = NewPlan();
        accounts.FailNextEconomyAcknowledgementForTest();
        var committed = roles.Commit(first, "test/e03-lost-ack");
        var replay = roles.Commit(NewPlan(), "test/e03-replay");

        Assert.Equal((long[])committed[0]!, (long[])replay[0]!);
        Assert.Equal(2, Assert.Single(items.Load(receiver)).Stack);
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        Assert.Single(ledger.Receipts);
        Assert.Single(ledger.Legs);
    }

    [Fact]
    public void LedgerInsertFailureRollsBackItemReceiptAndWatermark()
    {
        var intent = new EscrowEconomyIntent("e03/test-ledger-fault", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward);
        var transaction = roles.NewTransaction();
        transaction.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        transaction.WithEscrowEconomy(intent);
        FixtureSql.Execute(accounts, "CREATE TRIGGER reject_e03_leg BEFORE INSERT ON economy_entry BEGIN SELECT RAISE(ABORT,'e03-last-leg'); END");
        Assert.Throws<SqliteException>(() => roles.Commit(transaction, "test/e03-ledger-fault"));
        FixtureSql.Execute(accounts, "DROP TRIGGER reject_e03_leg");

        Assert.Empty(items.Load(receiver));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
        Assert.Empty(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 0).Receipts);
    }

    [Fact]
    public void CheckerRejectsExternalLegMovedOutsideAuthorizedOwner()
    {
        var intent = new EscrowEconomyIntent("e03/test-owner-evidence", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward,
                EconomyCompartment.Inventory, new(EconomyOwnerKind.Role, receiver.ToString()));
        var transaction = roles.NewTransaction();
        transaction.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        transaction.WithEscrowEconomy(intent);
        roles.Commit(transaction, "test/e03-owner-evidence");

        Assert.True(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1).CompleteHistory);
        FixtureSql.Execute(accounts,
            $"UPDATE economy_entry SET owner_id='{sender}' WHERE operation_id='{intent.OperationId}'");
        var altered = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.False(altered.CompleteHistory);
        Assert.Contains(altered.Issues, x => x.Detail.Contains("authorization cannot cover", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckerRejectsTamperedItemTransferFingerprint()
    {
        var grant = roles.NewTransaction();
        grant.Items(sender, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        grant.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-transfer-seed", "e03-sender", sender)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.QuestReward));
        roles.Commit(grant, "test/e03-transfer-seed");

        var transfer = roles.NewTransaction();
        transfer.Items(sender, []);
        transfer.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        var intent = new EscrowEconomyIntent("e03/test-item-transfer", "e03-sender", sender);
        transfer.WithEscrowEconomy(intent);
        roles.Commit(transfer, "test/e03-item-transfer");

        Assert.True(EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 2).CompleteHistory);
        FixtureSql.Execute(accounts,
            $"UPDATE economy_entry SET after_fingerprint='{new string('A', 64)}' " +
            $"WHERE operation_id='{intent.OperationId}' AND delta>0");
        var altered = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 2);
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
        var delivery = roles.NewTransaction();
        delivery.RoleBlob(receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        delivery.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mail-abstract", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.GmGrant));
        roles.Commit(delivery, "test/e03-mail-abstract");

        box.Letters[0].Claimed = true;
        var claim = roles.NewTransaction();
        claim.RoleBlob(receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        claim.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1,
            BindType = 1, Attrs = new() { [30] = 7 } }]);
        var intent = new EscrowEconomyIntent("e03/test-mail-realize", "e03-receiver", receiver,
            transferReason: EconomyReason.MailClaim)
            .RealizeMailAttachment(new(EconomyAssetKind.Item, 1000000), 1, receiver, "realize-1", 0, 1);
        claim.WithEscrowEconomy(intent);
        roles.Commit(claim, "test/e03-mail-realize");

        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        var legs = ledger.Legs.Where(x => x.OperationId == intent.OperationId).ToArray();
        var source = Assert.Single(legs, x => x.Kind == EconomyLegKind.Transfer && x.Delta < 0);
        var destination = Assert.Single(legs, x => x.Kind == EconomyLegKind.Transfer && x.Delta > 0);
        var metadata = Assert.Single(legs, x => x.Kind == EconomyLegKind.Metadata);
        Assert.Equal(source.BeforeFingerprint, destination.AfterFingerprint);
        Assert.Equal(destination.AfterFingerprint, metadata.BeforeFingerprint);
        Assert.NotEqual(metadata.BeforeFingerprint, metadata.AfterFingerprint);
        Assert.Equal(7, Assert.Single(items.Load(receiver)).Attrs![30]);

        FixtureSql.Execute(accounts,
            $"UPDATE economy_entry SET before_fingerprint='{new string('A', 64)}' " +
            $"WHERE operation_id='{intent.OperationId}' AND leg_kind={(int)EconomyLegKind.Metadata}");
        var altered = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 2);
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
        var delivery = roles.NewTransaction();
        delivery.RoleBlob(receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        delivery.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mail-exact", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.GmGrant));
        roles.Commit(delivery, "test/e03-mail-exact");

        box.Letters[0].Claimed = true;
        var claim = roles.NewTransaction();
        claim.RoleBlob(receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        claim.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1,
            Attrs = new() { [30] = 4 } }]);
        var intent = new EscrowEconomyIntent("e03/test-mail-exact-claim", "e03-receiver", receiver,
            transferReason: EconomyReason.MailClaim);
        claim.WithEscrowEconomy(intent);

        Assert.Throws<InvalidOperationException>(() => roles.Commit(claim, "test/e03-mail-exact-claim"));
        Assert.Empty(items.Load(receiver));
        Assert.Null(EconomyReceiptReader.Read(accounts.DatabasePath, intent.OperationId));
    }

    [Fact]
    public void MixedBindingMailClaimsExistingPlainStackAndNewRolledStack()
    {
        var seed = roles.NewTransaction();
        seed.Items(receiver, [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        seed.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mixed-seed", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 1, EconomyReason.GmGrant));
        roles.Commit(seed, "test/e03-mixed-seed");
        var existing = Assert.Single(items.Load(receiver));

        var box = new MailService.Mailbox
        {
            Letters = [new MailService.Letter
        {
            Id = "mixed-1", Attachments = [new(1000000, 1, 0), new(1000000, 1, 1)]
        }]
        };
        var delivery = roles.NewTransaction();
        delivery.RoleBlob(receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        delivery.WithEscrowEconomy(new EscrowEconomyIntent("e03/test-mixed-delivery", "e03-receiver", receiver)
            .Source(new(EconomyAssetKind.Item, 1000000), 2, EconomyReason.GmGrant));
        roles.Commit(delivery, "test/e03-mixed-delivery");

        box.Letters[0].Claimed = true;
        var claim = roles.NewTransaction();
        claim.RoleBlob(receiver, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        claim.Items(receiver, [new InvItem { Guid = existing.Guid, Entry = 1000000, Stack = 2,
            Pack = 1, Sort = 1 }, new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 2,
            BindType = 1, Attrs = new() { [30] = 11 } }]);
        var intent = new EscrowEconomyIntent("e03/test-mixed-claim", "e03-receiver", receiver,
            transferReason: EconomyReason.MailClaim)
            .RealizeMailAttachment(new(EconomyAssetKind.Item, 1000000), 1, receiver, "mixed-1", 0, 0)
            .RealizeMailAttachment(new(EconomyAssetKind.Item, 1000000), 1, receiver, "mixed-1", 1, 1);
        claim.WithEscrowEconomy(intent);
        roles.Commit(claim, "test/e03-mixed-claim");

        var stored = items.Load(receiver);
        Assert.Equal(2, stored.Single(x => x.BindType == 0).Stack);
        Assert.Equal(11, stored.Single(x => x.BindType == 1).Attrs![30]);
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, Catalog, "auto", 0, 3);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
    }

    public void Dispose()
    {
        accounts.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
