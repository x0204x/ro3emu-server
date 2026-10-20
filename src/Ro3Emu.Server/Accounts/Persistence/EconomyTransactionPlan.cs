using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One E01 NPC purchase: existing typed children plus semantic receipt and exact role asset legs.</summary>
internal sealed class EconomyTransactionPlan(RoleItemPlan child, EconomyMutation mutation,
    bool failAcknowledgementForTest = false, bool owned = false) : DbPlan
{
    private int pendingAcknowledgementFault = failAcknowledgementForTest ? 1 : 0;
    private bool isReplay;
    // E04 owned rewards retain their generic writer acknowledgement after economic replay validation.
    // E01 resolves through EconomyReceiptResolveReadPlan; semantic E05d has its own envelope plan.
    internal override bool OwnsOperationReceipt => !owned;
    internal override bool IsReplay => isReplay;
    internal override string? OperationId => owned ? mutation.OperationId : null;
    internal override void AfterCommit()
    {
        if (Interlocked.Exchange(ref pendingAcknowledgementFault, 0) != 0)
            throw new IOException("Test-only lost economy commit acknowledgement.");
    }
    // The second child allowance covers generated-ID receipts and per-location legs for split stacks.
    internal override int Bytes => checked(child.Bytes * 2 + 4096 + mutation.Outcome.Length + mutation.IntentPayload.Length +
        (mutation.OperationId.Length + mutation.ActorAccount.Length + mutation.CommandKind.Length +
         mutation.RequestFingerprint.Length + mutation.SourceKey.Length + mutation.IntentHash.Length) * 2 +
        (mutation.Costs.Count + mutation.Goods.Count) * 256);
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
        using var receipt = DbSql.Command(db, tx, """
            SELECT actor_account,actor_role,command_kind,request_fingerprint,source_key,child_receipts,intent_hash,realm_id,leg_count,intent_payload,outcome
            FROM operation_receipt WHERE operation_id=$op
            """);
        receipt.Parameters.AddWithValue("$op", mutation.OperationId);
        using var row = receipt.ExecuteReader();
        if (row.Read())
        {
            if (row.GetString(0) != mutation.ActorAccount || row.GetInt64(1) != mutation.ActorRoleId ||
                row.GetString(2) != mutation.CommandKind || row.GetString(3) != mutation.RequestFingerprint ||
                row.GetString(4) != mutation.SourceKey || row.GetString(6) != mutation.IntentHash ||
                !((byte[])row.GetValue(9)).AsSpan().SequenceEqual(mutation.IntentPayload) ||
                !((byte[])row.GetValue(10)).AsSpan().SequenceEqual(mutation.Outcome))
                throw new EconomyOperationConflictException("Committed operation ID belongs to different actor or intent.");
            object?[] original = DecodeChildReceipts((byte[])row.GetValue(5));
            if (row.GetInt32(8) < (mutation.RewardEvidence is null && mutation.CommandKind != "cd13e/daily-reset" ? 1 : 0))
                throw new InvalidDataException("Committed economic receipt has an invalid leg count.");
            ValidateChildReceipts(original, child.Parts[0] is ItemDeltaWritePlan replayDelta
                ? replayDelta.ExpectedReceiptCount : -1, mutation.CommandKind);
            string storedRealm = row.GetString(7);
            row.Close();
            using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
            if (meta.ExecuteScalar() is not string actualRealm || actualRealm != storedRealm)
                throw new InvalidDataException("Committed purchase receipt has a foreign realm identity.");
            using var claimed = DbSql.Command(db, tx,
                "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$source AND operation_id=$op");
            claimed.Parameters.AddWithValue("$realm", storedRealm);
            claimed.Parameters.AddWithValue("$source", mutation.SourceKey);
            claimed.Parameters.AddWithValue("$op", mutation.OperationId);
            if (claimed.ExecuteScalar() is null)
                throw new InvalidDataException("Committed purchase receipt lost its natural source claim.");
            result = original;
            isReplay = true;
            return true;
        }
        row.Close();
        using var source = DbSql.Command(db, tx, "SELECT operation_id FROM operation_source WHERE source_key=$source");
        source.Parameters.AddWithValue("$source", mutation.SourceKey);
        if (source.ExecuteScalar() is string used)
            throw new EconomyOperationConflictException("Natural source already committed by operation " + used);
        result = null;
        return false;
    }

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        ValidateShape();
        using (var owner = DbSql.Command(db, tx, "SELECT account FROM role WHERE id=$role"))
        {
            owner.Parameters.AddWithValue("$role", mutation.ActorRoleId);
            if (owner.ExecuteScalar() is not string account || account != mutation.ActorAccount)
                throw new EconomyOperationConflictException("Authenticated account does not own the economic role.");
        }
        var before = EconomyRoleAssetRead.Read(db, tx, mutation.ActorRoleId);
        EconomyRoleProgress? beforeProgress = mutation.RewardEvidence is null
            ? null : EconomyRoleProgressRead.Read(db, tx, mutation.ActorRoleId);
        var receipts = (object?[])child.Execute(db, tx);
        ValidateChildReceipts(receipts, child.Parts[0] is ItemDeltaWritePlan committedDelta
            ? committedDelta.ExpectedReceiptCount : -1, mutation.CommandKind);
        var after = EconomyRoleAssetRead.Read(db, tx, mutation.ActorRoleId);
        if (child.Parts.Skip(2).OfType<DungeonObligationActivatePlan>().SingleOrDefault() is { } activation)
        {
            // Activation issues its own per-recipient earned receipts inside this same commit. Those Pending
            // holdings belong to those receipts, while this monster-kill receipt accounts for its own goods.
            using var sources = DbSql.Command(db, tx,
                "SELECT source_key FROM dungeon_reward_obligation WHERE room_id=$room AND activated=1");
            sources.Parameters.AddWithValue("$room", activation.RoomId);
            using var rows = sources.ExecuteReader();
            var prefixes = new List<string>();
            while (rows.Read()) prefixes.Add("dungeon/" + rows.GetString(0) + "/entry/");
            bool IsActivatedPending(EconomyLocation location) => location.Compartment == EconomyCompartment.Pending &&
                prefixes.Any(prefix => location.Key.StartsWith(prefix, StringComparison.Ordinal));
            before = before.Where(x => !IsActivatedPending(x.Key.Location)).ToDictionary(x => x.Key, x => x.Value);
            after = after.Where(x => !IsActivatedPending(x.Key.Location)).ToDictionary(x => x.Key, x => x.Value);
        }
        if (mutation.RewardEvidence is { } evidence)
        {
            var afterProgress = EconomyRoleProgressRead.Read(db, tx, mutation.ActorRoleId);
            if (beforeProgress != new EconomyRoleProgress(evidence.BeforeBaseLevel, evidence.BeforeBaseExp,
                    evidence.BeforeJobLevel, evidence.BeforeJobExp) ||
                afterProgress != new EconomyRoleProgress(evidence.AfterBaseLevel, evidence.AfterBaseExp,
                    evidence.AfterJobLevel, evidence.AfterJobExp))
                throw new InvalidOperationException("Committed role progression differs from frozen reward evidence.");
        }
        var legs = BuildLegs(before, after, db, tx);
        if (legs.Count == 0 && mutation.RewardEvidence is null && mutation.CommandKind != "cd13e/daily-reset")
            throw new InvalidOperationException("Economic purchase has no asset legs.");
        var expected = ExpectedRevisions?[mutation.ActorRoleId] ?? throw new InvalidOperationException("Missing captured role vector.");
        var beforeRevisions = RoleWrites.Where(x => x.RoleId == mutation.ActorRoleId).Select(x => x.Part)
            .Distinct().Order(StringComparer.Ordinal)
            .ToDictionary(part => part, expected.Revision, StringComparer.Ordinal);
        var mutated = child.MutatedRoleWrites(receipts);
        var mutatedParts = mutated.Where(x => x.RoleId == mutation.ActorRoleId)
            .Select(x => x.Part).ToHashSet(StringComparer.Ordinal);
        var afterRevisions = beforeRevisions.ToDictionary(x => x.Key,
            x => checked(x.Value + (mutatedParts.Contains(x.Key) ? 1 : 0)), StringComparer.Ordinal);
        var participantRoles = child.Parts.OfType<KillTaskProgressPlan>().SingleOrDefault()?.CreditedRoles
            .Where(role => role != mutation.ActorRoleId).ToArray() ?? [];
        string Revisions(Dictionary<string, long> parts, bool afterCommit)
        {
            if (participantRoles.Length == 0) return JsonSerializer.Serialize(new
            { roleId = expected.RoleId, epoch = expected.Epoch, deleted = expected.Deleted, parts });
            var participants = participantRoles.Select(role =>
            {
                var vector = ExpectedRevisions[role];
                var participantParts = new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["task_followup"] = checked(vector.Revision("task_followup") + (afterCommit ? 1 : 0)),
                    ["tasks"] = checked(vector.Revision("tasks") + (afterCommit ? 1 : 0))
                };
                return new { roleId = role, epoch = vector.Epoch, deleted = vector.Deleted, parts = participantParts };
            }).ToArray();
            return JsonSerializer.Serialize(new
            { roleId = expected.RoleId, epoch = expected.Epoch, deleted = expected.Deleted, parts, participants });
        }
        EconomyLedgerWrite.Append(db, tx, new EconomyLedgerCommit(mutation.OperationId, mutation.ActorAccount,
            mutation.ActorRoleId, mutation.CommandKind, mutation.RequestFingerprint, mutation.IntentHash,
            mutation.IntentPayload, mutation.SourceKey, mutation.EffectiveUtcMs, mutation.Outcome,
            EncodeChildReceipts(receipts), Revisions(beforeRevisions, false), Revisions(afterRevisions, true), legs));
        return receipts;
    }

    private void ValidateShape()
    {
        if (mutation.CommandKind == "cd13e/daily-reset") { ValidateDailyResetShape(); return; }
        if (EconomyMutation.IsOwnedExchangeCommand(mutation.CommandKind)) { ValidateOwnedExchangeShape(); return; }
        if (mutation.RewardEvidence is not null) { ValidateRewardShape(); return; }
        if (mutation.CommandKind != "npc-shop-buy") throw new InvalidOperationException("E01 covers one NPC purchase only.");
        if (child.Guards.Count != 0 || child.Parts.Count != 3 ||
            child.Parts[0] is not ItemDeltaWritePlan ||
            child.Parts[1] is not RoleProgressWritePlan ||
            child.Parts[2] is not RoleBlobWritePlan { Column: "shop" })
            throw new InvalidOperationException("E01 purchase requires the exact item/progress/shop typed child group.");
        if (RolePins.Count != 1 || RolePins[0] != mutation.ActorRoleId ||
            RoleWrites.Any(x => x.RoleId != mutation.ActorRoleId ||
                x.Part is not (RevisionStore.Inventory or RevisionStore.Progress or "shop")) ||
            !RoleWrites.Contains(RevisionStore.Role(mutation.ActorRoleId, RevisionStore.Inventory)) ||
            !RoleWrites.Contains(RevisionStore.Role(mutation.ActorRoleId, RevisionStore.Progress)) ||
            !RoleWrites.Contains(RevisionStore.Role(mutation.ActorRoleId, "shop")) ||
            EntityWrites.Count != 0 || EntityDeltaWrites.Count != 0)
            throw new InvalidOperationException("E01 envelope requires one role's item, progress and shop quota children.");
    }

    private void ValidateDailyResetShape()
    {
        if (mutation.RewardEvidence != null || mutation.Goods.Count != 0 ||
            mutation.Costs.Any(x => x.Asset != new EconomyAsset(EconomyAssetKind.Currency, 5)) ||
            mutation.Costs.Count > 1 || child.Guards.Count != 0 ||
            child.Parts is not [RoleDailyResetPlan] and not
                [RoleDailyResetPlan, RoleBlobWritePlan { Column: "wild_afk" }] ||
            RolePins.Count != 1 || RolePins[0] != mutation.ActorRoleId ||
            RoleWrites.Any(x => x.RoleId != mutation.ActorRoleId ||
                x.Part is not (RevisionStore.Progress or "daily" or "tasks" or "wild_afk")) ||
            EntityWrites.Count != 0 || EntityDeltaWrites.Count != 0)
            throw new InvalidOperationException("Daily reset needs one typed daily/tasks/currency child and optional AFK rollover.");
    }

    private void ValidateOwnedExchangeShape()
    {
        string[] sourceColumns = mutation.CommandKind switch
        {
            "cd13e/tower-milestone" => ["tower"],
            "cd13e/grant" or "cd13e/gm-grant" or "cd13e/character-exp" => [],
            "cd13e/quest-award" => ["tasks", "task_runtime"],
            "cd13e/achievement-award" => ["achievements"],
            "cd13e/advguild-award" => ["adventure_group"],
            "cd13e/monsterhunt-award" => ["area_concur"],
            "cd13e/guild-award" => ["guild_info"],
            "cd13e/growthclass-award" => ["growth_class"],
            "cd13e/library-award" => ["library"],
            "cd13e/firstheart-award" => ["first_heart"],
            "cd13e/daily-stage" => ["daily"],
            "cd13e/journey-victory" => ["new_activities", "dungeons"],
            _ => throw new InvalidOperationException("Unrecognized owned economic command.")
        };
        string[] columns = [.. sourceColumns, "daily", "library", "task_followup", "mounts", "activity_runtime"];
        bool award = mutation.CommandKind is "cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or
            "cd13e/journey-victory" or "cd13e/grant" or "cd13e/gm-grant";
        if (child.Parts.Count < 2 || child.Parts[0] is not ItemDeltaWritePlan ||
            (award ? child.Parts[1] is not RoleAwardStatePlan : child.Parts[1] is not RoleProgressWritePlan) ||
            child.Parts.Skip(2).Any(x => x is not RoleBlobWritePlan blob || !columns.Contains(blob.Column)) ||
            sourceColumns.Any(column => !RoleWrites.Any(x => x.Part == column)) ||
            child.Parts.Skip(2).OfType<RoleBlobWritePlan>().GroupBy(x => x.Column).Any(x => x.Count() > 1) ||
            child.Guards.Any(x => x is not RoleBlobGuardPlan blob || !columns.Contains(blob.Column)) ||
            RolePins.Count != 1 || RolePins[0] != mutation.ActorRoleId ||
            RoleWrites.Any(x => x.RoleId != mutation.ActorRoleId ||
                x.Part is not (RevisionStore.Inventory or RevisionStore.Progress) && !columns.Contains(x.Part)) ||
            EntityWrites.Count != 0 || EntityDeltaWrites.Count != 0)
            throw new InvalidOperationException("Owned economic exchange requires one role's item/progress and declared source children.");
    }

    private void ValidateRewardShape()
    {
        if ((mutation.CommandKind == "world-revive" && mutation.Goods.Count != 0) ||
            (mutation.CommandKind != "world-revive" && mutation.Costs.Count != 0))
            throw new InvalidOperationException("E04 reward command has unsupported costs or goods.");
        var taskProgress = child.Parts.OfType<KillTaskProgressPlan>().SingleOrDefault();
        var requiredRoles = new[] { mutation.ActorRoleId }
            .Concat(taskProgress?.CreditedRoles ?? []).Distinct().Order().ToArray();
        if (!EconomyMutation.IsRewardCommand(mutation.CommandKind) || child.Parts.Count < 2 ||
            child.Parts[0] is not ItemDeltaWritePlan || child.Parts[1] is not RoleProgressWritePlan ||
            !RolePins.Order().SequenceEqual(requiredRoles) ||
            (taskProgress != null && (mutation.CommandKind != "monster-kill" ||
                child.Parts[^1] != taskProgress || taskProgress.SourceKey != mutation.SourceKey)) ||
            RoleWrites.Any(x => x.RoleId != mutation.ActorRoleId &&
                (taskProgress == null || !taskProgress.CreditedRoles.Contains(x.RoleId) ||
                    x.Part is not ("tasks" or "task_followup"))) ||
            !RoleWrites.Contains(RevisionStore.Role(mutation.ActorRoleId, RevisionStore.Inventory)) ||
            !RoleWrites.Contains(RevisionStore.Role(mutation.ActorRoleId, RevisionStore.Progress)))
            throw new InvalidOperationException("E04 reward requires one role's item/progress typed child group.");
        string[] requiredColumns = mutation.CommandKind switch
        {
            "gather-yield" => ["lifejob", "life_restrict", "life_runtime"],
            "dungeon-reward" => ["dungeons"],
            "tower-reward" => ["tower"],
            "monster-kill" => [],
            "world-revive" => [],
            _ => throw new InvalidOperationException("Unknown E04 command.")
        };
        var blobs = child.Parts.Skip(2).OfType<RoleBlobWritePlan>().ToArray();
        string[] actualColumns = blobs.Select(x => x.Column).ToArray();
        var extra = child.Parts.Skip(2).ToArray();
        int cardSuffix = mutation.CommandKind is "gather-yield" or "dungeon-reward"
            ? extra is [.., RoleBlobWritePlan { Column: "library" }, RoleBlobWritePlan { Column: "task_followup" }] ? 2
                : extra is [.., RoleBlobWritePlan { Column: "library" }] ? 1 : 0
            : 0;
        string[] cardColumns = cardSuffix switch
        {
            1 => ["library"],
            2 => ["library", "task_followup"],
            _ => []
        };
        var sourceExtra = extra.Take(extra.Length - cardSuffix).ToArray();
        bool columnsValid = mutation.CommandKind == "monster-kill"
            ? true // The ordered typed grammar below validates the optional kill/card/AFK children.
            : actualColumns.SequenceEqual(requiredColumns.Concat(cardColumns));
        string[] allowedColumns = mutation.CommandKind == "monster-kill"
            ? ["library", "task_followup", "tower", "tasks", "wild_afk", "area_concur"]
            : mutation.CommandKind is "gather-yield" or "dungeon-reward"
                ? [.. requiredColumns, "library", "task_followup"] : requiredColumns;
        var activation = extra.OfType<DungeonObligationActivatePlan>().SingleOrDefault();
        var ordinaryExtra = extra.Where(x => x is not DungeonObligationActivatePlan and not KillTaskProgressPlan).ToArray();
        bool MonsterExtrasValid()
        {
            int index = 0;
            if (index < ordinaryExtra.Length && ordinaryExtra[index] is RoleBlobWritePlan { Column: "tower" })
            {
                index++;
                if (index >= ordinaryExtra.Length || ordinaryExtra[index++] is not TowerFirstPassWritePlan) return false;
            }
            if (index < ordinaryExtra.Length && ordinaryExtra[index] is RoleBlobWritePlan { Column: "wild_afk" }) index++;
            // Monster Hunt kill counters (MonsterHuntService), staged after the AFK child.
            if (index < ordinaryExtra.Length && ordinaryExtra[index] is RoleBlobWritePlan { Column: "area_concur" }) index++;
            if (index < ordinaryExtra.Length && ordinaryExtra[index] is RoleBlobWritePlan { Column: "library" })
            {
                index++;
                if (index < ordinaryExtra.Length && ordinaryExtra[index] is RoleBlobWritePlan { Column: "task_followup" }) index++;
            }
            return index == ordinaryExtra.Length;
        }
        var trailing = child.Parts.Skip(2 + actualColumns.Length).ToArray();
        bool sourceShape = mutation.CommandKind switch
        {
            "monster-kill" => (activation is null || extra[0] == activation &&
                    activation.KillSourceKey == mutation.SourceKey &&
                    ordinaryExtra.All(x => x is RoleBlobWritePlan { Column: "library" or "task_followup" or "wild_afk" or "area_concur" })) &&
                MonsterExtrasValid(),
            "gather-yield" => sourceExtra is [RoleBlobWritePlan { Column: "lifejob" },
                RoleBlobWritePlan { Column: "life_restrict" }, RoleBlobWritePlan { Column: "life_runtime" }],
            "dungeon-reward" => sourceExtra is [RoleBlobWritePlan { Column: "dungeons" }, DungeonObligationCompletePlan complete] &&
                complete.SourceKey == mutation.SourceKey ||
                sourceExtra is [RoleBlobWritePlan { Column: "dungeons" }, AuctionRunClearPlan, DungeonObligationCompletePlan completed] &&
                completed.SourceKey == mutation.SourceKey,
            "tower-reward" => trailing is [TowerFirstPassWritePlan],
            _ => trailing.Length == 0
        };
        if (!columnsValid ||
            child.Guards.Any(x => x is not RoleBlobGuardPlan guard || !allowedColumns.Contains(guard.Column)) ||
            (mutation.CommandKind is "tower-reward" or "world-revive") &&
                child.Parts.Skip(2).Take(actualColumns.Length).Any(x => x is not RoleBlobWritePlan) ||
            !sourceShape ||
            EntityWrites.Any(x => !AllowedEntity(x)) || EntityDeltaWrites.Any(x => !AllowedEntity(x)) ||
            RoleWrites.Any(x => x.Part is not (RevisionStore.Inventory or RevisionStore.Progress) &&
                !allowedColumns.Contains(x.Part) && !(mutation.CommandKind == "dungeon-reward" && x.Part == RevisionStore.AuctionOrder)))
            throw new InvalidOperationException("E04 reward has an unapproved source child, guard or revision part.");
        bool AllowedEntity(OwnerRevisionKey key) => mutation.CommandKind == "dungeon-reward" &&
            key.Kind is "auction_run" or "dungeon_reward_obligation";
    }

    private List<EconomyLeg> BuildLegs(Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> before,
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> after,
        SqliteConnection db, SqliteTransaction tx)
    {
        if (mutation.CommandKind == "dungeon-reward")
            return BuildDungeonTransferLegs(before, after, EconomyRoleAssetRead.ReadItemBinds(db, tx, mutation.ActorRoleId));
        var costs = Sum(mutation.Costs);
        var goods = Sum(mutation.Goods);
        var result = new List<EconomyLeg>();
        foreach (var asset in costs.Keys.Concat(goods.Keys).Concat(before.Keys.Select(x => x.Asset))
            .Concat(after.Keys.Select(x => x.Asset)).Distinct().OrderBy(x => (int)x.Kind).ThenBy(x => x.Id))
        {
            long cost = costs.GetValueOrDefault(asset), good = goods.GetValueOrDefault(asset);
            var keys = before.Keys.Concat(after.Keys).Where(x => x.Asset == asset).Distinct()
                .OrderBy(x => x.Location.Key, StringComparer.Ordinal).ThenBy(x => x.Location.Instance, StringComparer.Ordinal).ToArray();
            var changes = keys.Select(key => (Key: key, Before: before.GetValueOrDefault(key), After: after.GetValueOrDefault(key),
                Delta: checked((after.TryGetValue(key, out var a) ? a.Quantity : 0) -
                    (before.TryGetValue(key, out var b) ? b.Quantity : 0)))).ToArray();
            long negatives = 0, positives = 0;
            foreach (var change in changes)
            {
                if (change.Delta < 0) negatives = checked(negatives - change.Delta);
                if (change.Delta > 0) positives = checked(positives + change.Delta);
                if (change.Before is not null && change.After is not null &&
                    change.Before.Fingerprint != change.After.Fingerprint)
                    throw new InvalidOperationException("E01 purchase changed item metadata without a declared metadata leg.");
            }
            if (negatives > cost || positives > good || cost - negatives != good - positives)
                throw new InvalidOperationException($"Purchase asset delta disagrees with semantic cost/goods for {asset}.");
            long overlap = cost - negatives;
            if (overlap > 0)
            {
                foreach (var key in keys.Where(before.ContainsKey))
                {
                    if (overlap == 0) break;
                    long slice = Math.Min(overlap, before[key].Quantity);
                    string? start = Fingerprint(before[key]);
                    Add(key.Location, -slice, start, start);
                    Add(key.Location, slice, start, start);
                    overlap -= slice;
                }
                if (overlap != 0) throw new InvalidOperationException("Same-asset cost exceeds concrete opening holdings.");
            }
            foreach (var change in changes.Where(x => x.Delta != 0))
                Add(change.Key.Location, change.Delta, Fingerprint(change.Before), Fingerprint(change.After));
            void Add(EconomyLocation location, long delta, string? prior, string? next)
            {
                var kind = delta < 0 ? EconomyLegKind.Sink : EconomyLegKind.Source;
                var reason = delta < 0
                    ? mutation.CommandKind switch
                    {
                        "world-revive" or "cd13e/tower-milestone" => EconomyReason.ItemUse,
                        "cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or
                            "cd13e/journey-victory" => EconomyReason.OwnedAwardCost,
                        "cd13e/daily-reset" => EconomyReason.DailyReset,
                        _ => EconomyReason.NpcShopPayment
                    }
                    : mutation.CommandKind switch
                    {
                        "gather-yield" => EconomyReason.GatherYield,
                        "monster-kill" => EconomyReason.MonsterDrop,
                        "dungeon-reward" or "tower-reward" or "cd13e/tower-milestone" or
                            "cd13e/journey-victory" => EconomyReason.ActivityReward,
                        "cd13e/quest-award" => EconomyReason.QuestReward,
                        "cd13e/achievement-award" => EconomyReason.AchievementReward,
                        "cd13e/advguild-award" => EconomyReason.AdventureGroupReward,
                        "cd13e/monsterhunt-award" => EconomyReason.MonsterHuntReward,
                        "cd13e/guild-award" => EconomyReason.GuildReward,
                        "cd13e/growthclass-award" => EconomyReason.GrowthClassReward,
                        "cd13e/library-award" => EconomyReason.LibraryReward,
                        "cd13e/firstheart-award" => EconomyReason.FirstHeartReward,
                        "cd13e/daily-stage" => EconomyReason.DailyReward,
                        "cd13e/gm-grant" => EconomyReason.GmGrant,
                        "cd13e/grant" => EconomyReason.OwnedGrant,
                        _ => EconomyReason.NpcShopGoods
                    };
                result.Add(new EconomyLeg(0, mutation.OperationId, result.Count, kind, reason, asset, location,
                    delta, BeforeFingerprint: prior, AfterFingerprint: next, SourceKey: mutation.SourceKey));
            }
            string? Fingerprint(EconomyHolding? holding) => asset.Kind == EconomyAssetKind.Item ? holding?.Fingerprint : null;
        }
        return result;
    }

    private List<EconomyLeg> BuildDungeonTransferLegs(
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> before,
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> after,
        IReadOnlyDictionary<long, int> itemBinds)
    {
        string pendingPrefix = "dungeon/" + mutation.SourceKey + "/entry/";
        var keys = before.Keys.Concat(after.Keys).Distinct().ToArray();
        var changes = keys.Select(key => (Key: key, Before: before.GetValueOrDefault(key),
            After: after.GetValueOrDefault(key), Delta: checked(
                (after.TryGetValue(key, out var a) ? a.Quantity : 0) -
                (before.TryGetValue(key, out var b) ? b.Quantity : 0)))).Where(x => x.Delta != 0).ToArray();
        var pending = changes.Where(x => x.Delta < 0 && x.Key.Location.Compartment == EconomyCompartment.Pending &&
            x.Key.Location.Key.StartsWith(pendingPrefix, StringComparison.Ordinal)).ToArray();
        var destinations = changes.Where(x => x.Delta > 0 && x.Key.Location.Compartment is
            EconomyCompartment.Inventory or EconomyCompartment.Currency).ToArray();
        if (pending.Length + destinations.Length != changes.Length ||
            pending.Any(x => x.After is not null || x.Before is null) ||
            destinations.Any(x => x.Key.Asset.Kind == EconomyAssetKind.Item &&
                x.Key.Location.Compartment != EconomyCompartment.Inventory ||
                x.Key.Asset.Kind == EconomyAssetKind.Currency &&
                x.Key.Location.Compartment != EconomyCompartment.Currency ||
                x.Before is not null && x.After is not null && x.Before.Fingerprint != x.After.Fingerprint))
            throw new InvalidOperationException("Dungeon delivery changed holdings outside its pending transfer.");
        var goods = Sum(mutation.Goods);
        var pendingTotals = pending.GroupBy(x => x.Key.Asset).ToDictionary(g => g.Key,
            g => g.Aggregate(0L, (sum, x) => checked(sum - x.Delta)));
        if (pendingTotals.Count != goods.Count || pendingTotals.Any(x => goods.GetValueOrDefault(x.Key) != x.Value))
            throw new InvalidOperationException("Dungeon pending holdings differ from frozen reward goods.");
        int Bind((EconomyAsset Asset, EconomyLocation Location) key)
        {
            if (key.Asset.Kind == EconomyAssetKind.Currency) return 0;
            if (key.Location.Compartment == EconomyCompartment.Pending)
            {
                string text = key.Location.Key[(key.Location.Key.LastIndexOf("/bind/", StringComparison.Ordinal) + 6)..];
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int bind) && bind >= 0
                    ? bind : throw new InvalidDataException("Invalid pending reward bind type.");
            }
            if (!long.TryParse(key.Location.Instance, NumberStyles.None, CultureInfo.InvariantCulture, out long guid) ||
                !itemBinds.TryGetValue(guid, out int actual))
                throw new InvalidDataException("Delivered item bind identity is missing.");
            return actual;
        }
        var sourceGroups = pending.GroupBy(x => (x.Key.Asset, Bind: Bind(x.Key))).ToDictionary(x => x.Key,
            x => x.OrderBy(h => h.Key.Location.Key, StringComparer.Ordinal).ToArray());
        var destinationGroups = destinations.GroupBy(x => (x.Key.Asset, Bind: Bind(x.Key))).ToDictionary(x => x.Key,
            x => x.OrderBy(h => h.Key.Location.Key, StringComparer.Ordinal).ToArray());
        if (sourceGroups.Count != destinationGroups.Count || sourceGroups.Keys.Except(destinationGroups.Keys).Any())
            throw new InvalidOperationException("Dungeon delivery changed the frozen binding allocation.");
        var amounts = keys.ToDictionary(x => x, x => before.GetValueOrDefault(x)?.Quantity ?? 0);
        var fingerprints = keys.ToDictionary(x => x, x => before.GetValueOrDefault(x)?.Fingerprint);
        var result = new List<EconomyLeg>();
        int edge = 0;
        foreach (var group in sourceGroups.OrderBy(x => (int)x.Key.Asset.Kind).ThenBy(x => x.Key.Asset.Id)
            .ThenBy(x => x.Key.Bind))
        {
            var sinks = group.Value;
            var credits = destinationGroups[group.Key];
            int creditIndex = 0;
            foreach (var sink in sinks)
            {
                long remaining = -sink.Delta;
                while (remaining > 0)
                {
                    if (creditIndex >= credits.Length)
                        throw new InvalidOperationException("Dungeon pending transfer lacks destination quantity.");
                    var credit = credits[creditIndex];
                    long available = checked(credit.After!.Quantity - amounts[credit.Key]);
                    if (available == 0) { creditIndex++; continue; }
                    long slice = Math.Min(remaining, available);
                    string? sourceBefore = sink.Key.Asset.Kind == EconomyAssetKind.Item ? fingerprints[sink.Key] : null;
                    string? destinationBefore = credit.Key.Asset.Kind == EconomyAssetKind.Item ? fingerprints[credit.Key] : null;
                    long sourceAfterQuantity = checked(amounts[sink.Key] - slice);
                    string? sourceAfter = sourceAfterQuantity > 0 ? sourceBefore : null;
                    string? destinationAfter = credit.Key.Asset.Kind == EconomyAssetKind.Item ? credit.After.Fingerprint : null;
                    if (destinationBefore is not null && destinationBefore != destinationAfter)
                        throw new InvalidOperationException("Dungeon delivery altered existing item metadata.");
                    edge++;
                    result.Add(new(0, mutation.OperationId, result.Count, EconomyLegKind.Transfer,
                        EconomyReason.DungeonRewardDelivery, sink.Key.Asset, sink.Key.Location, -slice, edge,
                        credit.Key.Location, sourceBefore, sourceAfter, mutation.SourceKey));
                    result.Add(new(0, mutation.OperationId, result.Count, EconomyLegKind.Transfer,
                        EconomyReason.DungeonRewardDelivery, credit.Key.Asset, credit.Key.Location, slice, edge,
                        sink.Key.Location, destinationBefore, destinationAfter, mutation.SourceKey));
                    amounts[sink.Key] = sourceAfterQuantity;
                    amounts[credit.Key] = checked(amounts[credit.Key] + slice);
                    fingerprints[sink.Key] = sourceAfter;
                    fingerprints[credit.Key] = destinationAfter;
                    remaining -= slice;
                }
            }
            if (credits.Any(x => amounts[x.Key] != x.After!.Quantity))
                throw new InvalidOperationException("Dungeon delivery has extra destination quantity.");
        }
        if (keys.Any(key => amounts[key] != (after.GetValueOrDefault(key)?.Quantity ?? 0)))
            throw new InvalidOperationException("Dungeon transfer legs do not explain the committed holdings.");
        return result;
    }

    private static Dictionary<EconomyAsset, long> Sum(IReadOnlyList<EconomyQuantity> quantities)
    {
        var result = new Dictionary<EconomyAsset, long>();
        foreach (var q in quantities) result[q.Asset] = checked(result.GetValueOrDefault(q.Asset) + q.Quantity);
        return result;
    }

    internal static byte[] EncodeChildReceipts(object?[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(values.Length);
        foreach (object? value in values)
            switch (value)
            {
                case null: writer.Write((byte)0); break;
                case int number: writer.Write((byte)1); writer.Write(number); break;
                case long number: writer.Write((byte)2); writer.Write(number); break;
                case long[] numbers:
                    writer.Write((byte)3); writer.Write(numbers.Length);
                    foreach (long number in numbers) writer.Write(number);
                    break;
                case bool flag: writer.Write((byte)4); writer.Write(flag); break;
                case AuctionItemWriteReceipt lot when lot.Id > 0 && lot.State.Length is > 0 and <= 512 * 1024:
                    writer.Write((byte)5); writer.Write(lot.Id); writer.Write(lot.State.Length);
                    writer.Write(lot.State); break;
                // 13f used tags 5/6 on its branch; 5 was already the 13d auction lot. Tags are persisted.
                case byte[] bytes: writer.Write((byte)7); writer.Write(bytes.Length); writer.Write(bytes); break;
                case string text: writer.Write((byte)8); writer.Write(text); break;
                default: throw new InvalidOperationException("Economy child receipt type is not durable: " + value.GetType().Name);
            }
        return stream.ToArray();
    }

    internal static object?[] DecodeChildReceipts(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        int count = reader.ReadInt32();
        if (count < 0 || count > 100000) throw new InvalidDataException("Invalid child receipt count.");
        var result = new object?[count];
        for (int i = 0; i < count; i++)
            result[i] = reader.ReadByte() switch
            {
                0 => null,
                1 => reader.ReadInt32(),
                2 => reader.ReadInt64(),
                3 => ReadLongs(reader),
                4 => reader.ReadBoolean(),
                5 => ReadAuctionLot(reader),
                7 => ReadBytes(reader),
                8 => reader.ReadString(),
                _ => throw new InvalidDataException("Unknown child receipt type.")
            };
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing child receipt bytes.");
        return result;
    }

    internal static void ValidateChildReceipts(object?[] values, int expectedIds, string commandKind = "npc-shop-buy")
    {
        if (commandKind == "cd13e/daily-reset")
        {
            if (expectedIds != -1 || values is not [true] and not [true, 1])
                throw new InvalidDataException("Daily reset typed child result is invalid.");
            return;
        }
        bool ownedAward = commandKind is "cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or
            "cd13e/journey-victory" or "cd13e/grant" or "cd13e/gm-grant";
        bool hasTaskProgress = commandKind == "monster-kill" && values.Length > 2 && values[^1] is long[];
        if (hasTaskProgress && values[^1] is long[] credited &&
            (credited.Length == 0 || credited.Any(id => id <= 0) ||
             !credited.SequenceEqual(credited.Distinct().Order())))
            throw new InvalidDataException("Kill task participant receipt is invalid.");
        var sourceValues = hasTaskProgress ? values[..^1] : values;
        if (expectedIds < 0 || sourceValues.Length < 2 || sourceValues[0] is not long[] ids ||
            ids.Length != expectedIds || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length ||
            (ownedAward ? sourceValues[1] is not true : sourceValues[1] is not 1))
            throw new InvalidDataException("Economic child item/progress result is invalid.");
        if (commandKind == "npc-shop-buy")
        {
            if (sourceValues.Length != 3 || sourceValues[2] is not int shopRows || shopRows != 1)
                throw new InvalidDataException("Purchase child result shape is invalid.");
            return;
        }
        if (!EconomyMutation.IsRewardCommand(commandKind) && !EconomyMutation.IsOwnedExchangeCommand(commandKind) ||
            sourceValues.Skip(2).Any(x => x is not int number || number is < 0 or > 1))
            throw new InvalidDataException("Reward child result shape is invalid.");
        if (EconomyMutation.IsOwnedExchangeCommand(commandKind))
        {
            if (sourceValues.Skip(2).Any(x => x is not 1))
                throw new InvalidDataException("Owned exchange source/blob child was not committed.");
            return;
        }
        int expected = commandKind switch
        {
            "gather-yield" => sourceValues.Length is >= 5 and <= 7 ? sourceValues.Length : -1,
            "dungeon-reward" => sourceValues.Length is >= 4 and <= 7 ? sourceValues.Length : -1,
            "tower-reward" => 4,
            "monster-kill" => sourceValues.Length is >= 2 and <= 8 ? sourceValues.Length : -1,
            _ => 2
        };
        if (sourceValues.Length != expected) throw new InvalidDataException("Reward child result count is invalid.");
        if (commandKind == "dungeon-reward" && sourceValues[^1] is not 1 ||
            commandKind == "gather-yield" && sourceValues.Skip(2).Any(x => x is not 1) ||
            commandKind == "monster-kill" && sourceValues.Skip(2).Where((_, index) => index != 1)
                .Any(x => x is not 1) ||
            commandKind == "dungeon-reward" && sourceValues[2] is not 1 ||
            commandKind == "dungeon-reward" && sourceValues.Skip(4).Any(x => x is not 1) ||
            commandKind == "tower-reward" && sourceValues[2] is not 1)
            throw new InvalidDataException("Reward source/blob child was not committed.");
    }

    private static long[] ReadLongs(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException("Invalid generated ID count.");
        return Enumerable.Range(0, count).Select(_ => reader.ReadInt64()).ToArray();
    }

    private static AuctionItemWriteReceipt ReadAuctionLot(BinaryReader reader)
    {
        long id = reader.ReadInt64();
        int length = reader.ReadInt32();
        if (id <= 0 || length is <= 0 or > 512 * 1024)
            throw new InvalidDataException("Invalid auction lot child receipt.");
        byte[] state = reader.ReadBytes(length);
        if (state.Length != length) throw new EndOfStreamException("Truncated auction lot child receipt.");
        return new AuctionItemWriteReceipt(id, state);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 16777216) throw new InvalidDataException("Invalid child result bytes.");
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }
}

internal readonly record struct EconomyRoleProgress(int BaseLevel, long BaseExp, int JobLevel, long JobExp);

internal static class EconomyRoleProgressRead
{
    internal static EconomyRoleProgress Read(SqliteConnection db, SqliteTransaction tx, long role)
    {
        using var command = DbSql.Command(db, tx, "SELECT level,exp,job_level,job_exp FROM role WHERE id=$role");
        command.Parameters.AddWithValue("$role", role);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("Reward role progress row is missing.");
        return new(row.GetInt32(0), row.GetInt64(1), row.GetInt32(2), row.GetInt64(3));
    }
}

internal static class EconomyRoleAssetRead
{
    internal static IReadOnlyDictionary<long, int> ReadItemBinds(SqliteConnection db, SqliteTransaction tx, long role)
    {
        var result = new Dictionary<long, int>();
        using var command = DbSql.Command(db, tx, "SELECT guid,bind_type FROM item WHERE role_id=$role");
        command.Parameters.AddWithValue("$role", role);
        using var row = command.ExecuteReader();
        while (row.Read()) result.Add(row.GetInt64(0), row.GetInt32(1));
        return result;
    }
    internal static Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> Read(
        SqliteConnection db, SqliteTransaction tx, long role)
    {
        var result = new Dictionary<(EconomyAsset, EconomyLocation), EconomyHolding>();
        string ownerId = role.ToString(CultureInfo.InvariantCulture);
        var owner = new EconomyOwner(EconomyOwnerKind.Role, ownerId);
        using (var progress = DbSql.Command(db, tx, "SELECT currency FROM role WHERE id=$role"))
        {
            progress.Parameters.AddWithValue("$role", role);
            string text = progress.ExecuteScalar() as string ?? throw new InvalidDataException("Role currency is missing.");
            var seen = new HashSet<long>();
            if (text.Length != 0) foreach (string pair in text.Split(','))
            {
                string[] fields = pair.Split(':');
                if (fields.Length != 2 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long quantity) ||
                    id <= 0 || quantity < 0 || !seen.Add(id)) throw new InvalidDataException("Invalid role currency encoding.");
                if (quantity > 0) Add(new(EconomyAssetKind.Currency, id),
                    new(owner, EconomyCompartment.Currency, $"role/{ownerId}/currency/{id}"), quantity, "");
            }
        }
        using (var items = DbSql.Command(db, tx, """
            SELECT guid,entry,stack,pack,sort,grid,attrs,enchantments,str_attrs,bind_type,bind_expire,is_locked
            FROM item WHERE role_id=$role ORDER BY guid
            """))
        {
            items.Parameters.AddWithValue("$role", role);
            using var row = items.ExecuteReader();
            while (row.Read())
            {
                long guid = row.GetInt64(0), entry = row.GetInt64(1), stack = row.GetInt64(2);
                long pack = row.GetInt64(3), sort = row.GetInt64(4), grid = row.GetInt64(5);
                long bind = row.GetInt64(9), expiry = row.GetInt64(10), locked = row.GetInt64(11);
                if (guid <= 0 || entry <= 0 || stack <= 0 || pack < 0 || sort < 0 || bind < 0 || expiry < 0 || locked is not (0 or 1))
                    throw new InvalidDataException("Invalid item row in purchase scope.");
                string Extra(int column)
                {
                    if (row.IsDBNull(column)) return "{}";
                    using var json = JsonDocument.Parse(row.GetString(column));
                    if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid item metadata.");
                    return Canon(json.RootElement);
                }
                string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"Attrs={Extra(6)}|StringAttrs={Extra(8)}|Enchantments={Extra(7)}|BindType={bind}|BindExpire={expiry}|IsLocked={locked}")));
                Add(new(EconomyAssetKind.Item, entry), new(owner, EconomyCompartment.Inventory,
                    $"item/{guid}/pack-{pack}/grid-{grid}/sort-{sort}", guid.ToString(CultureInfo.InvariantCulture)), stack, fingerprint);
            }
        }
        foreach (var holding in EconomyDungeonPending.Read(db, tx, role))
            Add(holding.Asset, holding.Location, holding.Quantity, holding.Fingerprint);
        return result;

        void Add(EconomyAsset asset, EconomyLocation location, long quantity, string fingerprint)
        {
            var holding = new EconomyHolding(asset, location, quantity, fingerprint);
            if (!result.TryAdd((asset, location), holding)) throw new InvalidDataException("Duplicate purchase holding.");
        }
    }

    private static string Canon(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canon(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canon)) + "]",
        _ => e.GetRawText()
    };
}
