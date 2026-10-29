using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Resolve a lost E05d commit acknowledgement from the durable receipt and natural source.</summary>
internal sealed class EconomyEnvelopeResolveReadPlan(EconomyEnvelope envelope, int childCount) : DbReadPlan
{
    internal override int Bytes => 512 + envelope.OperationId.Length * 2;
    internal override string Label => "economy/resolve-e05d";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = DbSql.Command(db, tx, """
            SELECT realm_id,actor_account,actor_role,command_kind,request_fingerprint,source_key,intent_hash,
                intent_payload,outcome,child_receipts,leg_count FROM operation_receipt WHERE operation_id=$op
            """);
        command.Parameters.AddWithValue("$op", envelope.OperationId);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            row.Close();
            using var source = DbSql.Command(db, tx, "SELECT operation_id FROM operation_source WHERE source_key=$key");
            source.Parameters.AddWithValue("$key", envelope.SourceKey);
            if (source.ExecuteScalar() is string used)
                throw new EconomyOperationConflictException("Natural source already belongs to operation " + used);
            return null;
        }
        if (row.GetString(1) != envelope.ActorAccount || row.GetInt64(2) != envelope.ActorRoleId ||
            row.GetString(3) != envelope.CommandKind || row.GetString(4) != envelope.RequestFingerprint ||
            row.GetString(5) != envelope.SourceKey ||
            !ReceiptBlobs.SameIntent(row.GetString(6), (byte[])row.GetValue(7), envelope.IntentHash,
                envelope.IntentPayload, envelope.LegacyIntent) ||
            !ReceiptBlobs.SameOutcome((byte[])row.GetValue(8), (byte[])row.GetValue(7), envelope.Outcome))
            throw new EconomyOperationConflictException("Committed E05d operation belongs to a different actor or intent.");
        var children = EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(9));
        if (children.Length != childCount || row.GetInt32(10) < 0)
            throw new InvalidDataException("Committed E05d child receipt count is invalid.");
        string realm = row.GetString(0);
        row.Close();
        using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
        if (meta.ExecuteScalar() is not string actual || actual != realm)
            throw new InvalidDataException("Committed E05d receipt has a foreign realm identity.");
        using var claim = DbSql.Command(db, tx,
            "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$key AND operation_id=$op");
        claim.Parameters.AddWithValue("$realm", realm);
        claim.Parameters.AddWithValue("$key", envelope.SourceKey);
        claim.Parameters.AddWithValue("$op", envelope.OperationId);
        if (claim.ExecuteScalar() is null) throw new InvalidDataException("Committed E05d receipt lost its source claim.");
        return children;
    }
}
