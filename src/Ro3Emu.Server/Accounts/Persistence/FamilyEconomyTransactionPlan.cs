using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>E05a/b: one converted C/D typed family commit and its retained exact economic evidence.</summary>
internal sealed class FamilyEconomyTransactionPlan(RoleItemPlan child, EconomyMutation mutation,
    bool failAcknowledgementForTest = false) : DbPlan
{
    internal static IReadOnlySet<string> AllowedBlobsForKind(string kind)
    {
        if (CharacterFamilyEconomy.IsKind(kind)) return CharacterFamilyEconomy.AllowedBlobs(kind);
        string family = kind[6..].Split('-')[0];
        return family switch
        {
            // bag_state: a 204 SelfChoosePet box (cd13b/pet-self-choose) commits its item use, cooldowns included.
            "pet" => new HashSet<string>(["pets", "pet_books", "lottery", "bag_state", "library", "tasks",
                "task_runtime", "task_followup"], StringComparer.Ordinal),
            // career_special: a Special Class Item lease (cd13b/mount-career) also selects the battle mount slot.
            "mount" => new HashSet<string>(["mounts", "wardrobe", "career_special", "bag_state", "library", "tasks", "task_runtime",
                "task_followup"], StringComparer.Ordinal),
            "wardrobe" => new HashSet<string>(["wardrobe", "bag_state", "library", "tasks", "task_runtime",
                "task_followup"], StringComparer.Ordinal),
            "totem" => new HashSet<string>(["totems", "totem_refine_epoch", "library", "tasks",
                "task_runtime", "task_followup"], StringComparer.Ordinal),
            "emblem" => new HashSet<string>(["emblems", "library", "tasks", "task_runtime",
                "task_followup"], StringComparer.Ordinal),
            "genre" => new HashSet<string>(["genre", "stat_plans", "skill_plans", "equip_plans",
                "card_plans", "totems", "emblems", "pets", "season_talent", "library", "tasks",
                "task_runtime", "task_followup"], StringComparer.Ordinal),
            // The Odin statue of Valhalla Trials (cd13b/valhalla-pray): coins in, statue EXP and level out.
            "valhalla" => new HashSet<string>(["heroic_spirit", "library", "tasks", "task_runtime",
                "task_followup"], StringComparer.Ordinal),
            // Astral Level Star Cores (cd13b/paragon-*): a core installed leaves the bag, a removed one comes back.
            "paragon" => new HashSet<string>(["paragon", "library", "tasks", "task_runtime",
                "task_followup"], StringComparer.Ordinal),
            _ => throw new InvalidOperationException("Unknown converted C/D command kind.")
        };
    }
    private int pendingAcknowledgementFault = failAcknowledgementForTest ? 1 : 0;
    private bool isReplay;
    internal override bool IsReplay => isReplay;
    internal override void AfterCommit()
    {
        if (Interlocked.Exchange(ref pendingAcknowledgementFault, 0) != 0)
            throw new IOException("Test-only lost economy commit acknowledgement.");
    }
    internal override int Bytes => checked(child.Bytes * 2 + 4096 + mutation.IntentPayload.Length +
        mutation.Outcome.Length + (mutation.Costs.Count + mutation.Goods.Count) * 256);
    internal override string Label => "economy/" + mutation.CommandKind;
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => child.RoleWrites;
    internal override IReadOnlyList<long> RolePins => child.RolePins;
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => child.EntityWrites;
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites => child.EntityDeltaWrites;
    internal override IReadOnlyList<RoleRevisionKey> MutatedRoleWrites(object? result) => child.MutatedRoleWrites(result);
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityWrites(object? result) => child.MutatedEntityWrites(result);
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityDeltaWrites(object? result) => child.MutatedEntityDeltaWrites(result);
    internal override void ResolveRoleWrites(AccountStore accounts) => child.ResolveRoleWrites(accounts);

    internal override bool TryReplay(SqliteConnection db, SqliteTransaction tx, out object? result)
    {
        ValidateShape();
        using var command = DbSql.Command(db, tx, """
            SELECT realm_id,actor_account,actor_role,command_kind,request_fingerprint,intent_hash,
                   intent_payload,source_key,outcome,child_receipts,leg_count
            FROM operation_receipt WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", mutation.OperationId);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            row.Close();
            using var source = DbSql.Command(db, tx, "SELECT operation_id FROM operation_source WHERE source_key=$key");
            source.Parameters.AddWithValue("$key", mutation.SourceKey);
            if (source.ExecuteScalar() is string used)
                throw new EconomyOperationConflictException("Natural source already committed by operation " + used);
            result = null;
            return false;
        }
        if (row.GetString(1) != mutation.ActorAccount || row.GetInt64(2) != mutation.ActorRoleId ||
            row.GetString(3) != mutation.CommandKind || row.GetString(4) != mutation.RequestFingerprint ||
            row.GetString(5) != mutation.IntentHash ||
            !((byte[])row.GetValue(6)).AsSpan().SequenceEqual(mutation.IntentPayload) ||
            row.GetString(7) != mutation.SourceKey ||
            !((byte[])row.GetValue(8)).AsSpan().SequenceEqual(mutation.Outcome))
            throw new EconomyOperationConflictException("Committed operation ID belongs to another actor or intent.");
        string realm = row.GetString(0);
        var values = EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(9));
        ValidateResults(values);
        int legCount = row.GetInt32(10);
        if (legCount < 0) throw new InvalidDataException("Negative family ledger leg count.");
        row.Close();
        ValidateSource(db, tx, realm);
        ValidateLegCount(db, tx, mutation.OperationId, legCount);
        result = values;
        isReplay = true;
        return true;
    }

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        ValidateShape();
        using (var owner = DbSql.Command(db, tx, "SELECT account FROM role WHERE id=$role"))
        {
            owner.Parameters.AddWithValue("$role", mutation.ActorRoleId);
            if (owner.ExecuteScalar() is not string account || account != mutation.ActorAccount)
                throw new EconomyOperationConflictException("Authenticated account does not own the family role.");
        }
        string realm;
        using (var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1"))
            realm = meta.ExecuteScalar() as string ?? throw new InvalidDataException("Missing economy realm.");
        var before = EconomyRoleAssetRead.Read(db, tx, mutation.ActorRoleId);
        var receipts = (object?[])child.Execute(db, tx);
        ValidateResults(receipts);
        var after = EconomyRoleAssetRead.Read(db, tx, mutation.ActorRoleId);
        var legs = BuildLegs(before, after);
        long sequence;
        using (var advance = DbSql.Command(db, tx,
            "UPDATE economy_meta SET next_sequence=next_sequence+1 WHERE singleton=1 RETURNING next_sequence"))
            sequence = (long?)advance.ExecuteScalar() ?? throw new InvalidDataException("Missing economy watermark.");
        long recorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expected = ExpectedRevisions?[mutation.ActorRoleId] ?? throw new InvalidOperationException("Missing role vector.");
        var beforeParts = RoleWrites.Select(x => x.Part).Distinct().Order(StringComparer.Ordinal)
            .ToDictionary(part => part, expected.Revision, StringComparer.Ordinal);
        var afterParts = beforeParts.ToDictionary(x => x.Key, x => checked(x.Value + 1), StringComparer.Ordinal);
        using (var receipt = DbSql.Command(db, tx, """
            INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,
                child_receipts,before_revisions,after_revisions,leg_count)
            VALUES($op,$realm,$seq,$account,$role,$kind,$fingerprint,$hash,$payload,$source,$recorded,$effective,
                $outcome,$children,$before,$after,$count)
            """))
        {
            receipt.Parameters.AddWithValue("$op", mutation.OperationId);
            receipt.Parameters.AddWithValue("$realm", realm);
            receipt.Parameters.AddWithValue("$seq", sequence);
            receipt.Parameters.AddWithValue("$account", mutation.ActorAccount);
            receipt.Parameters.AddWithValue("$role", mutation.ActorRoleId);
            receipt.Parameters.AddWithValue("$kind", mutation.CommandKind);
            receipt.Parameters.AddWithValue("$fingerprint", mutation.RequestFingerprint);
            receipt.Parameters.AddWithValue("$hash", mutation.IntentHash);
            receipt.Parameters.AddWithValue("$payload", mutation.IntentPayload);
            receipt.Parameters.AddWithValue("$source", mutation.SourceKey);
            receipt.Parameters.AddWithValue("$recorded", recorded);
            receipt.Parameters.AddWithValue("$effective", mutation.EffectiveUtcMs);
            receipt.Parameters.AddWithValue("$outcome", mutation.Outcome);
            receipt.Parameters.AddWithValue("$children", EconomyTransactionPlan.EncodeChildReceipts(receipts));
            receipt.Parameters.AddWithValue("$before", JsonSerializer.Serialize(new
            { roleId = expected.RoleId, epoch = expected.Epoch, deleted = expected.Deleted, parts = beforeParts }));
            receipt.Parameters.AddWithValue("$after", JsonSerializer.Serialize(new
            { roleId = expected.RoleId, epoch = expected.Epoch, deleted = expected.Deleted, parts = afterParts }));
            receipt.Parameters.AddWithValue("$count", legs.Count);
            receipt.ExecuteNonQuery();
        }
        using (var claim = DbSql.Command(db, tx,
            "INSERT INTO operation_source(realm_id,source_key,operation_id) VALUES($realm,$key,$op)"))
        {
            claim.Parameters.AddWithValue("$realm", realm);
            claim.Parameters.AddWithValue("$key", mutation.SourceKey);
            claim.Parameters.AddWithValue("$op", mutation.OperationId);
            claim.ExecuteNonQuery();
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
            entry.Parameters.AddWithValue("$op", mutation.OperationId);
            entry.Parameters.AddWithValue("$no", leg.LegNumber);
            entry.Parameters.AddWithValue("$recorded", recorded);
            entry.Parameters.AddWithValue("$effective", mutation.EffectiveUtcMs);
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
            entry.Parameters.AddWithValue("$source", mutation.SourceKey);
            entry.ExecuteNonQuery();
        }
        return receipts;
    }

    private void ValidateShape()
    {
        if (!mutation.IsFamily) throw new InvalidOperationException("Unknown converted C/D command kind.");
        var allowedBlobs = AllowedBlobsForKind(mutation.CommandKind);
        string[] genreParts = ["genre", "stat_plans", "skill_plans", "equip_plans", "card_plans",
            "totems", "emblems", "pets", "season_talent"];
        if (!mutation.IsFamily || child.Parts.Count is < 1 or > 32 ||
            child.Parts[0] is not ItemDeltaWritePlan ||
            child.Parts.Skip(1).Any(part => part switch
            {
                RoleProgressWritePlan => false,
                RoleBlobWritePlan blob => !allowedBlobs.Contains(blob.Column),
                _ => true
            }) ||
            child.Guards.Any(guard => guard is not RoleBlobGuardPlan blob || !allowedBlobs.Contains(blob.Column)) ||
            RolePins.Count != 1 || RolePins[0] != mutation.ActorRoleId ||
            RoleWrites.Any(key => key.RoleId != mutation.ActorRoleId ||
                key.Part != RevisionStore.Inventory && key.Part != RevisionStore.Progress &&
                !allowedBlobs.Contains(key.Part)) ||
            !RoleWrites.Contains(RevisionStore.Role(mutation.ActorRoleId, RevisionStore.Inventory)) ||
            RoleWrites.Distinct().Count() != child.Parts.Count ||
            mutation.CommandKind == "cd13b/genre-apply" &&
                (child.Parts.Count != 11 || !child.Parts.Any(part => part is RoleProgressWritePlan) ||
                 genreParts.Any(name => child.Parts.Count(part => part is RoleBlobWritePlan blob &&
                     blob.Column == name) != 1)) ||
            EntityWrites.Count != 0 || EntityDeltaWrites.Count != 0)
            throw new InvalidOperationException("Family economy requires actor ItemDelta then allowlisted role state.");
    }

    internal void ValidateResults(object?[] values)
    {
        if (values.Length != child.Parts.Count || values[0] is not long[] ids ||
            ids.Length != ((ItemDeltaWritePlan)child.Parts[0]).ExpectedReceiptCount ||
            ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length ||
            values.Skip(1).Any(value => value is not int number || number != 1))
            throw new InvalidDataException("Family typed child receipts are invalid.");
    }

    private void ValidateSource(SqliteConnection db, SqliteTransaction tx, string realm)
    {
        using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
        if (meta.ExecuteScalar() is not string actual || actual != realm)
            throw new InvalidDataException("Foreign economy realm receipt.");
        using var source = DbSql.Command(db, tx,
            "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$key AND operation_id=$op");
        source.Parameters.AddWithValue("$realm", realm);
        source.Parameters.AddWithValue("$key", mutation.SourceKey);
        source.Parameters.AddWithValue("$op", mutation.OperationId);
        if (source.ExecuteScalar() is null) throw new InvalidDataException("Family receipt lost natural source claim.");
    }

    internal static void ValidateLegCount(SqliteConnection db, SqliteTransaction tx, string operation, int claimed)
    {
        using var counts = DbSql.Command(db, tx,
            "SELECT COUNT(*),MIN(leg_no),MAX(leg_no) FROM economy_entry WHERE operation_id=$op");
        counts.Parameters.AddWithValue("$op", operation);
        using var row = counts.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != claimed || claimed > 0 &&
            (row.GetInt64(1) != 0 || row.GetInt64(2) != claimed - 1))
            throw new InvalidDataException("Family receipt has missing or nonconsecutive ledger legs.");
    }

    private List<EconomyLeg> BuildLegs(
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> before,
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> after)
    {
        static Dictionary<EconomyAsset, long> Sum(IReadOnlyList<EconomyQuantity> quantities)
        {
            var values = new Dictionary<EconomyAsset, long>();
            foreach (var q in quantities) values[q.Asset] = checked(values.GetValueOrDefault(q.Asset) + q.Quantity);
            return values;
        }
        var costs = Sum(mutation.Costs);
        var goods = Sum(mutation.Goods);
        var legs = new List<EconomyLeg>();
        int edge = 0;
        string family = mutation.CommandKind[6..].Split('-')[0];
        var reasons = CharacterFamilyEconomy.IsKind(mutation.CommandKind)
            ? CharacterFamilyEconomy.Reasons(mutation.CommandKind) : family switch
            {
                "pet" => (EconomyReason.PetCost,
                    mutation.CommandKind == "cd13b/pet-rebirth" ? EconomyReason.PetRefund : EconomyReason.PetGoods),
                "mount" => (EconomyReason.MountCost, EconomyReason.MountGoods),
                "wardrobe" => (EconomyReason.WardrobeCost, EconomyReason.WardrobeGoods),
                "totem" => (EconomyReason.TotemCost,
                    mutation.CommandKind == "cd13b/totem-upgrade" ? EconomyReason.TotemRefund : EconomyReason.TotemGoods),
                "emblem" => (EconomyReason.EmblemCost, EconomyReason.EmblemGoods),
                "genre" => (EconomyReason.GenreCost, EconomyReason.GenreGoods),
                "valhalla" => (EconomyReason.ValhallaCost, EconomyReason.ValhallaCost), // no goods: a goods leg is refused
                "paragon" => (EconomyReason.ParagonCost, EconomyReason.ParagonGoods),
                _ => throw new InvalidOperationException("Unknown C/D 13b family.")
            };
        foreach (var asset in before.Keys.Select(x => x.Asset).Concat(after.Keys.Select(x => x.Asset))
            .Concat(costs.Keys).Concat(goods.Keys).Distinct().OrderBy(x => x.Kind).ThenBy(x => x.Id))
        {
            var opening = before.Values.Where(x => x.Asset == asset).OrderBy(x => x.Location.Key, StringComparer.Ordinal)
                .Select(x => new Remaining(x)).ToArray();
            var closing = after.Values.Where(x => x.Asset == asset).OrderBy(x => x.Location.Key, StringComparer.Ordinal)
                .Select(x => new Remaining(x)).ToArray();
            long cost = costs.GetValueOrDefault(asset), good = goods.GetValueOrDefault(asset);
            long oldTotal = opening.Sum(x => x.Quantity), newTotal = closing.Sum(x => x.Quantity);
            if (checked(newTotal - oldTotal) != checked(good - cost))
                throw new InvalidOperationException($"Family semantic cost/goods disagree with committed asset {asset}.");
            var matches = new List<(EconomyHolding Old, EconomyHolding New, long Quantity)>();
            // Retain exact item instances before assigning a semantic debit. Otherwise an emblem feed
            // with the same entry as its target could debit the target GUID instead of the removed feed.
            foreach (var old in opening)
                foreach (var next in closing.Where(x => x.Quantity > 0 &&
                    (asset.Kind == EconomyAssetKind.Item
                        ? x.Holding.Location.Instance == old.Holding.Location.Instance
                        : x.Holding.Location == old.Holding.Location)))
                {
                    long amount = Math.Min(old.Quantity, next.Quantity);
                    if (amount == 0) break;
                    matches.Add((old.Holding, next.Holding, amount));
                    old.Quantity -= amount; next.Quantity -= amount;
                }
            foreach (var item in opening)
            {
                long amount = Math.Min(cost, item.Quantity);
                if (amount == 0) continue;
                Add(EconomyLegKind.Sink, reasons.Item1, asset, item.Holding.Location, -amount, 0,
                    item.Holding.Fingerprint,
                    after.GetValueOrDefault((asset, item.Holding.Location))?.Fingerprint);
                item.Quantity -= amount; cost -= amount;
            }
            foreach (var item in closing)
            {
                long amount = Math.Min(good, item.Quantity);
                if (amount == 0) continue;
                Add(EconomyLegKind.Source, reasons.Item2, asset, item.Holding.Location, amount, 0,
                    before.GetValueOrDefault((asset, item.Holding.Location))?.Fingerprint,
                    item.Holding.Fingerprint);
                item.Quantity -= amount; good -= amount;
            }
            // A same-denomination cost and result can cancel visibly. Bind any remaining overlap to
            // concrete retained holdings rather than dropping either side of the semantic intent.
            if (cost > 0 && good > 0)
                for (int i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    long amount = Math.Min(Math.Min(cost, good), match.Quantity);
                    if (amount == 0) continue;
                    Add(EconomyLegKind.Sink, reasons.Item1, asset, match.Old.Location, -amount, 0,
                        match.Old.Fingerprint, match.Old.Fingerprint);
                    Add(EconomyLegKind.Source, reasons.Item2, asset, match.New.Location, amount, 0,
                        match.New.Fingerprint, match.New.Fingerprint);
                    matches[i] = (match.Old, match.New, match.Quantity - amount);
                    cost -= amount; good -= amount;
                    if (cost == 0 || good == 0) break;
                }
            if (cost != 0 || good != 0) throw new InvalidOperationException("Semantic amount exceeds concrete holdings.");
            // Residual units have no semantic source/sink and therefore require an explicit transfer.
            foreach (var old in opening.Where(x => x.Quantity > 0))
                while (old.Quantity > 0)
                {
                    var next = closing.Where(x => x.Quantity > 0)
                        .OrderByDescending(x => asset.Kind == EconomyAssetKind.Item &&
                            x.Holding.Location.Instance == old.Holding.Location.Instance)
                        .ThenByDescending(x => x.Holding.Location == old.Holding.Location).FirstOrDefault();
                    if (next is null) throw new InvalidOperationException("Unmatched family asset holding.");
                    long amount = Math.Min(old.Quantity, next.Quantity);
                    matches.Add((old.Holding, next.Holding, amount));
                    old.Quantity -= amount; next.Quantity -= amount;
                }
            if (closing.Any(x => x.Quantity != 0)) throw new InvalidOperationException("Unmatched family closing asset.");
            foreach (var match in matches)
            {
                if (match.Quantity == 0) continue;
                if (match.Old.Location == match.New.Location)
                {
                    if (match.Old.Fingerprint != match.New.Fingerprint &&
                        !legs.Any(leg => leg.Asset == asset && leg.Location == match.Old.Location) &&
                        !matches.Any(other => other.Old.Location != other.New.Location &&
                            (other.Old.Location == match.Old.Location ||
                             other.New.Location == match.Old.Location)))
                        Add(EconomyLegKind.Metadata, EconomyReason.ItemMetadata, asset, match.Old.Location,
                            0, 0, match.Old.Fingerprint, match.New.Fingerprint);
                }
                else
                {
                    int pair = ++edge;
                    Add(EconomyLegKind.Transfer, EconomyReason.InventoryRelocation, asset,
                        match.Old.Location, -match.Quantity, pair, match.Old.Fingerprint,
                        after.GetValueOrDefault((asset, match.Old.Location))?.Fingerprint);
                    Add(EconomyLegKind.Transfer, EconomyReason.InventoryRelocation, asset,
                        match.New.Location, match.Quantity, pair,
                        before.GetValueOrDefault((asset, match.New.Location))?.Fingerprint,
                        match.New.Fingerprint);
                }
            }
        }
        // One item location may emit several debit/credit legs (for example a stack split into
        // multiple socket GUIDs). Chain each fingerprint through that location's leg order.
        foreach (var group in legs.Select((leg, index) => (leg, index))
            .Where(x => x.leg.Asset.Kind == EconomyAssetKind.Item)
            .GroupBy(x => (x.leg.Asset, x.leg.Location)))
        {
            string? opening = before.GetValueOrDefault(group.Key)?.Fingerprint;
            string? closing = after.GetValueOrDefault(group.Key)?.Fingerprint;
            string? intermediate = opening ?? closing;
            int[] numbers = group.Select(x => x.index).Order().ToArray();
            for (int i = 0; i < numbers.Length; i++)
            {
                int number = numbers[i];
                legs[number] = legs[number] with
                {
                    BeforeFingerprint = i == 0 ? opening : intermediate,
                    AfterFingerprint = i == numbers.Length - 1 ? closing : intermediate
                };
            }
        }
        return legs;

        void Add(EconomyLegKind kind, EconomyReason reason, EconomyAsset asset, EconomyLocation location,
            long delta, int edgeNumber, string? prior, string? next) =>
            legs.Add(new EconomyLeg(0, mutation.OperationId, legs.Count, kind, reason, asset, location,
                delta, edgeNumber, BeforeFingerprint: asset.Kind == EconomyAssetKind.Item ? prior : null,
                AfterFingerprint: asset.Kind == EconomyAssetKind.Item ? next : null, SourceKey: mutation.SourceKey));
    }

    private sealed class Remaining(EconomyHolding holding)
    {
        internal EconomyHolding Holding { get; } = holding;
        internal long Quantity { get; set; } = holding.Quantity;
    }
}
