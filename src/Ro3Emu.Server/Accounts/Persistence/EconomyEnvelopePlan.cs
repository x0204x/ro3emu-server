using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>E05d typed owner plan, semantic value authorization, receipt and exact legs in one transaction.</summary>
internal sealed class EconomyEnvelopePlan(RoleItemPlan child, EconomyEnvelope envelope, EconomyCatalog catalog,
    bool failAcknowledgementForTest = false) : DbPlan
{
    private int pendingAcknowledgementFault = failAcknowledgementForTest ? 1 : 0;
    private bool replay;
    internal override string OperationId => envelope.OperationId;
    internal override bool OwnsOperationReceipt => true;
    internal override bool IsReplay => replay;
    internal override void AfterCommit()
    {
        if (Interlocked.Exchange(ref pendingAcknowledgementFault, 0) != 0)
            throw new IOException("Test-only lost economy commit acknowledgement.");
    }
    internal override int Bytes => checked(child.Bytes * 2 + 4096 + envelope.IntentPayload.Length +
        envelope.Outcome.Length + envelope.Effects.Count * 256);
    internal override string Label => "economy/" + envelope.CommandKind;
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
        if (!envelope.HasChildIntent) throw new InvalidOperationException("E05d typed child intent was not bound.");
        using var command = DbSql.Command(db, tx, """
            SELECT actor_account,actor_role,command_kind,request_fingerprint,source_key,intent_hash,intent_payload,
                outcome,child_receipts,leg_count,realm_id FROM operation_receipt WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", envelope.OperationId);
        using var row = command.ExecuteReader();
        if (row.Read())
        {
            if (row.GetString(0) != envelope.ActorAccount || row.GetInt64(1) != envelope.ActorRoleId ||
                row.GetString(2) != envelope.CommandKind || row.GetString(3) != envelope.RequestFingerprint ||
                row.GetString(4) != envelope.SourceKey || row.GetString(5) != envelope.IntentHash ||
                !((byte[])row.GetValue(6)).AsSpan().SequenceEqual(envelope.IntentPayload) ||
                !((byte[])row.GetValue(7)).AsSpan().SequenceEqual(envelope.Outcome))
                throw new EconomyOperationConflictException("Committed E05d operation belongs to different actor or intent.");
            object?[] children = EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(8));
            if (children.Length != child.ChildCount || row.GetInt32(9) < 0)
                throw new InvalidDataException("Committed E05d receipt has an invalid result or leg count.");
            string realm = row.GetString(10);
            row.Close();
            VerifyClaim(db, tx, realm);
            result = children;
            replay = true;
            return true;
        }
        row.Close();
        using var source = DbSql.Command(db, tx, "SELECT operation_id FROM operation_source WHERE source_key=$key");
        source.Parameters.AddWithValue("$key", envelope.SourceKey);
        if (source.ExecuteScalar() is string used)
            throw new EconomyOperationConflictException("Natural source already committed by operation " + used);
        result = null;
        return false;
    }

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (!envelope.HasChildIntent) throw new InvalidOperationException("E05d typed child intent was not bound.");
        if (child.ChildCount == 0) throw new InvalidOperationException("E05d envelope needs a typed child plan.");
        using (var owner = DbSql.Command(db, tx, "SELECT account FROM role WHERE id=$role"))
        {
            owner.Parameters.AddWithValue("$role", envelope.ActorRoleId);
            if (owner.ExecuteScalar() is not string account || account != envelope.ActorAccount)
                throw new EconomyOperationConflictException("Authenticated account does not own the E05d actor role.");
        }
        string realm;
        using (var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1"))
            realm = meta.ExecuteScalar() as string ?? throw new InvalidOperationException("Missing economy realm identity.");
        var beforeEntities = EntityWrites.Concat(EntityDeltaWrites).Distinct()
            .ToDictionary(key => key, key => ReadEntity(db, tx, key));
        var generatedEntities = new HashSet<OwnerRevisionKey>();
        var before = Read(db, tx);
        var children = (object?[])child.Execute(db, tx);
        EconomyTransactionPlan.EncodeChildReceipts(children); // Reject any non-durable child result before receipt work.
        // Guild creation obtains its owner ID inside the writer. Its initial revision is inserted by
        // GuildSavePlan, rather than by RevisionStore.Bump, so record that creation explicitly.
        for (int i = 0; i < child.Parts.Count; i++)
        {
            if (child.Parts[i] is not GuildSavePlan guild || guild.EntityWrites.Count != 0) continue;
            if (children[i] is not long guildId || guildId <= 0)
                throw new InvalidDataException("E05d guild creation returned no durable owner ID.");
            var key = RevisionStore.Entity("guild", guildId);
            if (!beforeEntities.TryAdd(key, new OwnerRevisionValue(key, 0, false)))
                throw new InvalidDataException("E05d guild creation reused an existing owner revision.");
            generatedEntities.Add(key);
            if (ReadEntity(db, tx, key) is not { Exists: true, Revision: 1 })
                throw new InvalidDataException("E05d guild creation did not install initial owner revision.");
        }
        var after = Read(db, tx);
        List<EconomyLeg> legs = Match(before, after, ResolveEffects(db, tx, children));
        long sequence;
        using (var advance = DbSql.Command(db, tx,
            "UPDATE economy_meta SET next_sequence=next_sequence+1 WHERE singleton=1 RETURNING next_sequence"))
            sequence = (long?)advance.ExecuteScalar() ?? throw new InvalidOperationException("Missing economy watermark.");
        long recorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var mutatedEntities = child.MutatedEntityWrites(children)
            .Concat(child.MutatedEntityDeltaWrites(children)).ToHashSet();
        if (mutatedEntities.Except(beforeEntities.Keys).Any())
            throw new InvalidDataException("E05d mutated an entity outside its captured participant set.");
        var orderedEntities = beforeEntities.Values.OrderBy(x => x.Key.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Id, StringComparer.Ordinal).ThenBy(x => x.Key.Part, StringComparer.Ordinal).ToArray();
        string beforeVector = JsonSerializer.Serialize(new
        {
            roles = ExpectedRevisions?.Values.OrderBy(x => x.RoleId).Select(x => new
            { x.RoleId, x.Epoch, x.Deleted, parts = x.Parts.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value) }),
            entities = orderedEntities
        });
        string afterVector = JsonSerializer.Serialize(new
        {
            roles = ExpectedRevisions?.Values.OrderBy(x => x.RoleId).Select(x => new
            {
                x.RoleId, x.Epoch, x.Deleted, parts = x.Parts.OrderBy(p => p.Key).ToDictionary(p => p.Key,
                    p => child.MutatedRoleWrites(children).Contains(RevisionStore.Role(x.RoleId, p.Key)) ? checked(p.Value + 1) : p.Value)
            }),
            entities = orderedEntities.Select(x => mutatedEntities.Contains(x.Key) || generatedEntities.Contains(x.Key)
                ? new OwnerRevisionValue(x.Key, checked(x.Revision + 1), true) : x)
        });
        using (var receipt = DbSql.Command(db, tx, """
            INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,
                child_receipts,before_revisions,after_revisions,leg_count)
            VALUES($op,$realm,$seq,$account,$role,$kind,$fingerprint,$hash,$payload,$source,$recorded,$effective,
                $outcome,$children,$before,$after,$count)
            """))
        {
            receipt.Parameters.AddWithValue("$op", envelope.OperationId);
            receipt.Parameters.AddWithValue("$realm", realm);
            receipt.Parameters.AddWithValue("$seq", sequence);
            receipt.Parameters.AddWithValue("$account", envelope.ActorAccount);
            receipt.Parameters.AddWithValue("$role", envelope.ActorRoleId);
            receipt.Parameters.AddWithValue("$kind", envelope.CommandKind);
            receipt.Parameters.AddWithValue("$fingerprint", envelope.RequestFingerprint);
            receipt.Parameters.AddWithValue("$hash", envelope.IntentHash);
            receipt.Parameters.AddWithValue("$payload", envelope.IntentPayload);
            receipt.Parameters.AddWithValue("$source", envelope.SourceKey);
            receipt.Parameters.AddWithValue("$recorded", recorded);
            receipt.Parameters.AddWithValue("$effective", envelope.EffectiveUtcMs);
            receipt.Parameters.AddWithValue("$outcome", envelope.Outcome);
            receipt.Parameters.AddWithValue("$children", EconomyTransactionPlan.EncodeChildReceipts(children));
            receipt.Parameters.AddWithValue("$before", beforeVector);
            receipt.Parameters.AddWithValue("$after", afterVector);
            receipt.Parameters.AddWithValue("$count", legs.Count);
            receipt.ExecuteNonQuery();
        }
        using (var source = DbSql.Command(db, tx,
            "INSERT INTO operation_source(realm_id,source_key,operation_id) VALUES($realm,$source,$op)"))
        {
            source.Parameters.AddWithValue("$realm", realm);
            source.Parameters.AddWithValue("$source", envelope.SourceKey);
            source.Parameters.AddWithValue("$op", envelope.OperationId);
            source.ExecuteNonQuery();
        }
        foreach (var leg in legs)
        {
            using var entry = DbSql.Command(db, tx, """
                INSERT INTO economy_entry(commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,
                    owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,
                    delta,reason_code,leg_kind,edge_no,before_fingerprint,after_fingerprint,source_key)
                VALUES($seq,$op,$no,$recorded,$effective,$ownerKind,$owner,$compartment,$key,$instance,$assetKind,$asset,
                    $epoch,$delta,$reason,$legKind,$edge,$before,$after,$source)
                """);
            entry.Parameters.AddWithValue("$seq", sequence);
            entry.Parameters.AddWithValue("$op", envelope.OperationId);
            entry.Parameters.AddWithValue("$no", leg.LegNumber);
            entry.Parameters.AddWithValue("$recorded", recorded);
            entry.Parameters.AddWithValue("$effective", envelope.EffectiveUtcMs);
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
            entry.Parameters.AddWithValue("$source", envelope.SourceKey);
            entry.ExecuteNonQuery();
        }
        return children;
    }

    private void VerifyClaim(SqliteConnection db, SqliteTransaction tx, string realm)
    {
        using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
        if (meta.ExecuteScalar() is not string actualRealm || actualRealm != realm)
            throw new InvalidDataException("E05d receipt realm differs from database identity.");
        using var claim = DbSql.Command(db, tx,
            "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$key AND operation_id=$op");
        claim.Parameters.AddWithValue("$realm", realm);
        claim.Parameters.AddWithValue("$key", envelope.SourceKey);
        claim.Parameters.AddWithValue("$op", envelope.OperationId);
        if (claim.ExecuteScalar() is null) throw new InvalidDataException("E05d receipt lost its source claim.");
    }

    private static OwnerRevisionValue ReadEntity(SqliteConnection db, SqliteTransaction tx, OwnerRevisionKey key)
    {
        using var revision = DbSql.Command(db, tx,
            "SELECT revision FROM owner_revision WHERE kind=$kind AND id=$id AND part=$part");
        revision.Parameters.AddWithValue("$kind", key.Kind);
        revision.Parameters.AddWithValue("$id", key.Id);
        revision.Parameters.AddWithValue("$part", key.Part);
        return revision.ExecuteScalar() is long value
            ? new OwnerRevisionValue(key, value, true) : new OwnerRevisionValue(key, 0, false);
    }

    private static Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> Read(
        SqliteConnection db, SqliteTransaction tx, EconomyCatalog catalog)
    {
        var decoded = EconomySnapshotReader.ReadTransaction(db, tx, catalog);
        if (decoded.Issues.Any(x => x.Kind is EconomyIssueKind.Corruption or EconomyIssueKind.UnknownSchema or EconomyIssueKind.Coverage))
            throw new InvalidDataException("E05d economic decoder found unclassified/corrupt stored value: " +
                decoded.Issues.First(x => x.Kind is EconomyIssueKind.Corruption or EconomyIssueKind.UnknownSchema or EconomyIssueKind.Coverage).Detail);
        var result = new Dictionary<(EconomyAsset, EconomyLocation), EconomyHolding>();
        foreach (var holding in decoded.Holdings)
            if (!result.TryAdd((holding.Asset, holding.Location), holding))
                throw new InvalidDataException("Duplicate economic holding location.");
        return result;
    }

    private Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> Read(
        SqliteConnection db, SqliteTransaction tx) => Read(db, tx, catalog);

    private IReadOnlyList<EconomyEffect> ResolveEffects(SqliteConnection db, SqliteTransaction tx, object?[] children)
    {
        const string alias = "auction-source:";
        if (!envelope.Effects.Any(x => x.Owner.Kind == EconomyOwnerKind.Escrow &&
            x.Owner.Id.StartsWith(alias, StringComparison.Ordinal))) return envelope.Effects;
        var claimed = child.Parts.Select((part, index) => (part, index))
            .Where(x => x.part is AuctionSourceClaimPlan && children[x.index] is true)
            .Select(x => ((AuctionSourceClaimPlan)x.part).SourceKey).ToHashSet(StringComparer.Ordinal);
        var lots = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < child.Parts.Count; i++)
        {
            if (child.Parts[i] is not AuctionItemWritePlan { CreatesNew: true } plan) continue;
            if (children[i] is not AuctionItemWriteReceipt receipt || receipt.Id <= 0)
                throw new InvalidDataException("E05d auction source has no typed created-lot receipt.");
            using (var state = JsonDocument.Parse(receipt.State))
                if (state.RootElement.GetProperty("Id").GetInt64() != receipt.Id ||
                    state.RootElement.GetProperty("SourceKey").GetString() != plan.SourceKey)
                    throw new InvalidDataException("E05d auction lot result changed identity.");
            using var row = DbSql.Command(db, tx, "SELECT source_key,state FROM auction_item WHERE id=$id");
            row.Parameters.AddWithValue("$id", receipt.Id);
            using var stored = row.ExecuteReader();
            if (!stored.Read() || stored.GetString(0) != plan.SourceKey ||
                !((byte[])stored.GetValue(1)).AsSpan().SequenceEqual(receipt.State) ||
                !lots.TryAdd(plan.SourceKey, receipt.Id))
                throw new InvalidDataException("E05d auction lot receipt has no exact stored source row.");
        }
        return envelope.Effects.Select(effect =>
        {
            if (effect.Owner.Kind != EconomyOwnerKind.Escrow ||
                !effect.Owner.Id.StartsWith(alias, StringComparison.Ordinal)) return effect;
            string source = effect.Owner.Id[alias.Length..];
            if (source.Length == 0 || effect.Asset.Kind != EconomyAssetKind.Item ||
                effect.Compartment != EconomyCompartment.AuctionLot || effect.Kind != EconomyLegKind.Source ||
                effect.Delta != 1 || !claimed.Contains(envelope.SourceKey) ||
                !IsAuctionLotSource(source, envelope.SourceKey) || !lots.TryGetValue(source, out long id))
                throw new InvalidDataException("E05d auction source effect has no claimed created lot.");
            return effect with
            {
                Owner = new EconomyOwner(EconomyOwnerKind.Escrow,
                "auction:" + id.ToString(CultureInfo.InvariantCulture))
            };
        }).ToArray();
    }

    private static bool IsAuctionLotSource(string lotSource, string batchSource)
    {
        if (!lotSource.StartsWith(batchSource + ":", StringComparison.Ordinal)) return false;
        string suffix = lotSource[(batchSource.Length + 1)..];
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
            index >= 0 && suffix == index.ToString(CultureInfo.InvariantCulture);
    }

    private List<EconomyLeg> Match(
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> before,
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> after,
        IReadOnlyList<EconomyEffect> effects)
    {
        var changes = new List<(EconomyAsset Asset, EconomyLocation Location, long Delta, string? Before, string? After)>();
        foreach (var key in before.Keys.Concat(after.Keys).Distinct())
        {
            before.TryGetValue(key, out var old);
            after.TryGetValue(key, out var next);
            long delta = checked((next?.Quantity ?? 0) - (old?.Quantity ?? 0));
            if (delta == 0 && old?.Fingerprint != next?.Fingerprint)
                throw new InvalidOperationException("E05d changed value metadata without a declared metadata effect.");
            if (delta != 0) changes.Add((key.Asset, key.Location, delta,
                key.Asset.Kind == EconomyAssetKind.Item ? old?.Fingerprint : null,
                key.Asset.Kind == EconomyAssetKind.Item ? next?.Fingerprint : null));
        }
        static (EconomyAsset, EconomyOwner, EconomyCompartment) EffectKey(EconomyEffect effect) =>
            (effect.Asset, effect.Owner, effect.Compartment);
        static (EconomyAsset, EconomyOwner, EconomyCompartment) ChangeKey(
            (EconomyAsset Asset, EconomyLocation Location, long Delta, string? Before, string? After) change) =>
            (change.Asset, change.Location.Owner, change.Location.Compartment);
        var expected = effects.GroupBy(EffectKey).ToDictionary(x => x.Key,
            x => x.Aggregate(BigInteger.Zero, (sum, effect) => sum + effect.Delta));
        var actual = changes.GroupBy(ChangeKey).ToDictionary(x => x.Key,
            x => x.Aggregate(BigInteger.Zero, (sum, change) => sum + change.Delta));
        foreach (var key in expected.Keys.Concat(actual.Keys).Distinct())
            if (expected.GetValueOrDefault(key) != actual.GetValueOrDefault(key))
                throw new InvalidOperationException("E05d stored value changed outside semantic authorization: " + key);
        var remaining = changes.ToDictionary(x => (x.Asset, x.Location), x => x.Delta);
        var legs = new List<EconomyLeg>();
        int nextEdge = 1;
        foreach (var effect in effects.Where(x => x.Kind != EconomyLegKind.Transfer))
            Allocate(effect, null);
        foreach (var edge in effects.Where(x => x.Kind == EconomyLegKind.Transfer).GroupBy(x => x.EdgeNumber))
        {
            var pair = edge.ToArray();
            var debit = pair.Single(x => x.Delta < 0);
            var credit = pair.Single(x => x.Delta > 0);
            var from = Allocate(debit, null, emit: false);
            var to = Allocate(credit, null, emit: false);
            int i = 0, j = 0;
            while (i < from.Count && j < to.Count)
            {
                long amount = Math.Min(-from[i].Delta, to[j].Delta);
                var left = from[i] with { Delta = -amount };
                var right = to[j] with { Delta = amount };
                int edgeNumber = nextEdge++;
                legs.Add(Leg(debit, left, edgeNumber, right.Location));
                legs.Add(Leg(credit, right, edgeNumber, left.Location));
                from[i] = from[i] with { Delta = checked(from[i].Delta + amount) };
                to[j] = to[j] with { Delta = checked(to[j].Delta - amount) };
                if (from[i].Delta == 0) i++;
                if (to[j].Delta == 0) j++;
            }
            if (i != from.Count || j != to.Count) throw new InvalidOperationException("E05d transfer allocation mismatch.");
        }
        if (remaining.Values.Any(x => x != 0))
            throw new InvalidOperationException("E05d has undeclared concrete holding movement.");
        return legs;

        List<(EconomyAsset Asset, EconomyLocation Location, long Delta, string? Before, string? After)> Allocate(
            EconomyEffect effect, EconomyLocation? counterpart, bool emit = true)
        {
            long left = effect.Delta;
            var slices = new List<(EconomyAsset, EconomyLocation, long, string?, string?)>();
            foreach (var change in changes.Where(x => ChangeKey(x) == EffectKey(effect) && Math.Sign(x.Delta) == Math.Sign(effect.Delta))
                .OrderBy(x => x.Location.Key, StringComparer.Ordinal).ThenBy(x => x.Location.Instance, StringComparer.Ordinal))
            {
                long available = remaining[(change.Asset, change.Location)];
                if (Math.Sign(available) != Math.Sign(left)) continue;
                long amount = Math.Min(Math.Abs(left), Math.Abs(available)) * Math.Sign(left);
                var slice = change with { Delta = amount };
                slices.Add(slice);
                remaining[(change.Asset, change.Location)] = checked(available - amount);
                left = checked(left - amount);
                if (left == 0) break;
            }
            if (left != 0) throw new InvalidOperationException("E05d effect has no matching concrete holding delta.");
            if (emit) foreach (var slice in slices) legs.Add(Leg(effect, slice, 0, counterpart));
            return slices;
        }
        EconomyLeg Leg(EconomyEffect effect,
            (EconomyAsset Asset, EconomyLocation Location, long Delta, string? Before, string? After) slice,
            int edge, EconomyLocation? counterpart) =>
            new(0, envelope.OperationId, legs.Count, effect.Kind, effect.Reason, effect.Asset, slice.Location,
                slice.Delta, edge, counterpart, slice.Before, slice.After, envelope.SourceKey);
    }
}
