using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Economy;

/// <summary>Read-only manifest for NPC, family, reward and event envelopes in one committed sequence interval.</summary>
public static class EconomyLedgerReader
{
    private sealed record ParsedIntent(IReadOnlyList<(EconomyAsset Asset, bool Cost, BigInteger Amount)> Amounts,
        EconomyRewardEvidence? RewardEvidence);
    public static EconomyLedgerWindow Read(string databasePath, EconomyCatalog catalog, string realmId,
        long fromExclusive, long throughInclusive)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (fromExclusive < 0 || throughInclusive < fromExclusive) throw new ArgumentOutOfRangeException(nameof(fromExclusive));
        if (string.IsNullOrWhiteSpace(realmId)) throw new ArgumentException("Realm identity is required.", nameof(realmId));
        if (!File.Exists(databasePath)) throw new FileNotFoundException("Economy database does not exist.", databasePath);
        var issues = new List<EconomyIssue>();
        using var db = Open(databasePath);
        using var tx = db.BeginTransaction(deferred: true);
        string storedRealm;
        long watermark, floor;
        // The first read (this schema check) pins the database view; the watermark below is read from that same view.
        if (!EconomySchema.Has(db, tx, "economy_meta"))
        {
            issues.Add(new(EconomyIssueKind.Coverage, "ledger/schema", "E01 economy tables are absent."));
            return Empty(realmId, catalog, fromExclusive, throughInclusive, issues);
        }
        using (var meta = Command(db, tx,
            "SELECT realm_id,next_sequence,retained_floor FROM economy_meta WHERE singleton=1"))
        {
            using var row = meta.ExecuteReader();
            if (!row.Read()) throw new InvalidDataException("Missing E01 economy metadata singleton.");
            storedRealm = row.GetString(0); watermark = row.GetInt64(1); floor = row.GetInt64(2);
        }
        if (realmId != "auto" && realmId != storedRealm)
            issues.Add(new(EconomyIssueKind.Corruption, "ledger/realm", "Expected realm ID differs from durable database identity."));
        if (fromExclusive < floor || floor != 0 || throughInclusive > watermark)
            issues.Add(new(EconomyIssueKind.Coverage, "ledger/range", "Requested range is outside the retained committed sequence."));
        var receipts = new List<EconomyReceipt>();
        var prior = new List<EconomySourceClaim>();
        var legs = new List<EconomyLeg>();
        var allTransferLegs = new List<EconomyLeg>();
        var allReceipts = new Dictionary<long, (string Operation, int Count, string Source, long Actor, string Kind)>();
        var intentAmounts = new Dictionary<(string Operation, EconomyAsset Asset, bool Cost), BigInteger>();
        var ledgerAmounts = new Dictionary<(string Operation, EconomyAsset Asset, bool Cost), BigInteger>();
        var escrowReasons = new Dictionary<(string Operation, EconomyAsset Asset, bool Cost, EconomyReason Reason), BigInteger>();
        var escrowLedgerReasons = new Dictionary<(string Operation, EconomyAsset Asset, bool Cost, EconomyReason Reason), BigInteger>();
        var escrowTransfers = new Dictionary<string, EconomyReason>(StringComparer.Ordinal);
        var escrowAuthorizations = new Dictionary<string, EscrowExternalChange[]>(StringComparer.Ordinal);
        var escrowRealizations = new Dictionary<string, EscrowMailRealization[]>(StringComparer.Ordinal);
        var escrowExternalLegs = new Dictionary<string, List<EconomyLeg>>(StringComparer.Ordinal);
        var escrowAllLegs = new Dictionary<string, List<EconomyLeg>>(StringComparer.Ordinal);
        var eventIntents = new Dictionary<string, EventEconomyMutation>(StringComparer.Ordinal);
        var eventOwnerAmounts = new Dictionary<(string Operation, EconomyOwner Owner, EconomyAsset Asset), BigInteger>();
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        bool obligationSchemaChecked = false;
        var e05Intent = new Dictionary<(string Operation, EconomyAsset Asset, EconomyOwner Owner,
            EconomyCompartment Compartment, EconomyLegKind Kind, EconomyReason Reason), BigInteger>();
        var e05Ledger = new Dictionary<(string Operation, EconomyAsset Asset, EconomyOwner Owner,
            EconomyCompartment Compartment, EconomyLegKind Kind, EconomyReason Reason), BigInteger>();
        var e05IntentEdges = new Dictionary<(string Operation, EconomyAsset Asset, EconomyOwner From,
            EconomyCompartment FromPart, EconomyReason FromReason, EconomyOwner To,
            EconomyCompartment ToPart, EconomyReason ToReason), BigInteger>();
        var e05LedgerEdges = new Dictionary<(string Operation, EconomyAsset Asset, EconomyOwner From,
            EconomyCompartment FromPart, EconomyReason FromReason, EconomyOwner To,
            EconomyCompartment ToPart, EconomyReason ToReason), BigInteger>();
        try
        {
            RequireLedgerSchema(db, tx);
            using (var bounds = Command(db, tx, """
                SELECT (SELECT COUNT(*) FROM operation_receipt WHERE commit_seq<=0 OR commit_seq>$watermark),
                       (SELECT COUNT(*) FROM economy_entry WHERE commit_seq<=0 OR commit_seq>$watermark)
                """))
            {
                bounds.Parameters.AddWithValue("$watermark", watermark);
                using var row = bounds.ExecuteReader();
                if (row.Read() && (row.GetInt64(0) != 0 || row.GetInt64(1) != 0))
                    issues.Add(new(EconomyIssueKind.Corruption, "ledger/watermark",
                        "Receipt or leg sequence lies outside the committed metadata watermark."));
            }
            long expectedSequence = 1;
            using (var receipt = Command(db, tx,
                """
                    SELECT commit_seq,operation_id,leg_count,source_key,realm_id,actor_account,actor_role,
                    command_kind,request_fingerprint,intent_hash,outcome,child_receipts,before_revisions,after_revisions,
                    effective_utc_ms,intent_payload
                    FROM operation_receipt WHERE commit_seq<=$through ORDER BY commit_seq
                    """))
            {
                receipt.Parameters.AddWithValue("$through", throughInclusive);
                using var row = receipt.ExecuteReader();
                while (row.Read())
                {
                    long seq = row.GetInt64(0);
                    if (seq != expectedSequence)
                        issues.Add(new(EconomyIssueKind.Corruption, "ledger/sequence/" + expectedSequence,
                            "Committed receipt history has a gap or nonconsecutive sequence."));
                    expectedSequence = seq == long.MaxValue ? long.MaxValue : seq + 1;
                    string op = row.GetString(1), source = row.GetString(3);
                    bool isEvent = row.GetString(7).StartsWith("event/", StringComparison.Ordinal);
                    int count = row.GetInt32(2);
                    string commandKind = row.GetString(7);
                    bool family = EconomyMutation.IsFamilyKind(commandKind) || EconomyMutation.IsProgressionKind(commandKind);
                    bool economic = Economic(commandKind);
                    bool escrow = EscrowEconomyIntent.IsKind(commandKind);
                    if (row.GetString(4) != storedRealm || !allReceipts.TryAdd(seq, (op, count, source, row.GetInt64(6), commandKind)))
                        issues.Add(new(EconomyIssueKind.Corruption, "receipt/" + seq, "Duplicate or foreign realm receipt."));
                    if (string.IsNullOrWhiteSpace(op) || string.IsNullOrWhiteSpace(source) ||
                        (!escrow && string.IsNullOrWhiteSpace(row.GetString(5))) ||
                        (isEvent || escrow ? row.GetInt64(6) < 0 : row.GetInt64(6) <= 0) ||
                        string.IsNullOrWhiteSpace(commandKind) || string.IsNullOrWhiteSpace(row.GetString(8)) ||
                        row.GetString(9).Length != 64 || count < (commandKind == "npc-shop-buy" || isEvent ? 1 : 0) ||
                        (economic || isEvent || escrow) && ((byte[])row.GetValue(10)).Length == 0)
                        issues.Add(new(EconomyIssueKind.Corruption, "receipt/" + op, "Incomplete committed actor, intent, result or leg count."));
                    commands[op] = commandKind;
                    EconomyRewardEvidence? rewardEvidence = null;
                    long? beforeProgressRevision = null, afterProgressRevision = null;
                    try
                    {
                        object?[] typedChildren = escrow
                            ? EscrowEconomyReceiptCodec.Decode((byte[])row.GetValue(11))
                            : EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(11));
                        if (isEvent)
                        {
                            if (typedChildren.Length == 0) throw new InvalidDataException("Event has no typed children.");
                            EventEconomyEvidence.ValidateRevisions(row.GetString(12), row.GetString(13), row.GetInt64(6));
                            var intent = EventEconomyEvidence.ReadIntent(catalog, (byte[])row.GetValue(15), row.GetString(9),
                                op, row.GetString(5), row.GetInt64(6), row.GetString(7), row.GetString(8), source,
                                row.GetInt64(14), (byte[])row.GetValue(10));
                            eventIntents.Add(op, intent);
                            foreach (var quantity in intent.Costs)
                                intentAmounts[(op, quantity.Asset, true)] = intentAmounts.GetValueOrDefault((op, quantity.Asset, true)) + quantity.Quantity;
                            foreach (var quantity in intent.Goods)
                                intentAmounts[(op, quantity.Asset, false)] = intentAmounts.GetValueOrDefault((op, quantity.Asset, false)) + quantity.Quantity;
                        }
                        else if (escrow)
                        {
                            if (!((byte[])row.GetValue(10)).AsSpan().SequenceEqual((byte[])row.GetValue(11)))
                                throw new InvalidDataException("E03 outcome differs from typed children.");
                            var staged = ValidateEscrowIntentPayload((byte[])row.GetValue(15), row.GetString(9),
                                op, row.GetString(5), row.GetInt64(6), commandKind, row.GetString(8), source, row.GetInt64(14));
                            ValidateEscrowRevisions(row.GetString(12), row.GetString(13),
                                staged.ChildRoleWrites, typedChildren);
                            escrowTransfers.Add(op, staged.TransferReason);
                            escrowAuthorizations.Add(op, staged.Changes);
                            escrowRealizations.Add(op, staged.Realizations);
                            foreach (var change in staged.Changes)
                            {
                                var key = (op, change.Asset, change.Kind == EconomyLegKind.Sink, change.Reason);
                                escrowReasons[key] = escrowReasons.GetValueOrDefault(key) + change.Quantity;
                            }
                        }
                        else if (!economic)
                        {
                            if (typedChildren.Length == 0) throw new InvalidDataException("E05d receipt has no typed children.");
                            ValidateEnvelopeRevisions(row.GetString(12), row.GetString(13));
                            var effects = ValidateEnvelopeIntent((byte[])row.GetValue(15), row.GetString(9),
                                op, row.GetString(5), row.GetInt64(6), commandKind, row.GetString(8), source,
                                row.GetInt64(14), (byte[])row.GetValue(10), typedChildren);
                            foreach (var effect in effects)
                            {
                                var key = (op, effect.Asset, effect.Owner, effect.Compartment, effect.Kind, effect.Reason);
                                e05Intent[key] = e05Intent.GetValueOrDefault(key) + effect.Delta;
                            }
                            foreach (var edge in effects.Where(x => x.Kind == EconomyLegKind.Transfer).GroupBy(x => x.EdgeNumber))
                            {
                                var pair = edge.ToArray();
                                if (edge.Key <= 0 || pair.Length != 2 ||
                                    pair.Count(x => x.Delta < 0) != 1 || pair.Count(x => x.Delta > 0) != 1)
                                    throw new InvalidDataException("E05d transfer intent lacks one exact counterparty.");
                                var debit = pair.Single(x => x.Delta < 0);
                                var credit = pair.Single(x => x.Delta > 0);
                                var key = (op, debit.Asset, debit.Owner, debit.Compartment, debit.Reason,
                                    credit.Owner, credit.Compartment, credit.Reason);
                                e05IntentEdges[key] = e05IntentEdges.GetValueOrDefault(key) - debit.Delta;
                            }
                        }
                        else if (commandKind == "dungeon-obligation-earn")
                        {
                            if (typedChildren.Length != 0) throw new InvalidDataException("Earned obligation has child results.");
                        }
                        else if (family)
                        {
                            if (typedChildren.Length is < 1 or > 32 || typedChildren[0] is not long[] familyIds ||
                                familyIds.Any(id => id <= 0) || familyIds.Distinct().Count() != familyIds.Length ||
                                typedChildren.Skip(1).Any(value => value is not int number || number != 1))
                                throw new InvalidDataException("Family typed child result shape is invalid.");
                        }
                        else if (commandKind == "cd13e/daily-reset")
                        {
                            EconomyTransactionPlan.ValidateChildReceipts(typedChildren, -1, commandKind);
                        }
                        else
                        {
                            if (typedChildren.Length < 2 || typedChildren[0] is not long[] ids)
                                throw new InvalidDataException("Economic typed child result shape is invalid.");
                            EconomyTransactionPlan.ValidateChildReceipts(typedChildren, ids.Length, commandKind);
                        }
                        if (economic)
                        {
                            (beforeProgressRevision, afterProgressRevision) = ValidateRevisions(row.GetString(12), row.GetString(13),
                                row.GetInt64(6), commandKind, typedChildren);
                            var parsed = ValidateIntentPayload((byte[])row.GetValue(15), row.GetString(9),
                                op, row.GetString(5), row.GetInt64(6), commandKind, row.GetString(8), source,
                                row.GetInt64(14), (byte[])row.GetValue(10));
                            bool dungeonReceipt = commandKind is "dungeon-reward" or "dungeon-obligation-earn";
                            if (dungeonReceipt && !obligationSchemaChecked)
                            {
                                RequireObligationSchema(db, tx);
                                obligationSchemaChecked = true;
                            }
                            if (commandKind == "dungeon-reward")
                                ValidateDungeonObligation(db, tx, source, row.GetInt64(6), parsed);
                            if (commandKind == "dungeon-obligation-earn")
                                ValidateEarnedObligation(db, tx, source, row.GetInt64(6), seq, throughInclusive, parsed,
                                    (byte[])row.GetValue(15), (byte[])row.GetValue(10));
                            rewardEvidence = parsed.RewardEvidence;
                            foreach (var amount in parsed.Amounts)
                            {
                                var amountKey = (op, amount.Asset, amount.Cost);
                                intentAmounts[amountKey] = intentAmounts.GetValueOrDefault(amountKey) + amount.Amount;
                            }
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or JsonException or EndOfStreamException or KeyNotFoundException or OverflowException or FormatException or ArgumentException or InvalidOperationException)
                    {
                        issues.Add(new(EconomyIssueKind.Corruption, "receipt/" + op, "Invalid typed child result or revision evidence: " + error.Message));
                    }
                    if (seq > fromExclusive) receipts.Add(new EconomyReceipt(seq, op, count, [source])
                    {
                        ActorRoleId = row.GetInt64(6), CommandKind = commandKind, RewardEvidence = rewardEvidence,
                        BeforeProgressRevision = beforeProgressRevision, AfterProgressRevision = afterProgressRevision
                    });
                }
            }
            if (expectedSequence <= throughInclusive)
                issues.Add(new(EconomyIssueKind.Corruption, "ledger/sequence/" + expectedSequence,
                    "Committed receipt history ends before the requested cut."));
            // No-prune E01: certify the entire retained prefix, not just the requested window.
            using (var counts = Command(db, tx, """
                SELECT r.operation_id,r.leg_count,COUNT(e.entry_id),MIN(e.leg_no),MAX(e.leg_no)
                FROM operation_receipt r LEFT JOIN economy_entry e ON e.operation_id=r.operation_id
                WHERE r.commit_seq<=$through GROUP BY r.operation_id,r.leg_count
                """))
            {
                counts.Parameters.AddWithValue("$through", throughInclusive);
                using var row = counts.ExecuteReader();
                while (row.Read())
                {
                    int claimed = row.GetInt32(1);
                    long actual = row.GetInt64(2);
                    if (claimed < 0 || actual != claimed ||
                        actual > 0 && (row.IsDBNull(3) || row.GetInt64(3) != 0 || row.GetInt64(4) != actual - 1))
                        issues.Add(new(EconomyIssueKind.Corruption, "receipt/" + row.GetString(0),
                            "Retained receipt has missing, extra or nonconsecutive ledger legs."));
                }
            }
            using (var orphan = Command(db, tx, """
                SELECT COUNT(*) FROM economy_entry e LEFT JOIN operation_receipt r ON r.operation_id=e.operation_id
                WHERE e.commit_seq<=$through AND (r.operation_id IS NULL OR r.commit_seq!=e.commit_seq)
                """))
            {
                orphan.Parameters.AddWithValue("$through", throughInclusive);
                if ((long)orphan.ExecuteScalar()! != 0)
                    issues.Add(new(EconomyIssueKind.Corruption, "ledger/orphan", "Retained ledger prefix has orphan or wrong-sequence legs."));
            }
            using (var source = Command(db, tx, """
                SELECT s.realm_id,s.source_key,s.operation_id,r.commit_seq
                FROM operation_source s LEFT JOIN operation_receipt r ON r.operation_id=s.operation_id
                ORDER BY s.source_key
                """))
            using (var row = source.ExecuteReader())
                while (row.Read())
                {
                    string key = row.GetString(1), op = row.GetString(2);
                    if (row.GetString(0) != storedRealm || row.IsDBNull(3))
                        issues.Add(new(EconomyIssueKind.Corruption, "source/" + key, "Orphan or foreign realm source claim."));
                    else if (row.GetInt64(3) <= fromExclusive) prior.Add(new(key, op));
                    if (!row.IsDBNull(3) && row.GetInt64(3) <= throughInclusive &&
                        (!allReceipts.TryGetValue(row.GetInt64(3), out var claimed) || claimed.Operation != op || claimed.Source != key))
                        issues.Add(new(EconomyIssueKind.Corruption, "source/" + key, "Source claim does not match its receipt."));
                }
            foreach (var receipt in allReceipts.Values)
            {
                using var source = Command(db, tx, "SELECT COUNT(*) FROM operation_source WHERE realm_id=$realm AND operation_id=$op AND source_key=$key");
                source.Parameters.AddWithValue("$realm", storedRealm);
                source.Parameters.AddWithValue("$op", receipt.Operation);
                source.Parameters.AddWithValue("$key", receipt.Source);
                if ((long)source.ExecuteScalar()! != 1)
                    issues.Add(new(EconomyIssueKind.Corruption, "source/" + receipt.Source, "Receipt has no exact natural-source claim."));
            }
            using (var entry = Command(db, tx, """
                SELECT commit_seq,operation_id,leg_no,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,
                    asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,before_fingerprint,after_fingerprint,
                    source_key,counterparty_owner_kind,counterparty_owner_id,counterparty_compartment_kind,
                    counterparty_compartment_key,counterparty_instance_key
                    FROM economy_entry WHERE commit_seq<=$through ORDER BY commit_seq,leg_no
                """))
            {
                entry.Parameters.AddWithValue("$through", throughInclusive);
                using var row = entry.ExecuteReader();
                while (row.Read())
                {
                    long seq = row.GetInt64(0);
                    string op = row.GetString(1), source = row.GetString(17);
                    var asset = new EconomyAsset((EconomyAssetKind)row.GetInt32(8), row.GetInt64(9), row.GetString(10));
                    var location = new EconomyLocation(new((EconomyOwnerKind)row.GetInt32(3), row.GetString(4)),
                        (EconomyCompartment)row.GetInt32(5), row.GetString(6), row.GetString(7));
                    EconomyLocation? counterparty = row.IsDBNull(18) ? null : new EconomyLocation(
                        new((EconomyOwnerKind)row.GetInt32(18), row.GetString(19)),
                        (EconomyCompartment)row.GetInt32(20), row.GetString(21), row.GetString(22));
                    var leg = new EconomyLeg(seq, op, row.GetInt32(2), (EconomyLegKind)row.GetInt32(13),
                        (EconomyReason)row.GetInt32(12), asset, location, row.GetInt64(11), row.GetInt32(14),
                        Counterparty: counterparty,
                        BeforeFingerprint: row.IsDBNull(15) ? null : row.GetString(15),
                        AfterFingerprint: row.IsDBNull(16) ? null : row.GetString(16), SourceKey: source);
                    if (escrowTransfers.ContainsKey(op))
                    {
                        if (!escrowAllLegs.TryGetValue(op, out var economicLegs))
                            escrowAllLegs.Add(op, economicLegs = []);
                        economicLegs.Add(leg);
                    }
                    bool isEvent = eventIntents.TryGetValue(op, out var eventIntent);
                    if (isEvent)
                    {
                        var ownerKey = (op, location.Owner, asset);
                        eventOwnerAmounts[ownerKey] = eventOwnerAmounts.GetValueOrDefault(ownerKey) + leg.Delta;
                    }
                    if (leg.Kind == EconomyLegKind.Transfer) allTransferLegs.Add(leg);
                    if (seq > fromExclusive) legs.Add(leg);
                    commands.TryGetValue(op, out string? command);
                    bool economic = Economic(command ?? "");
                    bool escrow = EscrowEconomyIntent.IsKind(command ?? "");
                    if (escrow && leg.Kind is EconomyLegKind.Sink or EconomyLegKind.Source)
                    {
                        BigInteger amount = leg.Kind == EconomyLegKind.Sink ? -(BigInteger)leg.Delta : leg.Delta;
                        var reasonKey = (op, asset, leg.Kind == EconomyLegKind.Sink, leg.Reason);
                        escrowLedgerReasons[reasonKey] = escrowLedgerReasons.GetValueOrDefault(reasonKey) + amount;
                        if (!escrowExternalLegs.TryGetValue(op, out var posted))
                            escrowExternalLegs.Add(op, posted = []);
                        posted.Add(leg);
                    }
                    if ((economic || isEvent) && (leg.Kind is EconomyLegKind.Sink or EconomyLegKind.Source ||
                        command == "dungeon-reward" && leg.Kind == EconomyLegKind.Transfer && leg.Delta > 0))
                    {
                        var amountKey = (op, asset, leg.Kind == EconomyLegKind.Sink);
                        ledgerAmounts[amountKey] = ledgerAmounts.GetValueOrDefault(amountKey) +
                            (leg.Kind == EconomyLegKind.Sink ? -(BigInteger)leg.Delta : leg.Delta);
                    }
                    bool knownAsset = asset.Kind switch
                    {
                        EconomyAssetKind.GuildResource => asset.Id is >= 1 and <= 5 && asset.Epoch.Length == 0,
                        EconomyAssetKind.EventResource => asset.Id == EconomyDenominations.EscortGoods && asset.Epoch.Length != 0,
                        _ => catalog.TryAsset(asset.Id, out var classified) && classified == asset
                    };
                    if (!knownAsset && !(isEvent && EventEconomyMutation.ValidAsset(asset)))
                        issues.Add(new(EconomyIssueKind.Coverage, "leg/" + op + "/" + leg.LegNumber, "Ledger denomination is not catalogued."));
                    bool family = allReceipts.TryGetValue(seq, out var receiptOwner) &&
                        (EconomyMutation.IsFamilyKind(receiptOwner.Kind) || EconomyMutation.IsProgressionKind(receiptOwner.Kind));
                    if (isEvent)
                    {
                        if (!EconomyReasonCatalog.Allows(leg.Reason, leg.Kind) ||
                            !(leg.Kind switch
                            {
                                EconomyLegKind.Source => leg.Delta > 0 && leg.EdgeNumber == 0 && leg.Reason == eventIntent!.SourceReason,
                                EconomyLegKind.Sink => leg.Delta < 0 && leg.EdgeNumber == 0 && leg.Reason == eventIntent!.SinkReason,
                                EconomyLegKind.Transfer => leg.Delta != 0 && leg.EdgeNumber > 0 && leg.Reason == eventIntent!.TransferReason,
                                EconomyLegKind.Metadata => leg.Delta == 0 && leg.EdgeNumber == 0 &&
                                    leg.Reason == EconomyReason.ItemMetadata && asset.Kind == EconomyAssetKind.Item &&
                                    leg.BeforeFingerprint != leg.AfterFingerprint,
                                _ => false
                            }))
                            issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                                "Illegal event reason, kind, edge or delta."));
                    }
                    else if (escrow)
                    {
                        bool legal = escrowTransfers.TryGetValue(op, out var escrowTransfer) &&
                            EconomyReasonCatalog.Allows(leg.Reason, leg.Kind) &&
                            (leg.Kind == EconomyLegKind.Source && leg.Delta > 0 ||
                             leg.Kind == EconomyLegKind.Sink && leg.Delta < 0 ||
                             leg.Kind == EconomyLegKind.Transfer && leg.Delta != 0 && leg.EdgeNumber > 0 ||
                             leg.Kind == EconomyLegKind.Metadata && leg.Delta == 0 && leg.EdgeNumber == 0) &&
                            leg.Kind switch
                            {
                                EconomyLegKind.Source or EconomyLegKind.Sink => escrowReasons.ContainsKey(
                                    (op, asset, leg.Kind == EconomyLegKind.Sink, leg.Reason)),
                                EconomyLegKind.Transfer => leg.Reason == escrowTransfer,
                                EconomyLegKind.Metadata => leg.Reason == EconomyReason.ItemMetadata &&
                                    asset.Kind == EconomyAssetKind.Item && leg.BeforeFingerprint != leg.AfterFingerprint,
                                _ => false
                            };
                        if (!legal)
                            issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                                "Illegal E03 reason, kind, edge or delta."));
                    }
                    else if (family)
                    {
                        string familyName = receiptOwner.Kind[6..].Split('-')[0];
                        var reasons = EconomyMutation.IsProgressionKind(receiptOwner.Kind)
                            ? ProgressionEconomyTransactionPlan.Reasons(receiptOwner.Kind)
                            : FamilyReasons(familyName, receiptOwner.Kind);
                        bool legal = EconomyReasonCatalog.Allows(leg.Reason, leg.Kind) &&
                            (leg.Kind == EconomyLegKind.Source && leg.Delta > 0 ||
                             leg.Kind == EconomyLegKind.Sink && leg.Delta < 0 ||
                             leg.Kind == EconomyLegKind.Transfer && leg.Delta != 0 && leg.EdgeNumber > 0 ||
                             leg.Kind == EconomyLegKind.Metadata && leg.Delta == 0 && leg.EdgeNumber == 0) &&
                            leg.Kind switch
                            {
                                EconomyLegKind.Source => leg.Reason == reasons.Goods,
                                EconomyLegKind.Sink => leg.Reason == reasons.Cost,
                                EconomyLegKind.Transfer => leg.Reason == EconomyReason.InventoryRelocation,
                                EconomyLegKind.Metadata => leg.Reason == EconomyReason.ItemMetadata &&
                                    asset.Kind == EconomyAssetKind.Item && leg.BeforeFingerprint != leg.AfterFingerprint,
                                _ => false
                            };
                        if (!legal)
                            issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                                "Illegal command reason, kind, edge or delta."));
                    }
                    else if (economic)
                    {
                        var expectedReason = command switch
                        {
                            "monster-kill" when leg.Kind == EconomyLegKind.Source => EconomyReason.MonsterDrop,
                            "gather-yield" when leg.Kind == EconomyLegKind.Source => EconomyReason.GatherYield,
                            "tower-reward" when leg.Kind == EconomyLegKind.Source => EconomyReason.ActivityReward,
                            "dungeon-obligation-earn" when leg.Kind == EconomyLegKind.Source => EconomyReason.ActivityReward,
                            "dungeon-reward" when leg.Kind == EconomyLegKind.Transfer => EconomyReason.DungeonRewardDelivery,
                            "world-revive" when leg.Kind == EconomyLegKind.Sink => EconomyReason.ItemUse,
                            "npc-shop-buy" when leg.Kind == EconomyLegKind.Source => EconomyReason.NpcShopGoods,
                            "npc-shop-buy" when leg.Kind == EconomyLegKind.Sink => EconomyReason.NpcShopPayment,
                            "cd13e/tower-milestone" when leg.Kind == EconomyLegKind.Source => EconomyReason.ActivityReward,
                            "cd13e/tower-milestone" when leg.Kind == EconomyLegKind.Sink => EconomyReason.ItemUse,
                            "cd13e/grant" when leg.Kind == EconomyLegKind.Source => EconomyReason.OwnedGrant,
                            "cd13e/gm-grant" when leg.Kind == EconomyLegKind.Source => EconomyReason.GmGrant,
                            "cd13e/quest-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.QuestReward,
                            "cd13e/quest-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/achievement-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.AchievementReward,
                            "cd13e/achievement-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/advguild-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.AdventureGroupReward,
                            "cd13e/advguild-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/monsterhunt-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.MonsterHuntReward,
                            "cd13e/monsterhunt-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/guild-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.GuildReward,
                            "cd13e/guild-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/growthclass-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.GrowthClassReward,
                            "cd13e/growthclass-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/library-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.LibraryReward,
                            "cd13e/library-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/firstheart-award" when leg.Kind == EconomyLegKind.Source => EconomyReason.FirstHeartReward,
                            "cd13e/firstheart-award" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/daily-stage" when leg.Kind == EconomyLegKind.Source => EconomyReason.DailyReward,
                            "cd13e/daily-stage" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/journey-victory" when leg.Kind == EconomyLegKind.Source => EconomyReason.ActivityReward,
                            "cd13e/journey-victory" when leg.Kind == EconomyLegKind.Sink => EconomyReason.OwnedAwardCost,
                            "cd13e/daily-reset" when leg.Kind == EconomyLegKind.Sink => EconomyReason.DailyReset,
                            _ => EconomyReason.Unknown
                        };
                        if (!EconomyReasonCatalog.Allows(leg.Reason, leg.Kind) ||
                            leg.Kind == EconomyLegKind.Source && leg.Delta <= 0 ||
                            leg.Kind == EconomyLegKind.Sink && leg.Delta >= 0 ||
                            leg.Reason != expectedReason ||
                            leg.Kind is not (EconomyLegKind.Source or EconomyLegKind.Sink or EconomyLegKind.Transfer))
                            issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber, "Illegal economic reason, kind or delta."));
                    }
                    else if (!EconomyReasonCatalog.Allows(leg.Reason, leg.Kind) ||
                        leg.Kind == EconomyLegKind.Source && leg.Delta <= 0 ||
                        leg.Kind == EconomyLegKind.Sink && leg.Delta >= 0 ||
                        leg.Kind == EconomyLegKind.Transfer && (leg.Delta == 0 || leg.EdgeNumber <= 0) ||
                        leg.Kind == EconomyLegKind.Metadata && leg.Delta != 0)
                        issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                            "Illegal semantic reason, kind or delta."));
                    if (asset.Kind == EconomyAssetKind.Item &&
                        (leg.Delta < 0 && string.IsNullOrEmpty(leg.BeforeFingerprint) ||
                         leg.Delta > 0 && string.IsNullOrEmpty(leg.AfterFingerprint) ||
                         leg.Kind == EconomyLegKind.Metadata &&
                            (string.IsNullOrEmpty(leg.BeforeFingerprint) || string.IsNullOrEmpty(leg.AfterFingerprint))))
                        issues.Add(new(EconomyIssueKind.Coverage, "leg/" + op + "/" + leg.LegNumber,
                            "Item leg lacks before/after fingerprint evidence."));
                    if (asset.Kind == EconomyAssetKind.Item &&
                        (!ValidFingerprint(leg.BeforeFingerprint) || !ValidFingerprint(leg.AfterFingerprint)) ||
                        economic && location.Compartment == EconomyCompartment.Pending &&
                            (command is not ("dungeon-obligation-earn" or "dungeon-reward") ||
                             !location.Key.StartsWith("dungeon/", StringComparison.Ordinal) ||
                             location.Instance.Length != 0) ||
                        (!economic || location.Compartment != EconomyCompartment.Pending) &&
                        asset.Kind == EconomyAssetKind.Currency &&
                        (leg.BeforeFingerprint is not null || leg.AfterFingerprint is not null))
                        issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                            "Noncanonical item fingerprint or currency fingerprint evidence."));
                    if (!allReceipts.TryGetValue(seq, out var owner) || owner.Operation != op || owner.Source != source)
                        issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber, "Leg is not linked to matching receipt/source."));
                    else if (command == "cd13c/season-reissue" && location.Compartment == EconomyCompartment.Mail)
                    {
                        if (location.Owner.Kind != EconomyOwnerKind.Role || location.Owner.Id != owner.Actor.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                            !location.Key.StartsWith($"role/{owner.Actor}/mailbox/", StringComparison.Ordinal) ||
                            leg.Kind != EconomyLegKind.Source || leg.Reason != EconomyReason.SeasonGoods || leg.Delta <= 0)
                            issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                                "Season reissue leg is not a source to its actor's mailbox."));
                    }
                    else if (economic && (location.Owner.Kind != EconomyOwnerKind.Role ||
                        location.Owner.Id != owner.Actor.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                        location.Compartment != EconomyCompartment.Pending && asset.Kind == EconomyAssetKind.Currency &&
                            (location.Compartment != EconomyCompartment.Currency || location.Instance.Length != 0 ||
                             location.Key != $"role/{owner.Actor}/currency/{asset.Id}") ||
                        location.Compartment != EconomyCompartment.Pending && asset.Kind == EconomyAssetKind.Item &&
                            (location.Compartment != EconomyCompartment.Inventory ||
                             !long.TryParse(location.Instance, out long instance) || instance <= 0 ||
                             !location.Key.StartsWith("item/" + location.Instance + "/pack-", StringComparison.Ordinal))))
                        issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                            "Leg has a foreign actor or noncanonical holding location."));
                    if (escrow && (location.Owner.Id.Length == 0 || location.Key.Length == 0 ||
                        location.Compartment is < EconomyCompartment.Inventory or > EconomyCompartment.Recovery))
                        issues.Add(new(EconomyIssueKind.Corruption, "leg/" + op + "/" + leg.LegNumber,
                            "E03 leg has a noncanonical holding location."));
                    if (!economic && !isEvent && !escrow && allReceipts.ContainsKey(seq)) // E05d semantic receipts only
                    {
                        var key = (op, asset, location.Owner, location.Compartment, leg.Kind, leg.Reason);
                        e05Ledger[key] = e05Ledger.GetValueOrDefault(key) + leg.Delta;
                    }
                }
            }
            foreach (var (operation, intent) in eventIntents)
            {
                var expectedOwners = intent.OwnerDeltas.GroupBy(d => (d.Owner, d.Asset)).ToDictionary(g => g.Key,
                    g => g.Aggregate(BigInteger.Zero, (sum, d) => sum + d.Delta));
                var actualOwners = eventOwnerAmounts.Where(p => p.Key.Operation == operation)
                    .ToDictionary(p => (p.Key.Owner, p.Key.Asset), p => p.Value);
                foreach (var key in expectedOwners.Keys.Concat(actualOwners.Keys).Distinct())
                    if (expectedOwners.GetValueOrDefault(key) != actualOwners.GetValueOrDefault(key))
                        issues.Add(new(EconomyIssueKind.Corruption, "event/beneficiary/" + operation, "Event legs changed the frozen recipient or amount."));
            }
            var transferIndexes = new Dictionary<(string OperationId, int EdgeNumber), List<int>>();
            for (int i = 0; i < legs.Count; i++)
                if (legs[i].Kind == EconomyLegKind.Transfer)
                {
                    var key = (legs[i].OperationId, legs[i].EdgeNumber);
                    if (!transferIndexes.TryGetValue(key, out var indexes))
                        transferIndexes.Add(key, indexes = []);
                    indexes.Add(i);
                }
            foreach (var edge in allTransferLegs
                .GroupBy(x => (x.OperationId, x.EdgeNumber)))
            {
                var pair = edge.ToArray();
                var debits = pair.Where(x => x.Delta < 0).ToArray();
                var credits = pair.Where(x => x.Delta > 0).ToArray();
                if (pair.Length != 2 || debits.Length != 1 || credits.Length != 1 ||
                    debits[0].Asset != credits[0].Asset ||
                    debits[0].Delta != -credits[0].Delta || debits[0].Location == credits[0].Location ||
                    debits[0].Counterparty is { } debitCounterparty && debitCounterparty != credits[0].Location ||
                    credits[0].Counterparty is { } creditCounterparty && creditCounterparty != debits[0].Location ||
                    escrowTransfers.ContainsKey(edge.Key.OperationId) && debits[0].Asset.Kind == EconomyAssetKind.Item &&
                        (pair[0].Delta >= 0 || debits[0].BeforeFingerprint != credits[0].AfterFingerprint))
                    issues.Add(new(EconomyIssueKind.Corruption, "edge/" + edge.Key.OperationId + "/" + edge.Key.EdgeNumber,
                        "Relocation edge lacks one exact opposite counterparty."));
                else
                {
                    var debit = debits[0];
                    var credit = credits[0];
                    if (commands.TryGetValue(edge.Key.OperationId, out var kind) && !Economic(kind) && !kind.StartsWith("event/", StringComparison.Ordinal) && !EscrowEconomyIntent.IsKind(kind))
                    {
                        var key = (debit.OperationId, debit.Asset, debit.Location.Owner, debit.Location.Compartment,
                            debit.Reason, credit.Location.Owner, credit.Location.Compartment, credit.Reason);
                        e05LedgerEdges[key] = e05LedgerEdges.GetValueOrDefault(key) - debit.Delta;
                    }
                    if (transferIndexes.TryGetValue(edge.Key, out var indexes))
                        foreach (int i in indexes)
                            legs[i] = legs[i] with
                            {
                                Counterparty = legs[i].LegNumber == debit.LegNumber
                                ? credit.Location : debit.Location
                            };
                }
            }
            foreach (var receipt in receipts)
            {
                int[] numbers = legs.Where(x => x.OperationId == receipt.OperationId).Select(x => x.LegNumber).Order().ToArray();
                if (numbers.Length != receipt.LegCount || !numbers.SequenceEqual(Enumerable.Range(0, numbers.Length)))
                    issues.Add(new(EconomyIssueKind.Corruption, "receipt/" + receipt.OperationId, "Leg count or numbering is incomplete."));
            }
            foreach (var key in intentAmounts.Keys.Concat(ledgerAmounts.Keys).Distinct())
                if (intentAmounts.GetValueOrDefault(key) != ledgerAmounts.GetValueOrDefault(key))
                    issues.Add(new(EconomyIssueKind.Corruption, "intent/" + key.Operation + "/" + key.Asset,
                        "Semantic cost/goods amount differs from retained sink/source legs."));
            foreach (var key in escrowReasons.Keys.Concat(escrowLedgerReasons.Keys).Distinct())
                if (escrowReasons.GetValueOrDefault(key) != escrowLedgerReasons.GetValueOrDefault(key))
                    issues.Add(new(EconomyIssueKind.Corruption, "intent/" + key.Operation + "/" + key.Asset,
                        "E03 reason/denomination amount differs from retained external legs."));
            foreach (var (operation, authorizations) in escrowAuthorizations)
                try
                {
                    if (!MatchEscrowExternal(authorizations, escrowExternalLegs.GetValueOrDefault(operation) ?? []))
                        issues.Add(new(EconomyIssueKind.Corruption, "intent/" + operation,
                            "E03 external authorization cannot cover the posted owner and compartment locations."));
                }
                catch (OverflowException)
                {
                    issues.Add(new(EconomyIssueKind.Corruption, "intent/" + operation,
                        "E03 external authorization quantity overflowed."));
                }
            foreach (var (operation, realizations) in escrowRealizations)
                if (!MatchEscrowRealizations(realizations, escrowAllLegs.GetValueOrDefault(operation) ?? []))
                    issues.Add(new(EconomyIssueKind.Corruption, "intent/" + operation,
                        "E03 mail attachment realization does not match exact transfer and metadata evidence."));
            foreach (var key in e05Intent.Keys.Concat(e05Ledger.Keys).Distinct())
                if (e05Intent.GetValueOrDefault(key) != e05Ledger.GetValueOrDefault(key))
                    issues.Add(new(EconomyIssueKind.Corruption, "intent/" + key.Operation + "/" + key.Asset,
                        "E05d semantic owner/compartment/reason differs from retained ledger legs."));
            foreach (var key in e05IntentEdges.Keys.Concat(e05LedgerEdges.Keys).Distinct())
                if (e05IntentEdges.GetValueOrDefault(key) != e05LedgerEdges.GetValueOrDefault(key))
                    issues.Add(new(EconomyIssueKind.Corruption, "intent/transfer/" + key.Operation + "/" + key.Asset,
                        "E05d transfer counterpart differs from semantic authorization."));
        }
        catch (EconomySchemaMissingException)
        {
            issues.Add(new(EconomyIssueKind.UnknownSchema, "ledger/schema", "Required economy receipt/source/entry or obligation schema is absent."));
        }
        tx.Commit();
        bool complete = issues.Count == 0;
        return new(storedRealm, catalog.Version, EconomySnapshotReader.CoverageVersion, fromExclusive, throughInclusive, complete,
            legs, receipts, prior, issues, EconomySnapshotReader.UncoveredFamilies);
    }

    private static bool MatchEscrowExternal(IReadOnlyList<EscrowExternalChange> authorizations,
        IReadOnlyList<EconomyLeg> posted)
    {
        if (posted.Any(x => !EscrowReasonLocation(x))) return false;
        foreach (var family in authorizations.Select(x => (x.Kind, x.Asset, x.Reason))
            .Concat(posted.Select(x => (x.Kind, x.Asset, x.Reason))).Distinct())
        {
            var grants = authorizations.Where(x => (x.Kind, x.Asset, x.Reason) == family)
                .GroupBy(x => (x.Owner, x.Compartment))
                .Select(x => (x.Key.Owner, x.Key.Compartment,
                    Amount: x.Aggregate(0L, (sum, value) => checked(sum + value.Quantity)))).ToArray();
            var locations = posted.Where(x => (x.Kind, x.Asset, x.Reason) == family)
                .GroupBy(x => (x.Location.Owner, x.Location.Compartment))
                .Select(x => (x.Key.Owner, x.Key.Compartment,
                    Amount: x.Aggregate(0L, (sum, value) => checked(sum + Math.Abs(value.Delta))))).ToArray();
            long authorized = grants.Aggregate(0L, (sum, x) => checked(sum + x.Amount));
            long observed = locations.Aggregate(0L, (sum, x) => checked(sum + x.Amount));
            if (authorized != observed) return false;
            int source = 0, firstGrant = 1, firstLocation = firstGrant + grants.Length;
            int sink = firstLocation + locations.Length;
            var flow = new EscrowAuthorizationFlow(sink + 1);
            for (int i = 0; i < grants.Length; i++)
            {
                flow.Add(source, firstGrant + i, grants[i].Amount);
                for (int j = 0; j < locations.Length; j++)
                    if ((grants[i].Owner is null || grants[i].Owner == locations[j].Owner) &&
                        (grants[i].Compartment is null || grants[i].Compartment == locations[j].Compartment))
                        flow.Add(firstGrant + i, firstLocation + j, Math.Min(grants[i].Amount, locations[j].Amount));
            }
            for (int j = 0; j < locations.Length; j++)
                flow.Add(firstLocation + j, sink, locations[j].Amount);
            if (flow.MaxFlow(source, sink) != authorized) return false;
        }
        return true;
    }

    private static bool EscrowReasonLocation(EconomyLeg leg) => leg.Reason switch
    {
        EconomyReason.MailExpiry => leg.Location.Compartment is EconomyCompartment.Mail or EconomyCompartment.LegacyMail,
        EconomyReason.SystemRetire or EconomyReason.SystemRestock =>
            leg.Location.Compartment == EconomyCompartment.Stall && leg.Location.Owner.Kind == EconomyOwnerKind.System,
        EconomyReason.GuildMarketCapDiscard =>
            leg.Location.Compartment is EconomyCompartment.GuildMarket or EconomyCompartment.Recovery,
        _ => true
    };

    private static bool MatchEscrowRealizations(IReadOnlyList<EscrowMailRealization> realizations,
        IReadOnlyList<EconomyLeg> ledger)
    {
        foreach (var realization in realizations)
        {
            string fingerprint = realization.AbstractFingerprint;
            var debits = ledger.Where(x => x.Kind == EconomyLegKind.Transfer && x.Delta < 0 &&
                x.Asset == realization.Asset && x.Location == realization.SourceLocation).ToArray();
            if (debits.Aggregate(BigInteger.Zero, (sum, x) => sum - x.Delta) != realization.Quantity ||
                debits.Any(x => x.BeforeFingerprint != fingerprint)) return false;
            foreach (var debit in debits)
            {
                var credits = ledger.Where(x => x.Kind == EconomyLegKind.Transfer && x.Delta > 0 &&
                    x.EdgeNumber == debit.EdgeNumber).ToArray();
                if (credits.Length != 1) return false;
                var credit = credits[0];
                if (credit.Delta != -debit.Delta || credit.Asset != realization.Asset ||
                    credit.Location.Owner != realization.SourceLocation.Owner ||
                    credit.Location.Compartment != EconomyCompartment.Inventory ||
                    credit.AfterFingerprint != fingerprint) return false;
                var metadata = ledger.Where(x => x.Kind == EconomyLegKind.Metadata &&
                    x.Asset == realization.Asset && x.Location == credit.Location).ToArray();
                if (metadata.Length > 1 || metadata.Length == 1 &&
                    (metadata[0].BeforeFingerprint != fingerprint ||
                     metadata[0].AfterFingerprint == fingerprint || metadata[0].Delta != 0)) return false;
            }
        }
        return true;
    }

    private sealed class EscrowAuthorizationFlow(int nodes)
    {
        private sealed class Edge(int target, int reverse, long capacity)
        {
            internal int Target = target, Reverse = reverse;
            internal long Capacity = capacity;
        }
        private readonly List<Edge>[] _graph = Enumerable.Range(0, nodes).Select(_ => new List<Edge>()).ToArray();

        internal void Add(int from, int to, long capacity)
        {
            if (capacity <= 0) return;
            _graph[from].Add(new Edge(to, _graph[to].Count, capacity));
            _graph[to].Add(new Edge(from, _graph[from].Count - 1, 0));
        }

        internal long MaxFlow(int source, int sink)
        {
            long total = 0;
            var previous = new (int Node, int Edge)[_graph.Length];
            while (true)
            {
                Array.Fill(previous, (-1, -1));
                var queue = new Queue<int>();
                queue.Enqueue(source);
                previous[source] = (source, -1);
                while (queue.Count != 0 && previous[sink].Node < 0)
                {
                    int from = queue.Dequeue();
                    for (int i = 0; i < _graph[from].Count; i++)
                    {
                        var edge = _graph[from][i];
                        if (edge.Capacity <= 0 || previous[edge.Target].Node >= 0) continue;
                        previous[edge.Target] = (from, i);
                        queue.Enqueue(edge.Target);
                    }
                }
                if (previous[sink].Node < 0) return total;
                long amount = long.MaxValue;
                for (int node = sink; node != source; node = previous[node].Node)
                {
                    var (from, index) = previous[node];
                    amount = Math.Min(amount, _graph[from][index].Capacity);
                }
                for (int node = sink; node != source; node = previous[node].Node)
                {
                    var (from, index) = previous[node];
                    var edge = _graph[from][index];
                    edge.Capacity -= amount;
                    _graph[node][edge.Reverse].Capacity = checked(_graph[node][edge.Reverse].Capacity + amount);
                }
                total = checked(total + amount);
            }
        }
    }

    private static EconomyLedgerWindow Empty(string realmId, EconomyCatalog catalog, long from, long through,
        IReadOnlyList<EconomyIssue> issues) =>
        new(realmId, catalog.Version, EconomySnapshotReader.CoverageVersion, from, through, false, [], [], [], issues,
            EconomySnapshotReader.UncoveredFamilies);

    private static (EconomyReason Cost, EconomyReason Goods) FamilyReasons(string family, string command) =>
        CharacterFamilyEconomy.IsKind(command) ? CharacterFamilyEconomy.Reasons(command) : family switch
        {
            "pet" => (EconomyReason.PetCost,
                command == "cd13b/pet-rebirth" ? EconomyReason.PetRefund : EconomyReason.PetGoods),
            "mount" => (EconomyReason.MountCost, EconomyReason.MountGoods),
            "wardrobe" => (EconomyReason.WardrobeCost, EconomyReason.WardrobeGoods),
            "totem" => (EconomyReason.TotemCost,
                command == "cd13b/totem-upgrade" ? EconomyReason.TotemRefund : EconomyReason.TotemGoods),
            "emblem" => (EconomyReason.EmblemCost, EconomyReason.EmblemGoods),
            "genre" => (EconomyReason.GenreCost, EconomyReason.GenreGoods),
            "valhalla" => (EconomyReason.ValhallaCost, EconomyReason.ValhallaCost),
            _ => throw new InvalidDataException("Unknown family reason namespace.")
        };

    private static void ValidateEscrowRevisions(string beforeJson, string afterJson,
        EscrowRolePart[][] childRoleWrites, object?[] childReceipts)
    {
        if (childRoleWrites.Length != childReceipts.Length)
            throw new InvalidDataException("E03 child write declarations differ from typed receipt count.");
        var expectedParts = childRoleWrites.SelectMany(x => x).Distinct()
            .GroupBy(x => x.RoleId)
            .ToDictionary(group => group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                group => group.Select(x => x.Part).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var mutated = childRoleWrites.SelectMany((parts, index) =>
            childReceipts[index] is false or 0 or 0L ? Array.Empty<EscrowRolePart>() : parts).ToHashSet();
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var prior = before.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
        var next = after.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
        if (prior.Count != expectedParts.Count || next.Count != expectedParts.Count)
            throw new InvalidDataException("E03 role vector count differs from declared child writes.");
        foreach (var (id, parts) in expectedParts)
        {
            if (!prior.TryGetValue(id, out var old) || !next.TryGetValue(id, out var updated) ||
                old.GetProperty("roleId").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) != id ||
                old.GetProperty("roleId").GetInt64() != updated.GetProperty("roleId").GetInt64() ||
                old.GetProperty("epoch").GetInt64() <= 0 ||
                old.GetProperty("epoch").GetInt64() != updated.GetProperty("epoch").GetInt64() ||
                old.GetProperty("deleted").GetBoolean() || updated.GetProperty("deleted").GetBoolean())
                throw new InvalidDataException("Invalid E03 role vector identity.");
            var oldParts = old.GetProperty("parts").EnumerateObject()
                .ToDictionary(x => x.Name, x => x.Value.GetInt64(), StringComparer.Ordinal);
            var newParts = updated.GetProperty("parts").EnumerateObject()
                .ToDictionary(x => x.Name, x => x.Value.GetInt64(), StringComparer.Ordinal);
            if (oldParts.Count != parts.Count || newParts.Count != parts.Count ||
                !oldParts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(parts) ||
                !newParts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(parts) ||
                oldParts.Any(x => x.Value < 0 || !newParts.TryGetValue(x.Key, out long revision) ||
                    revision != (mutated.Contains(new EscrowRolePart(old.GetProperty("roleId").GetInt64(), x.Key))
                        ? checked(x.Value + 1) : x.Value)))
                throw new InvalidDataException("E03 role revision advancement differs from typed child mutations.");
        }
    }

    private static (EconomyReason TransferReason, EscrowExternalChange[] Changes,
        EscrowMailRealization[] Realizations, EscrowRolePart[][] ChildRoleWrites) ValidateEscrowIntentPayload(
        byte[] payload, string hash, string operation, string account, long role, string command,
        string fingerprint, string source, long effective)
    {
        if (payload.Length is < 1 or > 1024 * 1024 ||
            Convert.ToHexString(SHA256.HashData(payload)) != hash || fingerprint != hash)
            throw new InvalidDataException("Invalid E03 intent hash or length.");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("OperationId").GetString() != operation ||
            root.GetProperty("CommandKind").GetString() != command ||
            root.GetProperty("ActorAccount").GetString() != account ||
            root.GetProperty("ActorRoleId").GetInt64() != role ||
            root.GetProperty("SourceKey").GetString() != source ||
            root.GetProperty("EffectiveUtcMs").GetInt64() != effective || role < 0 ||
            role > 0 && string.IsNullOrWhiteSpace(account))
            throw new InvalidDataException("E03 intent identity differs from receipt.");
        var transfer = (EconomyReason)root.GetProperty("TransferReason").GetInt32();
        if (!EconomyReasonCatalog.Allows(transfer, EconomyLegKind.Transfer))
            throw new InvalidDataException("Unknown E03 transfer reason.");
        var changes = root.GetProperty("Changes").Deserialize<EscrowExternalChange[]>();
        if (changes is null || changes.Length > 8192 || changes.Any(x =>
            x.Quantity <= 0 || x.Asset.Id <= 0 || x.Kind is not (EconomyLegKind.Source or EconomyLegKind.Sink) ||
            !EconomyReasonCatalog.Allows(x.Reason, x.Kind)))
            throw new InvalidDataException("Invalid E03 external authorization.");
        var realizations = root.GetProperty("Realizations").Deserialize<EscrowMailRealization[]>();
        if (realizations is null || realizations.Length > 8192 || realizations.Any(x =>
            x.Asset.Kind != EconomyAssetKind.Item || x.Asset.Id <= 0 || x.Quantity <= 0 || x.RoleId <= 0 ||
            string.IsNullOrWhiteSpace(x.LetterId) || x.LetterId.Length > 128 || x.LetterId.Contains('/') ||
            x.AttachmentIndex < 0 || x.Bind < 0) ||
            realizations.Select(x => x.SourceLocation).Distinct().Count() != realizations.Length)
            throw new InvalidDataException("Invalid E03 mail attachment realization.");
        var evidence = root.GetProperty("Evidence").Deserialize<EscrowEvidence[]>();
        if (evidence is null || evidence.Length > 256 || evidence.Any(x => x.Hash.Length != 64 ||
            x.Bytes < 0 || x.Bytes > 16 * 1024 * 1024 || x.Json is { } json &&
            (Encoding.UTF8.GetByteCount(json) != x.Bytes ||
             Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) != x.Hash)))
            throw new InvalidDataException("Invalid E03 resolved evidence.");
        var childRoleWrites = root.GetProperty("ChildRoleWrites").Deserialize<EscrowRolePart[][]>();
        if (childRoleWrites is null || childRoleWrites.Length is < 1 or > 16384 ||
            childRoleWrites.Any(parts => parts is null))
            throw new InvalidDataException("Invalid E03 child role-write declarations.");
        foreach (var parts in childRoleWrites)
            for (int i = 0; i < parts.Length; i++)
            {
                var current = parts[i];
                if (current.RoleId <= 0 || string.IsNullOrWhiteSpace(current.Part) || current.Part.Length > 128 ||
                    i > 0 && (parts[i - 1].RoleId > current.RoleId ||
                        parts[i - 1].RoleId == current.RoleId &&
                        StringComparer.Ordinal.Compare(parts[i - 1].Part, current.Part) >= 0))
                    throw new InvalidDataException("Noncanonical E03 child role-write declaration.");
            }
        return (transfer, changes, realizations, childRoleWrites);
    }

    private static (long? BeforeProgress, long? AfterProgress) ValidateRevisions(string beforeJson, string afterJson, long actorRole,
        string commandKind, object?[] childReceipts)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var b = before.RootElement; var a = after.RootElement;
        if (b.GetProperty("roleId").GetInt64() != actorRole ||
            b.GetProperty("roleId").GetInt64() != a.GetProperty("roleId").GetInt64() ||
            b.GetProperty("epoch").GetInt64() <= 0 ||
            b.GetProperty("epoch").GetInt64() != a.GetProperty("epoch").GetInt64() ||
            b.GetProperty("deleted").GetBoolean() || a.GetProperty("deleted").GetBoolean())
            throw new InvalidDataException("Role lifecycle identity changed inside purchase receipt.");
        var bParts = b.GetProperty("parts").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt64());
        var aParts = a.GetProperty("parts").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt64());
        if (EconomyMutation.IsFamilyKind(commandKind) || EconomyMutation.IsProgressionKind(commandKind))
        {
            int childCount = childReceipts.Length;
            var familyParts = (EconomyMutation.IsProgressionKind(commandKind)
                ? ProgressionEconomyTransactionPlan.AllowedBlobsForKind(commandKind)
                : FamilyEconomyTransactionPlan.AllowedBlobsForKind(commandKind)).Append("inventory").Append("progress")
                .ToHashSet(StringComparer.Ordinal);
            if (bParts.Count != childCount || !bParts.ContainsKey("inventory") || bParts.Keys.Except(familyParts).Any() ||
                commandKind == "cd13b/genre-apply" &&
                    (childCount != 11 || bParts.Count != 11 ||
                     new[] { "inventory", "progress", "genre", "stat_plans", "skill_plans", "equip_plans",
                         "card_plans", "totems", "emblems", "pets", "season_talent" }
                        .Any(part => !bParts.ContainsKey(part))) ||
                aParts.Count != bParts.Count || bParts.Any(x => x.Value < 0 ||
                    !aParts.TryGetValue(x.Key, out long next) || next != checked(x.Value + 1)))
                throw new InvalidDataException("Receipt revision vector is incomplete or nonconsecutive.");
            return bParts.TryGetValue("progress", out long familyBefore) ? (familyBefore, aParts["progress"]) : (null, null);
        }
        string[] required = commandKind switch
        {
            "npc-shop-buy" => ["inventory", "progress", "shop"],
            "gather-yield" => ["inventory", "progress", "lifejob", "life_restrict", "life_runtime"],
            "dungeon-reward" => ["inventory", "progress", "dungeons"],
            "tower-reward" => ["inventory", "progress", "tower"],
            "cd13e/tower-milestone" => ["inventory", "progress", "tower"],
            "cd13e/quest-award" => ["inventory", "progress", "tasks", "task_runtime"],
            "cd13e/achievement-award" => ["inventory", "progress", "achievements"],
            "cd13e/advguild-award" => ["inventory", "progress", "adventure_group"],
            "cd13e/monsterhunt-award" => ["inventory", "progress", "area_concur"],
            "cd13e/guild-award" => ["inventory", "progress", "guild_info"],
            "cd13e/growthclass-award" => ["inventory", "progress", "growth_class"],
            "cd13e/library-award" => ["inventory", "progress", "library"],
            "cd13e/firstheart-award" => ["inventory", "progress", "first_heart"],
            "cd13e/daily-stage" => ["inventory", "progress", "daily"],
            "cd13e/daily-reset" => ["progress", "daily", "tasks"],
            "cd13e/journey-victory" => ["inventory", "progress", "new_activities", "dungeons"],
            "dungeon-obligation-earn" => [],
            _ => ["inventory", "progress"]
        };
        string[] allowed = commandKind switch
        {
            "monster-kill" => ["inventory", "progress", "library", "task_followup", "tower", "tasks", "wild_afk", "area_concur"],
            "cd13e/daily-reset" => [.. required, "wild_afk"],
            "gather-yield" => [.. required, "library", "task_followup"],
            "dungeon-reward" => [.. required, "auction_order", "library", "task_followup"],
            "cd13e/tower-milestone" or "cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or
            "cd13e/grant" or "cd13e/gm-grant" or "cd13e/character-exp" or
                "cd13e/journey-victory" => [.. required, "daily", "library", "task_followup", "activity_runtime", "mounts"],
            _ => required
        };
        bool library = bParts.ContainsKey("library"), followup = bParts.ContainsKey("task_followup");
        if (commandKind == "cd13e/daily-reset" &&
            (childReceipts.Length != (bParts.ContainsKey("wild_afk") ? 2 : 1) ||
             bParts.ContainsKey("wild_afk") != (childReceipts.Length == 2)))
            throw new InvalidDataException("Daily reset child/revision shape is incomplete.");
        int expectedChildren = commandKind switch
        {
            "gather-yield" => 5 + (library ? 1 : 0) + (followup ? 1 : 0),
            "dungeon-reward" => 4 + (bParts.ContainsKey("auction_order") ? 1 : 0) +
                (library ? 1 : 0) + (followup ? 1 : 0),
            _ => childReceipts.Length
        };
        bool creditedKill = commandKind == "monster-kill" && childReceipts.Length > 2 &&
            childReceipts[^1] is long[];
        if (required.Except(bParts.Keys).Any() || bParts.Keys.Except(allowed).Any() ||
            followup && !library && !(creditedKill && bParts.ContainsKey("tasks")) ||
            (creditedKill && ((long[])childReceipts[^1]!).Contains(actorRole) !=
                (bParts.ContainsKey("tasks") && followup)) ||
            commandKind == "monster-kill" &&
                (!creditedKill || !((long[])childReceipts[^1]!).Contains(actorRole)) && bParts.ContainsKey("tasks") ||
            childReceipts.Length != expectedChildren ||
            aParts.Count != bParts.Count || bParts.Any(x => x.Value < 0 ||
                !aParts.TryGetValue(x.Key, out long next) || next != checked(x.Value +
                    (x.Key == "auction_order" && childReceipts[3] is 0 ? 0 : 1))))
            throw new InvalidDataException("Economic revision vector is incomplete or nonconsecutive.");
        if (commandKind == "monster-kill")
        {
            long[] credited = creditedKill ? (long[])childReceipts[^1]! : [];
            long[] others = credited.Where(role => role != actorRole).ToArray();
            var beforeParticipants = b.TryGetProperty("participants", out var bp)
                ? bp.EnumerateArray().ToArray() : [];
            var afterParticipants = a.TryGetProperty("participants", out var ap)
                ? ap.EnumerateArray().ToArray() : [];
            if (beforeParticipants.Length != others.Length || afterParticipants.Length != others.Length)
                throw new InvalidDataException("Kill task participant revision list is incomplete.");
            for (int i = 0; i < others.Length; i++)
            {
                var pb = beforeParticipants[i]; var pa = afterParticipants[i];
                if (pb.GetProperty("roleId").GetInt64() != others[i] ||
                    pa.GetProperty("roleId").GetInt64() != others[i] ||
                    pb.GetProperty("epoch").GetInt64() <= 0 ||
                    pb.GetProperty("epoch").GetInt64() != pa.GetProperty("epoch").GetInt64() ||
                    pb.GetProperty("deleted").GetBoolean() || pa.GetProperty("deleted").GetBoolean())
                    throw new InvalidDataException("Kill task participant lifecycle is invalid.");
                var ppb = pb.GetProperty("parts").EnumerateObject()
                    .ToDictionary(x => x.Name, x => x.Value.GetInt64());
                var ppa = pa.GetProperty("parts").EnumerateObject()
                    .ToDictionary(x => x.Name, x => x.Value.GetInt64());
                if (ppb.Count != 2 || !ppb.ContainsKey("tasks") || !ppb.ContainsKey("task_followup") ||
                    ppa.Count != 2 || ppb.Any(x => x.Value < 0 ||
                        !ppa.TryGetValue(x.Key, out long next) || next != checked(x.Value + 1)))
                    throw new InvalidDataException("Kill task participant revisions are invalid.");
            }
        }
        return bParts.TryGetValue("progress", out long beforeProgress)
            ? (beforeProgress, aParts["progress"]) : (null, null);
    }

    private static void ValidateEnvelopeRevisions(string beforeJson, string afterJson)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var b = before.RootElement.GetProperty("roles");
        var a = after.RootElement.GetProperty("roles");
        if (b.ValueKind != a.ValueKind) throw new InvalidDataException("E05d role vector shape changed.");
        if (b.ValueKind != JsonValueKind.Null &&
            (b.ValueKind != JsonValueKind.Array || a.ValueKind != JsonValueKind.Array || b.GetArrayLength() != a.GetArrayLength()))
            throw new InvalidDataException("E05d role vector is malformed.");
        for (int i = 0; b.ValueKind == JsonValueKind.Array && i < b.GetArrayLength(); i++)
        {
            var prior = b[i]; var next = a[i];
            if (prior.GetProperty("RoleId").GetInt64() != next.GetProperty("RoleId").GetInt64() ||
                prior.GetProperty("Epoch").GetInt64() != next.GetProperty("Epoch").GetInt64() ||
                prior.GetProperty("Deleted").GetBoolean() != next.GetProperty("Deleted").GetBoolean())
                throw new InvalidDataException("E05d lifecycle vector changed inside receipt.");
            var oldParts = prior.GetProperty("parts").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt64());
            var newParts = next.GetProperty("parts").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt64());
            if (oldParts.Count != newParts.Count || oldParts.Any(x => x.Value < 0 ||
                !newParts.TryGetValue(x.Key, out long value) || value < x.Value || value > checked(x.Value + 1)))
                throw new InvalidDataException("E05d revision evidence is missing or nonconsecutive.");
        }
        var be = before.RootElement.GetProperty("entities");
        var ae = after.RootElement.GetProperty("entities");
        if (be.ValueKind != ae.ValueKind || be.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array) ||
            be.ValueKind == JsonValueKind.Array && be.GetArrayLength() != ae.GetArrayLength())
            throw new InvalidDataException("E05d entity vector is malformed.");
        var seen = new HashSet<(string Kind, string Id, string Part)>();
        for (int i = 0; be.ValueKind == JsonValueKind.Array && i < be.GetArrayLength(); i++)
        {
            var prior = be[i]; var next = ae[i];
            var oldKey = prior.GetProperty("Key"); var newKey = next.GetProperty("Key");
            var key = (oldKey.GetProperty("Kind").GetString() ?? "",
                oldKey.GetProperty("Id").GetString() ?? "",
                oldKey.GetProperty("Part").GetString() ?? "");
            if (key.Item1.Length == 0 || key.Item2.Length == 0 || key.Item3.Length == 0 || !seen.Add(key) ||
                newKey.GetProperty("Kind").GetString() != key.Item1 ||
                newKey.GetProperty("Id").GetString() != key.Item2 ||
                newKey.GetProperty("Part").GetString() != key.Item3)
                throw new InvalidDataException("E05d entity identity changed inside receipt.");
            bool oldExists = prior.GetProperty("Exists").GetBoolean();
            bool newExists = next.GetProperty("Exists").GetBoolean();
            long oldRevision = prior.GetProperty("Revision").GetInt64();
            long newRevision = next.GetProperty("Revision").GetInt64();
            if (oldRevision < 0 || !oldExists && oldRevision != 0 ||
                oldExists && !newExists || newRevision < oldRevision ||
                newRevision > checked(oldRevision + 1) ||
                !oldExists && newExists && newRevision != 1 ||
                !newExists && newRevision != 0)
                throw new InvalidDataException("E05d entity revision evidence is missing or nonconsecutive.");
        }
    }

    private static IReadOnlyList<EconomyEffect> ValidateEnvelopeIntent(byte[] payload, string hash,
        string operation, string account, long role, string command, string fingerprint, string source,
        long effective, byte[] outcome, object?[] typedChildren)
    {
        if (payload.Length is 0 or > 2 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(payload)) != hash)
            throw new InvalidDataException("E05d canonical intent payload/hash is altered.");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("OperationId").GetString() != operation ||
            root.GetProperty("ActorAccount").GetString() != account ||
            root.GetProperty("ActorRoleId").GetInt64() != role ||
            root.GetProperty("CommandKind").GetString() != command ||
            root.GetProperty("RequestFingerprint").GetString() != fingerprint ||
            root.GetProperty("SourceKey").GetString() != source ||
            root.GetProperty("EffectiveUtcMs").GetInt64() != effective ||
            !Convert.FromBase64String(root.GetProperty("Outcome").GetString() ?? "").AsSpan().SequenceEqual(outcome))
            throw new InvalidDataException("E05d canonical intent differs from receipt fields.");
        var effects = root.GetProperty("Effects").Deserialize<EconomyEffect[]>() ??
            throw new InvalidDataException("E05d semantic effects are absent.");
        byte[] childIntent = Convert.FromBase64String(root.GetProperty("ChildIntent").GetString() ?? "");
        if (childIntent.Length == 0 || childIntent.Length > 512 * 1024)
            throw new InvalidDataException("E05d typed child intent is absent or oversized.");
        using (var typed = JsonDocument.Parse(childIntent))
            if (typed.RootElement.GetProperty("format").GetString() != "e05d-typed-children-v1" ||
                typed.RootElement.GetProperty("parts").ValueKind != JsonValueKind.Array ||
                typed.RootElement.GetProperty("parts").GetArrayLength() != typedChildren.Length)
                throw new InvalidDataException("E05d typed child intent is malformed.");
        var rebuilt = new EconomyEnvelope(operation, account, role, command, fingerprint, source, effective,
            effects, outcome).BindChildIntent(childIntent);
        if (!rebuilt.IntentPayload.AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("E05d canonical semantic effects were altered.");
        const string alias = "auction-source:";
        if (!effects.Any(x => x.Owner.Kind == EconomyOwnerKind.Escrow &&
            x.Owner.Id.StartsWith(alias, StringComparison.Ordinal))) return effects;
        using var typedIntent = JsonDocument.Parse(childIntent);
        var parts = typedIntent.RootElement.GetProperty("parts");
        var claims = new HashSet<string>(StringComparer.Ordinal);
        var lots = new Dictionary<string, (long Id, long Entry)>(StringComparer.Ordinal);
        for (int i = 0; i < parts.GetArrayLength(); i++)
        {
            var part = parts[i];
            string label = part.GetProperty("label").GetString() ?? "";
            if (label == "auction/source-claim")
            {
                if (part.GetProperty("type").GetString() != typeof(AuctionSourceClaimPlan).FullName ||
                    typedChildren[i] is not true ||
                    !claims.Add(part.GetProperty("auctionSource").GetString() ?? ""))
                    throw new InvalidDataException("E05d auction source claim child is invalid.");
            }
            else if (label == "auction/item" && typedChildren[i] is AuctionItemWriteReceipt lot)
            {
                if (part.GetProperty("type").GetString() != typeof(AuctionItemWritePlan).FullName ||
                    !part.GetProperty("auctionCreatesNew").GetBoolean() || lot.Id <= 0)
                    throw new InvalidDataException("E05d auction lot child is not a creation.");
                string lotSource = part.GetProperty("auctionSource").GetString() ?? "";
                using var state = JsonDocument.Parse(lot.State);
                if (lotSource.Length == 0 || state.RootElement.GetProperty("Id").GetInt64() != lot.Id ||
                    state.RootElement.GetProperty("SourceKey").GetString() != lotSource ||
                    !lots.TryAdd(lotSource, (lot.Id, state.RootElement.GetProperty("Entry").GetInt64())))
                    throw new InvalidDataException("E05d auction lot receipt identity is invalid.");
            }
        }
        return effects.Select(effect =>
        {
            if (effect.Owner.Kind != EconomyOwnerKind.Escrow ||
                !effect.Owner.Id.StartsWith(alias, StringComparison.Ordinal)) return effect;
            string lotSource = effect.Owner.Id[alias.Length..];
            if (lotSource.Length == 0 || effect.Asset.Kind != EconomyAssetKind.Item ||
                effect.Compartment != EconomyCompartment.AuctionLot || effect.Kind != EconomyLegKind.Source ||
                effect.Delta != 1 || !claims.Contains(source) || !IsLotSource(lotSource, source) ||
                !lots.TryGetValue(lotSource, out var lot) || lot.Entry != effect.Asset.Id)
                throw new InvalidDataException("E05d auction alias lacks its authenticated created lot.");
            return effect with
            {
                Owner = new EconomyOwner(EconomyOwnerKind.Escrow,
                "auction:" + lot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
        }).ToArray();

        static bool IsLotSource(string lotSource, string batchSource)
        {
            if (!lotSource.StartsWith(batchSource + ":", StringComparison.Ordinal)) return false;
            string suffix = lotSource[(batchSource.Length + 1)..];
            return int.TryParse(suffix, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                index >= 0 && suffix == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

    }

    private static ParsedIntent ValidateIntentPayload(
        byte[] payload, string hash, string operation, string account, long role, string command,
        string fingerprint, string source, long effective, byte[] outcome)
    {
        if (payload.Length == 0 || payload.Length > (EconomyMutation.IsFamilyKind(command) || EconomyMutation.IsProgressionKind(command)
                ? EconomyMutation.MaxFamilyIntentBytes : 65536) ||
            Convert.ToHexString(SHA256.HashData(payload)) != hash)
            throw new InvalidDataException("Canonical intent payload/hash is missing or altered.");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("OperationId").GetString() != operation ||
            root.GetProperty("ActorAccount").GetString() != account ||
            root.GetProperty("ActorRoleId").GetInt64() != role ||
            root.GetProperty("CommandKind").GetString() != command ||
            root.GetProperty("RequestFingerprint").GetString() != fingerprint ||
            root.GetProperty("SourceKey").GetString() != source ||
            root.GetProperty("EffectiveUtcMs").GetInt64() != effective ||
            !Convert.FromBase64String(root.GetProperty("Outcome").GetString() ?? "").AsSpan().SequenceEqual(outcome))
            throw new InvalidDataException("Canonical intent differs from committed receipt fields or outcome.");
        if (EconomyMutation.IsProgressionKind(command) &&
            (!root.TryGetProperty("PlanFingerprint", out var planHash) || planHash.GetString() is not { Length: 64 } hashValue ||
             !hashValue.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("C/D 13c receipt lacks its frozen plan fingerprint.");
        var costs = root.GetProperty("Costs").Deserialize<EconomyQuantity[]>()
            ?? throw new InvalidDataException("Intent costs are absent.");
        var goods = root.GetProperty("Goods").Deserialize<EconomyQuantity[]>()
            ?? throw new InvalidDataException("Intent goods are absent.");
        bool family = EconomyMutation.IsFamilyKind(command) || EconomyMutation.IsProgressionKind(command);
        bool reward = EconomyMutation.IsRewardCommand(command);
        bool earned = command == "dungeon-obligation-earn";
        if ((!family && !reward && !earned && command != "cd13e/daily-reset" && costs.Length + goods.Length == 0) ||
            costs.Length + goods.Length > (family ? EconomyMutation.MaxFamilyQuantities : 32) ||
            costs.Concat(goods).Any(q => q.Quantity <= 0 || q.Asset.Id <= 0 || q.Asset.Epoch != "" ||
                q.Asset.Kind is not (EconomyAssetKind.Item or EconomyAssetKind.Currency)))
            throw new InvalidDataException("Canonical economic amounts are invalid.");
        EconomyRewardEvidence? evidence = root.TryGetProperty("RewardEvidence", out var property) &&
            property.ValueKind != JsonValueKind.Null ? property.Deserialize<EconomyRewardEvidence>() : null;
        if (reward != (evidence is not null)) throw new InvalidDataException("Missing or unexpected reward evidence.");
        if (earned && costs.Length != 0) throw new InvalidDataException("Earned obligation has unexpected cost.");
        if (command == "cd13e/daily-reset" &&
            (goods.Length != 0 || costs.Length > 1 ||
             costs.Any(x => x.Asset != new EconomyAsset(EconomyAssetKind.Currency, 5))))
            throw new InvalidDataException("Daily reset may sink only liveness currency.");
        if (evidence is not null)
        {
            EconomyMutation.ValidateRewardEvidence(evidence, goods);
            if (command == "world-revive" && goods.Length != 0 ||
                command != "world-revive" && !EconomyMutation.IsOwnedExchangeCommand(command) && costs.Length != 0)
                throw new InvalidDataException("Reward command has unsupported cost or goods amounts.");
        }
        return new(costs.Select(q => (q.Asset, Cost: true, Amount: (BigInteger)q.Quantity))
            .Concat(goods.Select(q => (q.Asset, Cost: false, Amount: (BigInteger)q.Quantity))).ToArray(), evidence);
    }

    /// <summary>Receipts of an EconomyMutation (E01 NPC, cd13a/cd13b families, E04 rewards and obligations, 13e
    /// owned exchanges) keep their command rules. Event and escrow envelopes are separate families;
    /// remaining commands use E05d semantic validation.</summary>
    private static bool Economic(string kind) => kind == "npc-shop-buy" || EconomyMutation.IsFamilyKind(kind) ||
        EconomyMutation.IsProgressionKind(kind) ||
        EconomyMutation.IsRewardCommand(kind) || kind == "dungeon-obligation-earn" || EconomyMutation.IsOwnedExchangeCommand(kind);

    private static bool ValidFingerprint(string? value) => value is null ||
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>The receipt, source and entry columns the ledger read selects.</summary>
    private static void RequireLedgerSchema(SqliteConnection db, SqliteTransaction tx)
    {
        EconomySchema.Require(db, tx, "operation_receipt", "commit_seq", "operation_id", "leg_count", "source_key",
            "realm_id", "actor_account", "actor_role", "command_kind", "request_fingerprint", "intent_hash", "outcome",
            "child_receipts", "before_revisions", "after_revisions", "effective_utc_ms", "intent_payload");
        EconomySchema.Require(db, tx, "economy_entry", "entry_id", "commit_seq", "operation_id", "leg_no", "owner_kind",
            "owner_id", "compartment_kind", "compartment_key", "instance_key", "asset_kind", "asset_id", "asset_epoch",
            "delta", "reason_code", "leg_kind", "edge_no", "before_fingerprint", "after_fingerprint", "source_key",
            "counterparty_owner_kind", "counterparty_owner_id", "counterparty_compartment_kind",
            "counterparty_compartment_key", "counterparty_instance_key");
        EconomySchema.Require(db, tx, "operation_source", "realm_id", "source_key", "operation_id");
    }

    /// <summary>Checked once, at the first dungeon receipt: older databases without dungeon rewards lack it.</summary>
    private static void RequireObligationSchema(SqliteConnection db, SqliteTransaction tx) =>
        EconomySchema.Require(db, tx, "dungeon_reward_obligation", "source_key", "role_id", "activated", "completed",
            "state");

    private static void ValidateDungeonObligation(SqliteConnection db, SqliteTransaction tx,
        string sourceKey, long role, ParsedIntent intent)
    {
        using var command = Command(db, tx,
            "SELECT role_id,activated,completed,state FROM dungeon_reward_obligation WHERE source_key=$key");
        command.Parameters.AddWithValue("$key", sourceKey);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != role || row.GetInt64(1) != 1 || row.GetInt64(2) != 1)
            throw new InvalidDataException("Dungeon receipt has no completed matching frozen obligation.");
        var frozen = EconomyDungeonPending.Decode((byte[])row.GetValue(3));
        if (frozen.Key != sourceKey || frozen.RoleId != role)
            throw new InvalidDataException("Frozen dungeon source identity differs from receipt.");
        row.Close();
        using (var earned = Command(db, tx,
            "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='dungeon-obligation-earn' AND source_key=$source AND actor_role=$role"))
        {
            earned.Parameters.AddWithValue("$source", EconomyDungeonPending.EarnedSource(sourceKey));
            earned.Parameters.AddWithValue("$role", role);
            if ((long)earned.ExecuteScalar()! != 1)
                throw new InvalidDataException("Dungeon delivery lacks its earned obligation receipt.");
        }
        var goods = EconomyDungeonPending.Holdings(frozen).GroupBy(h => h.Asset)
            .ToDictionary(x => x.Key, x => x.Aggregate(BigInteger.Zero, (sum, h) => sum + h.Quantity));
        BigInteger baseExp = frozen.Goods.Where(g => g.Entry == 100).Aggregate(BigInteger.Zero,
            (sum, g) => sum + g.Count);
        BigInteger jobExp = frozen.Goods.Where(g => g.Entry == 101).Aggregate(BigInteger.Zero,
            (sum, g) => sum + g.Count);
        var resolved = intent.Amounts.Where(x => !x.Cost).GroupBy(x => x.Asset)
            .ToDictionary(x => x.Key, x => x.Aggregate(BigInteger.Zero, (sum, amount) => sum + amount.Amount));
        if (goods.Count != resolved.Count || goods.Any(x => resolved.GetValueOrDefault(x.Key) != x.Value) ||
            intent.RewardEvidence is not { } evidence || baseExp != evidence.ResolvedBaseExp || jobExp != evidence.ResolvedJobExp)
            throw new InvalidDataException("Frozen dungeon outcome differs from committed goods or EXP.");
    }

    private static void ValidateEarnedObligation(SqliteConnection db, SqliteTransaction tx,
        string sourceKey, long role, long earnedSequence, long throughInclusive,
        ParsedIntent intent, byte[] intentPayload, byte[] outcome)
    {
        if (!sourceKey.EndsWith("/earned", StringComparison.Ordinal))
            throw new InvalidDataException("Earned obligation source suffix is invalid.");
        string deliverySource = sourceKey[..^"/earned".Length];
        using var command = Command(db, tx,
            "SELECT role_id,activated,state FROM dungeon_reward_obligation WHERE source_key=$key");
        command.Parameters.AddWithValue("$key", deliverySource);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != role || row.GetInt64(1) != 1)
            throw new InvalidDataException("Earned receipt has no matching frozen obligation.");
        byte[] state = (byte[])row.GetValue(2);
        byte[] hash = SHA256.HashData(state);
        if (!outcome.AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("Earned outcome differs from frozen obligation state.");
        using var document = JsonDocument.Parse(intentPayload);
        var root = document.RootElement;
        if (root.GetProperty("FrozenStateHash").GetString() != Convert.ToHexString(hash) ||
            root.GetProperty("RequestFingerprint").GetString() != Convert.ToHexString(hash))
            throw new InvalidDataException("Earned intent fingerprint differs from frozen state.");
        var frozen = EconomyDungeonPending.Decode(state);
        if (frozen.Key != deliverySource || frozen.RoleId != role)
            throw new InvalidDataException("Earned source identity differs from frozen state.");
        if (frozen.RequiresKill)
        {
            if (string.IsNullOrWhiteSpace(frozen.KillSourceKey))
                throw new InvalidDataException("Activated dungeon entitlement has no terminal kill source.");
            using var kill = Command(db, tx, """
                SELECT COUNT(*) FROM operation_receipt
                WHERE command_kind='monster-kill' AND source_key=$source
                  AND commit_seq>$earned AND commit_seq<=$through
                """);
            kill.Parameters.AddWithValue("$source", frozen.KillSourceKey);
            kill.Parameters.AddWithValue("$earned", earnedSequence);
            kill.Parameters.AddWithValue("$through", throughInclusive);
            if ((long)kill.ExecuteScalar()! != 1)
                throw new InvalidDataException("Earned dungeon entitlement has no committed terminal kill.");
        }
        long baseExp = frozen.Goods.Where(g => g.Entry == 100).Sum(g => (long)g.Count);
        long jobExp = frozen.Goods.Where(g => g.Entry == 101).Sum(g => (long)g.Count);
        if (root.GetProperty("PendingBaseExp").GetInt64() != baseExp ||
            root.GetProperty("PendingJobExp").GetInt64() != jobExp)
            throw new InvalidDataException("Earned EXP obligation differs from frozen state.");
        var expected = EconomyDungeonPending.Holdings(frozen).GroupBy(h => h.Asset)
            .ToDictionary(g => g.Key, g => g.Aggregate(BigInteger.Zero, (sum, h) => sum + h.Quantity));
        var actual = intent.Amounts.Where(x => !x.Cost).GroupBy(x => x.Asset)
            .ToDictionary(g => g.Key, g => g.Aggregate(BigInteger.Zero, (sum, x) => sum + x.Amount));
        if (expected.Count != actual.Count || expected.Any(x => actual.GetValueOrDefault(x.Key) != x.Value))
            throw new InvalidDataException("Earned pending goods differ from frozen state.");
    }

    internal static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        db.Open();
        using var only = db.CreateCommand();
        only.CommandText = "PRAGMA query_only=ON";
        only.ExecuteNonQuery();
        return db;
    }

    internal static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }
}

public sealed record EconomyChildReceipt(int Index, string Type, long? Number = null,
    IReadOnlyList<long>? GeneratedIds = null, bool? Flag = null, byte[]? Payload = null, string? Text = null);

public sealed record EconomyPersistedReceipt(string RealmId, long CommitSequence, string OperationId,
    string ActorAccount, long ActorRoleId, string CommandKind, string RequestFingerprint, string IntentHash,
    byte[] IntentPayload,
    string SourceKey, long RecordedUtcMs, long EffectiveUtcMs, byte[] Outcome, int LegCount,
    string BeforeRevisions, string AfterRevisions, IReadOnlyList<EconomyChildReceipt> ChildReceipts);

public static class EconomyReceiptReader
{
    public static EconomyPersistedReceipt? Read(string databasePath, string operationId)
    {
        using var db = EconomyLedgerReader.Open(databasePath);
        using var tx = db.BeginTransaction(deferred: true);
        using var command = EconomyLedgerReader.Command(db, tx, """
            SELECT realm_id,commit_seq,operation_id,actor_account,actor_role,command_kind,request_fingerprint,
            source_key,recorded_utc_ms,effective_utc_ms,outcome,leg_count,before_revisions,after_revisions,
            child_receipts,intent_hash,intent_payload
            FROM operation_receipt WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", operationId);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        bool escrow = EscrowEconomyIntent.IsKind(row.GetString(5));
        object?[] child = escrow ? EscrowEconomyReceiptCodec.Decode((byte[])row.GetValue(14))
            : EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(14));
        var typed = child.Select((value, index) => value switch
        {
            null => new EconomyChildReceipt(index, "null"),
            int number => new EconomyChildReceipt(index, "int", number),
            long number => new EconomyChildReceipt(index, "long", number),
            long[] ids => new EconomyChildReceipt(index, "generated-ids", GeneratedIds: Array.AsReadOnly(ids)),
            bool flag => new EconomyChildReceipt(index, "bool", Flag: flag),
            AuctionItemWriteReceipt lot => new EconomyChildReceipt(index, escrow ? "auction-item" : "auction-lot", lot.Id, Payload: lot.State),
            byte[] bytes => new EconomyChildReceipt(index, "bytes", Payload: bytes),
            string text => new EconomyChildReceipt(index, "string", Text: text),
            _ => throw new InvalidDataException("Unknown committed child result type.")
        }).ToArray();
        return new(row.GetString(0), row.GetInt64(1), row.GetString(2), row.GetString(3), row.GetInt64(4),
            row.GetString(5), row.GetString(6), row.GetString(15), (byte[])row.GetValue(16),
            row.GetString(7), row.GetInt64(8), row.GetInt64(9),
            (byte[])row.GetValue(10), row.GetInt32(11), row.GetString(12), row.GetString(13), typed);
    }
}
