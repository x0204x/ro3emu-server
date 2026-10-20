using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>E03: one existing typed cross-owner plan, observed against explicit external economics.</summary>
internal sealed class EscrowEconomyTransactionPlan(RoleItemPlan child, EscrowEconomyIntent intent,
    EconomyCatalog catalog, bool failAcknowledgementForTest = false) : DbPlan
{
    private int pendingAcknowledgementFault = failAcknowledgementForTest ? 1 : 0;
    private bool replay;
    internal override string Label => child.Label;
    internal override int Bytes => checked(child.Bytes * 2 + 8192 + intent.Payload.Length +
        intent.Changes.Count * 256 + child.Parts.Count * 256);
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => child.RoleWrites;
    internal override IReadOnlyList<long> RolePins => intent.ActorRoleId > 0
        ? child.RolePins.Append(intent.ActorRoleId).Distinct().ToArray() : child.RolePins;
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => child.EntityWrites;
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites => child.EntityDeltaWrites;
    internal override IReadOnlyList<RoleRevisionKey> MutatedRoleWrites(object? result) => child.MutatedRoleWrites(result);
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityWrites(object? result) => child.MutatedEntityWrites(result);
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityDeltaWrites(object? result) => child.MutatedEntityDeltaWrites(result);
    internal override void ResolveRoleWrites(AccountStore accounts) => child.ResolveRoleWrites(accounts);
    internal override bool IsReplay => replay;
    internal override void AfterCommit()
    {
        if (Interlocked.Exchange(ref pendingAcknowledgementFault, 0) != 0)
            throw new IOException("Test-only lost E03 commit acknowledgement.");
    }

    internal override bool TryReplay(SqliteConnection db, SqliteTransaction tx, out object? result)
    {
        ValidateShape();
        result = EscrowEconomyReceiptResolveReadPlan.Read(db, tx, intent, child);
        replay = result is not null;
        return replay;
    }

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        ValidateShape();
        if (intent.ActorRoleId > 0)
        {
            using var owner = DbSql.Command(db, tx, "SELECT account FROM role WHERE id=$role");
            owner.Parameters.AddWithValue("$role", intent.ActorRoleId);
            if (owner.ExecuteScalar() is not string account || account != intent.ActorAccount)
                throw new EconomyOperationConflictException("E03 actor role is not owned by the authenticated account.");
        }
        string realm;
        using (var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1"))
            realm = meta.ExecuteScalar() as string ?? throw new InvalidDataException("Missing E03 economy realm.");
        var scope = Scope(null);
        var before = EconomySnapshotReader.ReadEscrowScope(db, tx, scope, catalog);
        var receipts = (object?[])child.Execute(db, tx);
        EscrowEconomyReceiptCodec.Validate(child, receipts);
        var expanded = Scope(receipts);
        var after = EconomySnapshotReader.ReadEscrowScope(db, tx, expanded, catalog);
        var legs = BuildLegs(before.Holdings, after.Holdings, after.InventoryBinds);
        long sequence;
        using (var advance = DbSql.Command(db, tx,
            "UPDATE economy_meta SET next_sequence=next_sequence+1 WHERE singleton=1 RETURNING next_sequence"))
            sequence = (long?)advance.ExecuteScalar() ?? throw new InvalidDataException("Missing E03 watermark.");
        long recorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var revisionBefore = new Dictionary<string, object?>();
        var revisionAfter = new Dictionary<string, object?>();
        var mutatedRoleWrites = child.MutatedRoleWrites(receipts).ToHashSet();
        foreach (var group in RoleWrites.GroupBy(x => x.RoleId))
        {
            var expected = ExpectedRevisions?[group.Key] ?? throw new InvalidOperationException("Missing E03 role vector.");
            var prior = group.Select(x => x.Part).Distinct().Order(StringComparer.Ordinal)
                .ToDictionary(part => part, expected.Revision, StringComparer.Ordinal);
            revisionBefore[group.Key.ToString(CultureInfo.InvariantCulture)] = new
            { roleId = expected.RoleId, epoch = expected.Epoch, deleted = expected.Deleted, parts = prior };
            revisionAfter[group.Key.ToString(CultureInfo.InvariantCulture)] = new
            {
                roleId = expected.RoleId, epoch = expected.Epoch, deleted = expected.Deleted,
                parts = prior.ToDictionary(x => x.Key,
                    x => mutatedRoleWrites.Contains(new RoleRevisionKey(group.Key, x.Key))
                        ? checked(x.Value + 1) : x.Value, StringComparer.Ordinal)
            };
        }
        byte[] payload = intent.Payload;
        byte[] childBytes = EscrowEconomyReceiptCodec.Encode(receipts);
        using (var receipt = DbSql.Command(db, tx, """
            INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,
                child_receipts,before_revisions,after_revisions,leg_count)
            VALUES($op,$realm,$seq,$account,$role,$kind,$fingerprint,$hash,$payload,$source,$recorded,$effective,
                $outcome,$children,$before,$after,$count)
            """))
        {
            receipt.Parameters.AddWithValue("$op", intent.OperationId);
            receipt.Parameters.AddWithValue("$realm", realm);
            receipt.Parameters.AddWithValue("$seq", sequence);
            receipt.Parameters.AddWithValue("$account", intent.ActorAccount);
            receipt.Parameters.AddWithValue("$role", intent.ActorRoleId);
            receipt.Parameters.AddWithValue("$kind", intent.CommandKind);
            receipt.Parameters.AddWithValue("$fingerprint", intent.Hash);
            receipt.Parameters.AddWithValue("$hash", intent.Hash);
            receipt.Parameters.AddWithValue("$payload", payload);
            receipt.Parameters.AddWithValue("$source", intent.SourceKey);
            receipt.Parameters.AddWithValue("$recorded", recorded);
            receipt.Parameters.AddWithValue("$effective", intent.EffectiveUtcMs);
            receipt.Parameters.AddWithValue("$outcome", childBytes);
            receipt.Parameters.AddWithValue("$children", childBytes);
            receipt.Parameters.AddWithValue("$before", JsonSerializer.Serialize(revisionBefore));
            receipt.Parameters.AddWithValue("$after", JsonSerializer.Serialize(revisionAfter));
            receipt.Parameters.AddWithValue("$count", legs.Count);
            receipt.ExecuteNonQuery();
        }
        using (var source = DbSql.Command(db, tx,
            "INSERT INTO operation_source(realm_id,source_key,operation_id) VALUES($realm,$key,$op)"))
        {
            source.Parameters.AddWithValue("$realm", realm);
            source.Parameters.AddWithValue("$key", intent.SourceKey);
            source.Parameters.AddWithValue("$op", intent.OperationId);
            source.ExecuteNonQuery();
        }
        foreach (var leg in legs)
        {
            using var entry = DbSql.Command(db, tx, """
                INSERT INTO economy_entry(commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,
                    owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,
                    delta,reason_code,leg_kind,edge_no,before_fingerprint,after_fingerprint,source_key)
                VALUES($seq,$op,$no,$recorded,$effective,$ownerKind,$owner,$compartment,$key,$instance,$assetKind,
                    $asset,$epoch,$delta,$reason,$legKind,$edge,$before,$after,$source)
                """);
            entry.Parameters.AddWithValue("$seq", sequence);
            entry.Parameters.AddWithValue("$op", intent.OperationId);
            entry.Parameters.AddWithValue("$no", leg.LegNumber);
            entry.Parameters.AddWithValue("$recorded", recorded);
            entry.Parameters.AddWithValue("$effective", intent.EffectiveUtcMs);
            entry.Parameters.AddWithValue("$ownerKind", (int)leg.Location.Owner.Kind);
            entry.Parameters.AddWithValue("$owner", leg.Location.Owner.Id);
            entry.Parameters.AddWithValue("$compartment", (int)leg.Location.Compartment);
            entry.Parameters.AddWithValue("$key", leg.Location.Key);
            entry.Parameters.AddWithValue("$instance", leg.Location.Instance);
            entry.Parameters.AddWithValue("$assetKind", (int)leg.Asset.Kind);
            entry.Parameters.AddWithValue("$asset", leg.Asset.Id);
            entry.Parameters.AddWithValue("$epoch", leg.Asset.Epoch);
            entry.Parameters.AddWithValue("$delta", leg.Delta);
            entry.Parameters.AddWithValue("$reason", (int)leg.Reason);
            entry.Parameters.AddWithValue("$legKind", (int)leg.Kind);
            entry.Parameters.AddWithValue("$edge", leg.EdgeNumber);
            entry.Parameters.AddWithValue("$before", (object?)leg.BeforeFingerprint ?? DBNull.Value);
            entry.Parameters.AddWithValue("$after", (object?)leg.AfterFingerprint ?? DBNull.Value);
            entry.Parameters.AddWithValue("$source", intent.SourceKey);
            entry.ExecuteNonQuery();
        }
        return receipts;
    }

    private void ValidateShape()
    {
        if (child.Parts.Count is < 1 or > 16384 || child.Guards.Count > 16384 || intent.Payload.Length > 1024 * 1024)
            throw new InvalidOperationException("Invalid E03 typed child or intent bound.");
        if (intent.ChildRoleWrites.Length != child.Parts.Count ||
            child.Parts.Select((part, index) => (part, index)).Any(x =>
                !intent.ChildRoleWrites[x.index].SequenceEqual(x.part.RoleWrites
                    .Select(key => new EscrowRolePart(key.RoleId, key.Part)).Distinct()
                    .OrderBy(key => key.RoleId).ThenBy(key => key.Part, StringComparer.Ordinal))))
            throw new InvalidOperationException("E03 child role-write declarations differ from the staged plan.");
        foreach (var part in child.Parts.Concat(child.Guards))
        {
            string name = part.GetType().Name;
            if (name is not ("MarketItemsWritePlan" or "MarketCurrencyWritePlan" or "MarketBlobWritePlan" or
                "MarketRecordWritePlan" or "MarketRecordGuardPlan" or "MarketRoleGuardPlan" or
                "RoleBlobGuardPlan" or "RoleBlobWritePlan" or "RoleProgressWritePlan" or
                "ItemDeltaWritePlan" or "ItemSnapshotWritePlan" or "ItemAwardUpsertPlan" or
                "RoleAwardStatePlan" or "FinancialMailAppendPlan" or "AuctionItemWritePlan" or
                "AuctionPrebuyWritePlan" or "AuctionBonusWritePlan" or "AuctionBonusClaimPlan" or
                "AuctionSourceClaimPlan" or "AuctionRunSavePlan" or "AuctionRunClearPlan" or
                "AuctionPersonalCasPlan" or "GuildSavePlan" or "GuildMarketSavePlan" or
                "GuildMaterialContributionPlan" or "GuildCaravanRewardPlan" or "GuildEventAppendPlan" or
                "FriendGiftWritePlan" or "FriendGiftGuardPlan" or "FriendGiftSourcePlan" or
                "ActivityEveGlobalPlan" or "ActivityPreDonationPlan" or "ActivityInventoryGuardPlan" or
                "CaravanMemberWritePlan"))
                throw new InvalidOperationException("Unclassified E03 typed child: " + name);
        }
        foreach (var blob in child.Parts.OfType<RoleBlobWritePlan>())
            if (blob.Column is not ("mailbox" or "new_activities" or "activity_runtime" or
                "tasks" or "task_runtime" or "task_followup" or "daily" or "library" or "bag_state" or
                "trade_info" or "stalls_info" or "auction" or "guild_info" or
                "season_page" or "season_page_meta"))
                throw new InvalidOperationException("Unclassified E03 role blob: " + blob.Column);
        foreach (var entity in EntityWrites.Concat(EntityDeltaWrites))
            if (!entity.Kind.StartsWith("market_record:", StringComparison.Ordinal) &&
                entity.Kind is not ("market_kind" or "auction_item" or "auction_source" or "auction_prebuy" or
                    "auction_run" or "guild" or "guild_market" or "guild_caravan_member" or
                    "activity_eve_global"))
                throw new InvalidOperationException("Unclassified E03 entity write: " + entity.Kind);
        foreach (var change in intent.Changes)
            if (change.Asset.Kind is EconomyAssetKind.Item or EconomyAssetKind.Currency &&
                (!catalog.TryAsset(change.Asset.Id, out var actual) || actual != change.Asset))
                throw new InvalidOperationException("E03 external denomination disagrees with catalogue.");
        foreach (var realization in intent.Realizations)
            if (!RolePins.Contains(realization.RoleId) ||
                !catalog.TryAsset(realization.Asset.Id, out var realizedAsset) || realizedAsset != realization.Asset)
                throw new InvalidOperationException("E03 mail realization lacks a pinned role or item denomination.");
    }

    private EconomySnapshotReader.EscrowScope Scope(object?[]? receipts)
    {
        var roles = RolePins.Concat(child.RoleWrites.Select(x => x.RoleId)).ToHashSet();
        var markets = new HashSet<(string Kind, string Id)>();
        var auctions = new HashSet<string>(StringComparer.Ordinal);
        var guilds = new HashSet<long>();
        var caravanRoles = new HashSet<long>();
        var prebuyRoles = child.RoleWrites.Where(x => x.Part == RevisionStore.AuctionOrder)
            .Select(x => x.RoleId).ToHashSet();
        var bonusRoles = child.RoleWrites.Where(x => x.Part == RevisionStore.AuctionBonus)
            .Select(x => x.RoleId).ToHashSet();
        bool guildMarket = false;
        foreach (var entity in child.EntityWrites.Concat(child.EntityDeltaWrites))
        {
            if (entity.Kind.StartsWith("market_record:", StringComparison.Ordinal))
                markets.Add((entity.Kind[14..], entity.Id));
            else if (entity.Kind == "auction_item") auctions.Add(entity.Id);
            else if (entity.Kind == "guild" && long.TryParse(entity.Id, out long guild)) guilds.Add(guild);
            else if (entity.Kind == "guild_market") guildMarket = true;
            else if (entity.Kind == "guild_caravan_member" && long.TryParse(entity.Id, out long caravanRole))
                caravanRoles.Add(caravanRole);
        }
        if (receipts is not null)
            for (int i = 0; i < child.Parts.Count; i++)
                if (child.Parts[i] is GuildSavePlan && receipts[i] is long guild && guild > 0)
                    guilds.Add(guild);
        return new(roles, markets, auctions, guilds, guildMarket, caravanRoles, prebuyRoles, bonusRoles);
    }

    private List<EconomyLeg> BuildLegs(
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> before,
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> after,
        IReadOnlyDictionary<EconomyLocation, long> inventoryBinds)
    {
        var result = new List<EconomyLeg>();
        int edge = 0;
        var assets = before.Keys.Select(x => x.Asset).Concat(after.Keys.Select(x => x.Asset))
            .Concat(intent.Changes.Select(x => x.Asset)).Distinct()
            .OrderBy(x => x.Kind).ThenBy(x => x.Id).ThenBy(x => x.Epoch, StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            var changes = before.Keys.Concat(after.Keys).Where(x => x.Asset == asset).Distinct()
                .OrderBy(x => x.Location.Owner.Kind).ThenBy(x => x.Location.Owner.Id, StringComparer.Ordinal)
                .ThenBy(x => x.Location.Compartment).ThenBy(x => x.Location.Key, StringComparer.Ordinal)
                .ThenBy(x => x.Location.Instance, StringComparer.Ordinal)
                .Select(key => new Movement(key.Location, before.GetValueOrDefault(key), after.GetValueOrDefault(key),
                    inventoryBinds.TryGetValue(key.Location, out long bind) ? bind : null))
                .ToArray();
            var realizations = intent.Realizations.Where(x => x.Asset == asset)
                .Select(x => new MailRealizationRemainder(x)).ToArray();
            var realizedDestinations = new Dictionary<EconomyLocation, string>();
            foreach (var realization in realizations)
            {
                var sourceMovement = changes.SingleOrDefault(x => x.Location == realization.Value.SourceLocation);
                if (sourceMovement?.Old?.Quantity != realization.Value.Quantity ||
                    sourceMovement.New is not null ||
                    sourceMovement.Old.Fingerprint != realization.Value.AbstractFingerprint)
                    throw new InvalidOperationException("E03 mail realization does not match its exact abstract attachment.");
            }
            long source = Sum(EconomyLegKind.Source), sink = Sum(EconomyLegKind.Sink);
            long net = 0;
            foreach (var movement in changes) net = checked(net + movement.Delta);
            if (net != checked(source - sink))
                throw new InvalidOperationException($"E03 actual asset delta disagrees with authorized external changes: {asset}.");
            var negatives = changes.Where(x => x.Delta < 0).Select(x => new Remainder(x, -x.Delta)).ToList();
            var positives = changes.Where(x => x.Delta > 0).Select(x => new Remainder(x, x.Delta)).ToList();
            // With both a sink and a source of this asset authorized (a system stall's retire and restock), only the
            // movements their authorizations may consume explain them: an unrelated transfer of the same asset in the
            // same operation (a closing player stall's stock to mail) must not count as the retired holding. Those
            // other movements must then balance as pure transfers.
            var sinks = intent.Changes.Where(x => x.Asset == asset && x.Kind == EconomyLegKind.Sink).ToArray();
            var sources = intent.Changes.Where(x => x.Asset == asset && x.Kind == EconomyLegKind.Source).ToArray();
            bool both = sinks.Length > 0 && sources.Length > 0;
            long grossNegative = negatives.Where(x => !both || sinks.Any(a => Eligible(a, x.Move.Location)))
                .Aggregate(0L, (n, x) => checked(n + x.Quantity));
            long grossPositive = positives.Where(x => !both || sources.Any(a => Eligible(a, x.Move.Location)))
                .Aggregate(0L, (n, x) => checked(n + x.Quantity));
            long overlap = checked(sink - grossNegative);
            if (overlap != checked(source - grossPositive))
                throw new InvalidOperationException("E03 external changes disagree with concrete movements.");
            if (overlap > 0)
            {
                foreach (var movement in changes.Where(x => x.Old is not null && x.New is not null &&
                    intent.Changes.Any(a => a.Asset == asset && a.Kind == EconomyLegKind.Sink && Eligible(a, x.Location)) &&
                    intent.Changes.Any(a => a.Asset == asset && a.Kind == EconomyLegKind.Source && Eligible(a, x.Location))))
                {
                    if (overlap == 0) break;
                    long amount = Math.Min(overlap, Math.Min(movement.Old!.Quantity, movement.New!.Quantity));
                    if (amount == 0) continue;
                    negatives.Add(new Remainder(movement, amount));
                    positives.Add(new Remainder(movement, amount));
                    overlap -= amount;
                }
                if (overlap != 0) throw new InvalidOperationException("E03 same-location source/sink lacks retained holding.");
            }
            foreach (var authorization in intent.Changes.Where(x => x.Asset == asset && x.Kind == EconomyLegKind.Sink))
                Consume(negatives, authorization);
            foreach (var authorization in intent.Changes.Where(x => x.Asset == asset && x.Kind == EconomyLegKind.Source))
                Consume(positives, authorization);
            // Preserve exact InvItem transfers before pairing abstract configured grants with resolved stacks.
            foreach (var debit in negatives.OrderBy(x =>
                realizations.Any(r => r.Value.SourceLocation == x.Move.Location) ? 1 : 0))
            {
                while (debit.Quantity > 0)
                {
                    var realization = realizations.FirstOrDefault(x => x.Value.SourceLocation == debit.Move.Location);
                    if (realization is not null && realization.Quantity == 0)
                        throw new InvalidOperationException("E03 mail realization exceeded its authorized amount.");
                    bool Compatible(Remainder credit) => credit.Quantity > 0 &&
                        credit.Move.Location != debit.Move.Location &&
                        (asset.Kind != EconomyAssetKind.Item ||
                         (realization is not null
                            ? credit.Move.Location.Owner == debit.Move.Location.Owner &&
                              credit.Move.Location.Compartment == EconomyCompartment.Inventory &&
                              credit.Move.NewBind == realization.Value.Bind &&
                              (debit.Move.Old?.Fingerprint == credit.Move.New?.Fingerprint ||
                               credit.Move.Old is null)
                            : debit.Move.Old?.Fingerprint == credit.Move.New?.Fingerprint));
                    // Refunds, reserve release and claims stay with their owner before matching
                    // cross-owner sale or gift edges of the same denomination.
                    var credit = realization is not null
                        ? positives.FirstOrDefault(x => Compatible(x) &&
                            debit.Move.Old?.Fingerprint == x.Move.New?.Fingerprint)
                        : null;
                    credit ??= positives.FirstOrDefault(x => Compatible(x) &&
                        x.Move.Location.Owner == debit.Move.Location.Owner)
                        ?? positives.FirstOrDefault(Compatible)
                        ?? throw new InvalidOperationException("E03 transfer remainder has no matching counterparty.");
                    long amount = Math.Min(debit.Quantity, credit.Quantity);
                    if (realization is not null) amount = Math.Min(amount, realization.Quantity);
                    bool materialized = realization is not null &&
                        debit.Move.Old?.Fingerprint != credit.Move.New?.Fingerprint;
                    if (materialized && (credit.Move.Old is not null ||
                        credit.Move.New?.Quantity != amount || credit.Quantity != amount ||
                        realizedDestinations.ContainsKey(credit.Move.Location)))
                        throw new InvalidOperationException("E03 randomized mail attachment must occupy one new destination stack.");
                    int number = ++edge;
                    Add(debit.Move, -amount, EconomyLegKind.Transfer, intent.TransferReason, number);
                    Add(credit.Move, amount, EconomyLegKind.Transfer, intent.TransferReason, number);
                    if (materialized)
                    {
                        realizedDestinations.Add(credit.Move.Location, debit.Move.Old!.Fingerprint);
                        Add(credit.Move, 0, EconomyLegKind.Metadata, EconomyReason.ItemMetadata, 0);
                    }
                    debit.Quantity -= amount; credit.Quantity -= amount;
                    if (realization is not null) realization.Quantity -= amount;
                }
            }
            if (realizations.Any(x => x.Quantity != 0))
                throw new InvalidOperationException("E03 mail realization has no complete transfer edge.");
            if (negatives.Any(x => x.Quantity != 0) || positives.Any(x => x.Quantity != 0))
                throw new InvalidOperationException("E03 transfer remainder is unbalanced.");
            foreach (var movement in changes.Where(x => x.Delta == 0 && x.Old?.Fingerprint != x.New?.Fingerprint))
                Add(movement, 0, EconomyLegKind.Metadata, EconomyReason.ItemMetadata, 0);
            // A location can have several legs. Preserve a continuous fingerprint chain in their ledger order.
            foreach (var group in result.Where(x => x.Asset == asset).GroupBy(x => x.Location))
            {
                var movement = changes.Single(x => x.Location == group.Key);
                string? old = movement.Old?.Fingerprint is { Length: > 0 } prior ? prior : null;
                string? next = movement.New?.Fingerprint is { Length: > 0 } current ? current : null;
                var positions = group.Select(x => x.LegNumber).ToArray();
                if (asset.Kind != EconomyAssetKind.Item) continue;
                string? currentState = old;
                foreach (int position in positions)
                {
                    var leg = result[position];
                    string? afterState = leg.Kind == EconomyLegKind.Metadata || leg.Delta > 0 ? next : currentState;
                    if (leg.Kind == EconomyLegKind.Transfer && leg.Delta > 0 &&
                        realizedDestinations.TryGetValue(group.Key, out var abstractFingerprint))
                        afterState = abstractFingerprint;
                    if (position == positions[^1]) afterState = next;
                    result[position] = leg with { BeforeFingerprint = currentState, AfterFingerprint = afterState };
                    currentState = afterState;
                }
            }

            long Sum(EconomyLegKind kind) => intent.Changes.Where(x => x.Asset == asset && x.Kind == kind)
                .Aggregate(0L, (sum, x) => checked(sum + x.Quantity));
            void Consume(List<Remainder> pool, EscrowExternalChange authorization)
            {
                long quantity = authorization.Quantity;
                foreach (var remainder in pool.Where(x => Eligible(authorization, x.Move.Location)))
                {
                    if (quantity == 0) break;
                    long amount = Math.Min(quantity, remainder.Quantity);
                    if (amount == 0) continue;
                    Add(remainder.Move, authorization.Kind == EconomyLegKind.Sink ? -amount : amount,
                        authorization.Kind, authorization.Reason, 0);
                    remainder.Quantity -= amount; quantity -= amount;
                }
                if (quantity != 0) throw new InvalidOperationException("E03 authorized amount exceeds concrete holding delta.");
            }
            void Add(Movement movement, long delta, EconomyLegKind kind, EconomyReason reason, int number)
                => result.Add(new EconomyLeg(0, intent.OperationId, result.Count, kind, reason, asset,
                    movement.Location, delta, number,
                    BeforeFingerprint: asset.Kind == EconomyAssetKind.Item ? movement.Old?.Fingerprint : null,
                    AfterFingerprint: asset.Kind == EconomyAssetKind.Item ? movement.New?.Fingerprint : null,
                    SourceKey: intent.SourceKey));
            static bool Eligible(EscrowExternalChange authorization, EconomyLocation location)
            {
                if (authorization.Compartment is { } part && location.Compartment != part ||
                    authorization.Owner is { } owner && location.Owner != owner) return false;
                return authorization.Reason switch
                {
                    EconomyReason.MailExpiry => location.Compartment is EconomyCompartment.Mail or EconomyCompartment.LegacyMail,
                    EconomyReason.SystemRetire => location.Compartment == EconomyCompartment.Stall &&
                        location.Owner.Kind == EconomyOwnerKind.System,
                    EconomyReason.SystemRestock => location.Compartment == EconomyCompartment.Stall &&
                        location.Owner.Kind == EconomyOwnerKind.System,
                    EconomyReason.GuildMarketCapDiscard => location.Compartment is EconomyCompartment.GuildMarket or EconomyCompartment.Recovery,
                    _ => true
                };
            }
        }
        return result;
    }

    private sealed record Movement(EconomyLocation Location, EconomyHolding? Old, EconomyHolding? New, long? NewBind)
    {
        internal long Delta => checked((New?.Quantity ?? 0) - (Old?.Quantity ?? 0));
    }
    private sealed class Remainder(Movement move, long quantity)
    {
        internal Movement Move { get; } = move;
        internal long Quantity { get; set; } = quantity;
    }
    private sealed class MailRealizationRemainder(EscrowMailRealization value)
    {
        internal EscrowMailRealization Value { get; } = value;
        internal long Quantity { get; set; } = value.Quantity;
    }
}

internal sealed class EscrowEconomyReceiptResolveReadPlan(EscrowEconomyIntent intent, RoleItemPlan child) : DbReadPlan
{
    internal override int Bytes => 1024 + intent.Payload.Length;
    internal override string Label => "economy/e03-resolve";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx) => Read(db, tx, intent, child);
    internal static object?[]? Read(SqliteConnection db, SqliteTransaction tx, EscrowEconomyIntent intent, RoleItemPlan child)
    {
        using var receipt = DbSql.Command(db, tx, """
            SELECT realm_id,actor_account,actor_role,command_kind,request_fingerprint,intent_hash,intent_payload,
                source_key,outcome,child_receipts,leg_count FROM operation_receipt WHERE operation_id=$op
            """);
        receipt.Parameters.AddWithValue("$op", intent.OperationId);
        using var row = receipt.ExecuteReader();
        if (!row.Read())
        {
            row.Close();
            using var source = DbSql.Command(db, tx, "SELECT operation_id FROM operation_source WHERE source_key=$key");
            source.Parameters.AddWithValue("$key", intent.SourceKey);
            if (source.ExecuteScalar() is string used)
                throw new EconomyOperationConflictException("E03 natural source belongs to " + used);
            return null;
        }
        byte[] payload = intent.Payload;
        if (row.GetString(1) != intent.ActorAccount || row.GetInt64(2) != intent.ActorRoleId ||
            row.GetString(3) != intent.CommandKind || row.GetString(4) != intent.Hash ||
            row.GetString(5) != intent.Hash || !((byte[])row.GetValue(6)).AsSpan().SequenceEqual(payload) ||
            row.GetString(7) != intent.SourceKey ||
            !((byte[])row.GetValue(8)).AsSpan().SequenceEqual((byte[])row.GetValue(9)))
            throw new EconomyOperationConflictException("E03 operation ID belongs to another intent.");
        string realm = row.GetString(0);
        var result = EscrowEconomyReceiptCodec.Decode((byte[])row.GetValue(9));
        EscrowEconomyReceiptCodec.Validate(child, result);
        int legs = row.GetInt32(10);
        if (legs < 0) throw new InvalidDataException("Negative E03 ledger leg count.");
        row.Close();
        using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
        if (meta.ExecuteScalar() is not string actual || actual != realm)
            throw new InvalidDataException("E03 receipt has a foreign realm.");
        using var sourceClaim = DbSql.Command(db, tx,
            "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$key AND operation_id=$op");
        sourceClaim.Parameters.AddWithValue("$realm", realm);
        sourceClaim.Parameters.AddWithValue("$key", intent.SourceKey);
        sourceClaim.Parameters.AddWithValue("$op", intent.OperationId);
        if (sourceClaim.ExecuteScalar() is null) throw new InvalidDataException("E03 source claim is missing.");
        FamilyEconomyTransactionPlan.ValidateLegCount(db, tx, intent.OperationId, legs);
        return result;
    }
}

internal static class EscrowEconomyReceiptCodec
{
    private sealed record Value(string Kind, JsonElement Data);
    internal static byte[] Encode(object?[] values)
    {
        if (values.Length is < 1 or > 16384) throw new InvalidDataException("Invalid E03 child receipt count.");
        return JsonSerializer.SerializeToUtf8Bytes(values.Select(EncodeOne).ToArray());
    }
    private static Value EncodeOne(object? value) => value switch
    {
        null => new("null", JsonSerializer.SerializeToElement(0)),
        bool v => new("bool", JsonSerializer.SerializeToElement(v)),
        int v => new("int", JsonSerializer.SerializeToElement(v)),
        long v => new("long", JsonSerializer.SerializeToElement(v)),
        string v => new("string", JsonSerializer.SerializeToElement(v)),
        long[] v => new("ids", JsonSerializer.SerializeToElement(v)),
        byte[] v => new("bytes", JsonSerializer.SerializeToElement(v)),
        AuctionItemWriteReceipt v => new("auction", JsonSerializer.SerializeToElement(v)),
        _ => throw new InvalidDataException("Unsupported E03 child receipt " + value.GetType().Name)
    };
    internal static object?[] Decode(byte[] payload)
    {
        if (payload.Length > 64 * 1024 * 1024) throw new InvalidDataException("E03 child receipt is too large.");
        var values = JsonSerializer.Deserialize<Value[]>(payload) ?? throw new InvalidDataException("Missing E03 children.");
        if (values.Length is < 1 or > 16384) throw new InvalidDataException("Invalid E03 child count.");
        return values.Select(x => x.Kind switch
        {
            "null" => null,
            "bool" => (object?)x.Data.GetBoolean(),
            "int" => x.Data.GetInt32(),
            "long" => x.Data.GetInt64(),
            "string" => x.Data.GetString(),
            "ids" => x.Data.Deserialize<long[]>(),
            "bytes" => x.Data.Deserialize<byte[]>(),
            "auction" => x.Data.Deserialize<AuctionItemWriteReceipt>(),
            _ => throw new InvalidDataException("Unknown E03 child receipt kind.")
        }).ToArray();
    }
    internal static void Validate(RoleItemPlan child, object?[] values)
    {
        if (values.Length != child.Parts.Count) throw new InvalidDataException("E03 child receipt count mismatch.");
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is long[] ids && ids.Any(id => id <= 0))
                throw new InvalidDataException("Invalid E03 generated IDs.");
            if (child.Parts[i] is AuctionItemWritePlan && values[i] is not AuctionItemWriteReceipt { Id: > 0 })
                throw new InvalidDataException("Invalid E03 auction item receipt.");
            int? expected = child.Parts[i] switch
            {
                MarketItemsWritePlan market => market.ExpectedReceiptCount,
                ItemDeltaWritePlan delta => delta.ExpectedReceiptCount,
                ItemSnapshotWritePlan snapshot => snapshot.ExpectedReceiptCount,
                _ => null
            };
            if (expected is { } count && (values[i] is not long[] received || received.Length != count))
                throw new InvalidDataException("Invalid E03 item receipt length or type.");
            if (child.Parts[i] is ItemDeltaWritePlan && values[i] is long[] unique &&
                unique.Distinct().Count() != unique.Length)
                throw new InvalidDataException("Duplicate E03 delta generated IDs.");
        }
    }
}
