using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// One batch of <see cref="ReceiptRetention"/>: walks the receipts after the stored watermark
/// (<c>economy_meta.compacted_seq</c>) in commit order, at most <paramref name="batch"/> of them, and stops at the first
/// one recorded at or after <paramref name="cutoffUtcMs"/>. The due ones lose their intent payload (replaced by
/// <see cref="ReceiptBlobs.CompactedPayload"/>) and their outcome bytes, and the watermark moves past every receipt the
/// batch walked, so the next run starts where this one stopped instead of rescanning the realm's whole history.
/// - **Kept:** the row, its sequence, hashes (intent_hash covers the outcome), child receipts (a replay returns them),
///   revisions, leg count, ledger legs and source claim, so idempotency, replay and the ledger's continuity are unchanged
///   (<see cref="ReceiptBlobs"/>).
/// - **Not compacted:** <c>mail/</c> deliveries. <see cref="MailDeliveryReceiptReadPlan"/> re-reads a delivery's payload
///   whenever the same delivery key comes back, however late. The watermark still moves past them.
/// Receipts carry the wall clock of their commit, so commit order is time order only while that clock runs forward. A
/// receipt that is merely not due yet ends the walk, since what follows it is about as young. One dated at or after
/// <paramref name="futureUtcMs"/> was written under a wrong clock: the watermark moves past it without compacting it,
/// as its real age is unknown and one receipt must not stop compaction until its date arrives. Later due receipts
/// behind a date that is not that far off wait for it, at most a day past their own due time.
/// </summary>
internal sealed class ReceiptCompactionPlan(long cutoffUtcMs, long futureUtcMs, int batch) : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "economy/receipt-compaction";

    /// <summary>A batch's result: receipts compacted, receipts the watermark moved past, how many of those carried an
    /// impossible future date, whether more may be due.</summary>
    internal readonly record struct Batch(int Compacted, int Passed, int Future, bool More);

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long after;
        using (var meta = DbSql.Command(db, tx, "SELECT compacted_seq FROM economy_meta WHERE singleton=1"))
            after = meta.ExecuteScalar() is long stored ? stored : 0;
        var due = new List<string>(batch);
        long watermark = after;
        int read = 0, passed = 0, future = 0;
        bool reachedRecent = false;
        using (var scan = DbSql.Command(db, tx, """
            SELECT operation_id,commit_seq,recorded_utc_ms,command_kind FROM operation_receipt
            WHERE commit_seq>$after ORDER BY commit_seq LIMIT $batch
            """))
        {
            scan.Parameters.AddWithValue("$after", after);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                read++;
                long recorded = row.GetInt64(2);
                if (recorded >= futureUtcMs) future++;
                else if (recorded >= cutoffUtcMs) { reachedRecent = true; break; }
                else if (!row.GetString(3).StartsWith("mail/", StringComparison.Ordinal)) due.Add(row.GetString(0));
                watermark = row.GetInt64(1);
                passed++;
            }
        }
        int compacted = 0;
        using (var update = DbSql.Command(db, tx, """
            UPDATE operation_receipt SET intent_payload=$marker,outcome=X''
            WHERE operation_id=$op AND intent_payload<>$marker
            """))
        {
            update.Parameters.AddWithValue("$marker", ReceiptBlobs.CompactedPayload);
            var op = update.Parameters.Add("$op", SqliteType.Text);
            foreach (string id in due)
            {
                op.Value = id;
                compacted += update.ExecuteNonQuery();
            }
        }
        if (watermark > after)
        {
            using var advance = DbSql.Command(db, tx,
                "UPDATE economy_meta SET compacted_seq=$seq WHERE singleton=1 AND compacted_seq<$seq");
            advance.Parameters.AddWithValue("$seq", watermark);
            advance.ExecuteNonQuery();
        }
        return new Batch(compacted, passed, future, !reachedRecent && read == batch);
    }
}
