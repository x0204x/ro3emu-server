using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Resolve ambiguous writer acknowledgement on the normal read pool, using one pinned committed view.</summary>
internal sealed class EconomyReceiptResolveReadPlan(EconomyMutation mutation, int expectedItemIds,
    int expectedFamilyChildren = 0) : DbReadPlan
{
    internal override int Bytes => 512 + mutation.OperationId.Length * 2;
    internal override string Label => "economy/resolve-receipt";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var receipt = DbSql.Command(db, tx, """
            SELECT realm_id,actor_account,actor_role,command_kind,request_fingerprint,intent_hash,source_key,
                child_receipts,leg_count,intent_payload,outcome FROM operation_receipt WHERE operation_id=$op
            """);
        receipt.Parameters.AddWithValue("$op", mutation.OperationId);
        using var row = receipt.ExecuteReader();
        if (!row.Read())
        {
            row.Close();
            using var conflicting = DbSql.Command(db, tx,
                "SELECT operation_id FROM operation_source WHERE source_key=$source");
            conflicting.Parameters.AddWithValue("$source", mutation.SourceKey);
            if (conflicting.ExecuteScalar() is string other)
                throw new EconomyOperationConflictException("Natural source belongs to committed operation " + other);
            return null;
        }
        if (row.GetString(1) != mutation.ActorAccount || row.GetInt64(2) != mutation.ActorRoleId ||
            row.GetString(3) != mutation.CommandKind || row.GetString(4) != mutation.RequestFingerprint ||
            row.GetString(6) != mutation.SourceKey ||
            !ReceiptBlobs.SameIntent(row.GetString(5), (byte[])row.GetValue(9), mutation.IntentHash,
                mutation.IntentPayload, mutation.LegacyIntent) ||
            !ReceiptBlobs.SameOutcome((byte[])row.GetValue(10), (byte[])row.GetValue(9), mutation.Outcome))
            throw new EconomyOperationConflictException("Committed operation ID belongs to different actor or intent.");
        string realm = row.GetString(0);
        object?[] results = EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(7));
        int legCount = row.GetInt32(8);
        if (legCount < (mutation.IsFamily || mutation.IsProgression || mutation.RewardEvidence is not null ||
            mutation.CommandKind == "cd13e/daily-reset" ? 0 : 1))
            throw new InvalidDataException("Committed receipt child/leg count is corrupt.");
        if (mutation.IsFamily || mutation.IsProgression)
        {
            if (expectedFamilyChildren is < 1 or > 32 || results.Length != expectedFamilyChildren ||
                results[0] is not long[] ids || ids.Length != expectedItemIds ||
                ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length ||
                results.Skip(1).Any(value => value is not int count || count != 1))
                throw new InvalidDataException("Family typed child result shape is corrupt.");
        }
        else EconomyTransactionPlan.ValidateChildReceipts(results, expectedItemIds, mutation.CommandKind);
        row.Close();
        using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
        if (meta.ExecuteScalar() is not string actualRealm || actualRealm != realm)
            throw new InvalidDataException("Committed receipt realm differs from database identity.");
        using var source = DbSql.Command(db, tx,
            "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$key AND operation_id=$op");
        source.Parameters.AddWithValue("$realm", realm);
        source.Parameters.AddWithValue("$key", mutation.SourceKey);
        source.Parameters.AddWithValue("$op", mutation.OperationId);
        if (source.ExecuteScalar() is null) throw new InvalidDataException("Committed receipt lost its source claim.");
        if (mutation.IsFamily || mutation.IsProgression)
            FamilyEconomyTransactionPlan.ValidateLegCount(db, tx, mutation.OperationId, legCount);
        return results;
    }
}
