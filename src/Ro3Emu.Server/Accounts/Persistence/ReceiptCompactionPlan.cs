using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// One batch of <see cref="ReceiptRetention"/>: the receipts after commit <paramref name="afterSeq"/> recorded before
/// <paramref name="cutoffUtcMs"/>, at most <paramref name="batch"/> of them, lose their intent payload (replaced by
/// <see cref="ReceiptBlobs.CompactedPayload"/>) and their outcome bytes.
/// - **Kept:** the row, its sequence, hashes (intent_hash covers the outcome), child receipts (a replay returns them),
///   revisions, leg count, ledger legs and source claim, so idempotency, replay and the ledger's continuity are unchanged
///   (<see cref="ReceiptBlobs"/>).
/// - **Not compacted:** <c>mail/</c> deliveries. <see cref="MailDeliveryReceiptReadPlan"/> re-reads a delivery's payload
///   whenever the same delivery key comes back, however late.
/// Returns the receipts compacted and the last commit sequence scanned (0 when nothing was due).
/// </summary>
internal sealed class ReceiptCompactionPlan(long cutoffUtcMs, long afterSeq, int batch) : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "economy/receipt-compaction";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var due = new List<string>(batch);
        long last = 0;
        using (var scan = DbSql.Command(db, tx, """
            SELECT operation_id,commit_seq FROM operation_receipt
            WHERE commit_seq>$after AND recorded_utc_ms<$cutoff AND command_kind NOT LIKE 'mail/%'
            ORDER BY commit_seq LIMIT $batch
            """))
        {
            scan.Parameters.AddWithValue("$after", afterSeq);
            scan.Parameters.AddWithValue("$cutoff", cutoffUtcMs);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                due.Add(row.GetString(0));
                last = row.GetInt64(1);
            }
        }
        int compacted = 0;
        using var update = DbSql.Command(db, tx, """
            UPDATE operation_receipt SET intent_payload=$marker,outcome=X''
            WHERE operation_id=$op AND intent_payload<>$marker
            """);
        update.Parameters.AddWithValue("$marker", ReceiptBlobs.CompactedPayload);
        var op = update.Parameters.Add("$op", SqliteType.Text);
        foreach (string id in due)
        {
            op.Value = id;
            compacted += update.ExecuteNonQuery();
        }
        return (compacted, last);
    }
}
