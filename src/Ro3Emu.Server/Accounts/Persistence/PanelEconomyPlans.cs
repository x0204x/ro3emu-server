using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One economy_entry leg as the panel's economy log shows it, with its receipt's actor and command.</summary>
internal sealed record PanelEconomyRow(long Id, long RecordedMs, string Account, int OwnerKind, string OwnerId, string OwnerName,
    string Command, int Reason, int LegKind, int AssetKind, long AssetId, long Delta, int Compartment,
    int? CounterpartyKind, string? CounterpartyId, string CounterpartyName, string Operation);

/// <param name="Account">The receipt's actor account, or the account of the character whose holdings moved (any letter
/// case); empty = any.</param>
/// <param name="Character">The character whose holdings moved, by name (any letter case); empty = any.</param>
/// <param name="Assets">Item or currency ids; empty = any.</param>
internal sealed record PanelEconomyFilter(string Account, string Character, IReadOnlyList<long> Assets, long? FromMs, long? ToMs);

/// <summary>
/// A page of the economy ledger (economy_entry joined to its operation_receipt), newest first, with the number of legs
/// the filter matches and the sums of their positive and negative deltas. Read-only, on the read pool; a database
/// without the ledger tables reads as empty.
/// </summary>
internal sealed class PanelEconomyRead(PanelEconomyFilter filter, int offset, int limit) : DbReadPlan
{
    internal override int Bytes => 512 + Encoding.UTF8.GetByteCount(filter.Account + filter.Character) + filter.Assets.Count * 16 + limit * 384;
    internal override string Label => "panel/economy";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var exists = DbSql.Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('economy_entry','operation_receipt')"))
            if ((long)exists.ExecuteScalar()! < 2) return (0, 0L, 0L, new List<PanelEconomyRow>());

        const string From = " FROM economy_entry e JOIN operation_receipt o ON o.operation_id=e.operation_id " +
            "LEFT JOIN role r ON e.owner_kind=1 AND r.id=CAST(e.owner_id AS INTEGER) " +
            "LEFT JOIN role c ON e.counterparty_owner_kind=1 AND c.id=CAST(e.counterparty_owner_id AS INTEGER) ";
        var where = new StringBuilder("WHERE 1=1");
        if (filter.Account.Length > 0) where.Append(" AND (o.actor_account=$account COLLATE NOCASE OR r.account=$account COLLATE NOCASE)");
        if (filter.Character.Length > 0) where.Append(" AND r.name=$character COLLATE NOCASE");
        if (filter.Assets.Count > 0)
            where.Append(" AND e.asset_id IN (").Append(string.Join(',', filter.Assets.Select((_, i) => "$a" + i.ToString(CultureInfo.InvariantCulture)))).Append(')');
        if (filter.FromMs != null) where.Append(" AND e.recorded_utc_ms >= $from");
        if (filter.ToMs != null) where.Append(" AND e.recorded_utc_ms < $to");

        long total, sumIn, sumOut;
        using (var totals = Bind(DbSql.Command(db, tx, "SELECT COUNT(*), TOTAL(CASE WHEN e.delta>0 THEN e.delta END), " +
            "TOTAL(CASE WHEN e.delta<0 THEN e.delta END)" + From + where)))
        using (var row = totals.ExecuteReader())
        {
            row.Read();
            total = row.GetInt64(0);
            sumIn = Clamp(row.GetDouble(1));
            sumOut = Clamp(row.GetDouble(2));
        }
        using var select = Bind(DbSql.Command(db, tx, "SELECT e.entry_id,e.recorded_utc_ms,o.actor_account,e.owner_kind,e.owner_id," +
            "COALESCE(r.name,''),o.command_kind,e.reason_code,e.leg_kind,e.asset_kind,e.asset_id,e.delta,e.compartment_kind," +
            "e.counterparty_owner_kind,e.counterparty_owner_id,COALESCE(c.name,''),e.operation_id" + From + where +
            " ORDER BY e.entry_id DESC LIMIT $limit OFFSET $offset"));
        select.Parameters.AddWithValue("$limit", limit);
        select.Parameters.AddWithValue("$offset", offset);
        using var rows = select.ExecuteReader();
        var entries = new List<PanelEconomyRow>();
        while (rows.Read())
            entries.Add(new PanelEconomyRow(rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2), rows.GetInt32(3), rows.GetString(4),
                rows.GetString(5), rows.GetString(6), rows.GetInt32(7), rows.GetInt32(8), rows.GetInt32(9), rows.GetInt64(10), rows.GetInt64(11),
                rows.GetInt32(12), rows.IsDBNull(13) ? null : rows.GetInt32(13), rows.IsDBNull(14) ? null : rows.GetString(14), rows.GetString(15),
                rows.GetString(16)));
        return ((int)Math.Min(total, int.MaxValue), sumIn, sumOut, entries);
    }

    private SqliteCommand Bind(SqliteCommand command)
    {
        if (filter.Account.Length > 0) command.Parameters.AddWithValue("$account", filter.Account);
        if (filter.Character.Length > 0) command.Parameters.AddWithValue("$character", filter.Character);
        for (int i = 0; i < filter.Assets.Count; i++) command.Parameters.AddWithValue("$a" + i.ToString(CultureInfo.InvariantCulture), filter.Assets[i]);
        if (filter.FromMs is { } from) command.Parameters.AddWithValue("$from", from);
        if (filter.ToMs is { } to) command.Parameters.AddWithValue("$to", to);
        return command;
    }

    /// <summary>TOTAL sums in floating point (SUM would fail on an overflow); the page shows whole numbers.</summary>
    private static long Clamp(double sum) => sum >= long.MaxValue ? long.MaxValue : sum <= long.MinValue ? long.MinValue : (long)Math.Round(sum);
}
