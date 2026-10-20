using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Economy;

/// <summary>Issues the frozen pending entitlement exactly when it becomes active.</summary>
internal static class DungeonObligationLedger
{
    internal static void VerifyEarned(SqliteConnection db, SqliteTransaction tx, DungeonRewardObligation row)
    {
        string source = EconomyDungeonPending.EarnedSource(row.Key);
        string operation = "earned-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        using var receipt = DbSql.Command(db, tx, """
            SELECT 1 FROM operation_receipt r JOIN operation_source s ON s.operation_id=r.operation_id
            WHERE r.operation_id=$op AND r.source_key=$source AND s.source_key=$source
            """);
        receipt.Parameters.AddWithValue("$op", operation);
        receipt.Parameters.AddWithValue("$source", source);
        if (receipt.ExecuteScalar() is null)
            throw new InvalidDataException("Activated dungeon obligation has no earned receipt/source claim.");
    }

    internal static void Earn(SqliteConnection db, SqliteTransaction tx, DungeonRewardObligation row, byte[] state)
    {
        string earnedSource = EconomyDungeonPending.EarnedSource(row.Key);
        string operation = "earned-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(earnedSource)));
        using var actor = DbSql.Command(db, tx, """
            SELECT r.account,l.epoch,l.deleted FROM role r JOIN role_lifecycle l ON l.role_id=r.id
            WHERE r.id=$role
            """);
        actor.Parameters.AddWithValue("$role", row.RoleId);
        using var identity = actor.ExecuteReader();
        if (!identity.Read() || identity.GetInt64(2) != 0)
            throw new OwnerRevisionConflictException();
        string account = identity.GetString(0);
        long epoch = identity.GetInt64(1);
        identity.Close();
        var holdings = EconomyDungeonPending.Holdings(row);
        if (holdings.Count > 32) throw new InvalidOperationException("Frozen dungeon goods exceed economy receipt limit.");
        var goods = holdings.Select(h => new EconomyQuantity(h.Asset, h.Quantity)).ToArray();
        long baseExp = row.Goods.Where(g => g.Entry == 100).Sum(g => (long)g.Count);
        long jobExp = row.Goods.Where(g => g.Entry == 101).Sum(g => (long)g.Count);
        byte[] stateHash = SHA256.HashData(state);
        string fingerprint = Convert.ToHexString(stateHash);
        long effective = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] intent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            OperationId = operation, ActorAccount = account, ActorRoleId = row.RoleId,
            CommandKind = "dungeon-obligation-earn", RequestFingerprint = fingerprint,
            SourceKey = earnedSource, EffectiveUtcMs = effective,
            Costs = Array.Empty<EconomyQuantity>(), Goods = goods,
            Outcome = Convert.ToBase64String(stateHash), FrozenStateHash = fingerprint,
            PendingBaseExp = baseExp, PendingJobExp = jobExp
        });
        if (intent.Length > 65536) throw new InvalidOperationException("Frozen dungeon intent exceeds receipt limit.");
        string revisions = JsonSerializer.Serialize(new
        {
            roleId = row.RoleId, epoch,
            deleted = false, parts = new Dictionary<string, long>()
        });
        var legs = holdings.Select((h, index) => new EconomyLeg(0, operation, index,
            EconomyLegKind.Source, EconomyReason.ActivityReward, h.Asset, h.Location,
            h.Quantity, AfterFingerprint: h.Asset.Kind == EconomyAssetKind.Item ? h.Fingerprint : null,
            SourceKey: earnedSource)).ToArray();
        EconomyLedgerWrite.Append(db, tx, new EconomyLedgerCommit(operation, account, row.RoleId,
            "dungeon-obligation-earn", fingerprint, Convert.ToHexString(SHA256.HashData(intent)), intent,
            earnedSource, effective, stateHash, EconomyTransactionPlan.EncodeChildReceipts([]),
            revisions, revisions, legs));
    }
}
