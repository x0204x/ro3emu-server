using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Indexed copies of the auction_item state fields the reads filter on (#254): the sweep and the market read only
/// unsettled lots, history only settled ones of a type, a dungeon run only its own lots. The state blob stays the
/// source of truth; every write of it rewrites these columns in the same transaction.
/// </summary>
internal static class AuctionItemColumns
{
    internal const string Select = "SELECT a.state, r.revision FROM auction_item a LEFT JOIN owner_revision r"
        + " ON r.kind='auction_item' AND r.id=a.source_key AND r.part='state'";

    internal static void Migrate(SqliteConnection db, SqliteTransaction tx)
    {
        bool added = SchemaSql.AddColumn(db, tx, "auction_item", "settled", "INTEGER NOT NULL DEFAULT 0");
        added |= SchemaSql.AddColumn(db, tx, "auction_item", "auction_type", "INTEGER NOT NULL DEFAULT 0");
        added |= SchemaSql.AddColumn(db, tx, "auction_item", "run_id", "INTEGER NOT NULL DEFAULT 0");
        added |= SchemaSql.AddColumn(db, tx, "auction_item", "end_time", "INTEGER NOT NULL DEFAULT 0");
        if (added) Backfill(db, tx);
        SchemaSql.Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS auction_item_open ON auction_item(settled, auction_type);
            CREATE INDEX IF NOT EXISTS auction_item_run ON auction_item(run_id);
            """);
    }

    // Rows written before the columns existed: decode each once, as the reads did on every call.
    private static void Backfill(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = new List<(long Id, AuctionStore.Item Item)>();
        using (var read = DbSql.Command(db, tx, "SELECT id, state FROM auction_item"))
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                if (reader[1] is byte[] { Length: > 0 } state)
                    rows.Add((reader.GetInt64(0), JsonSerializer.Deserialize<AuctionStore.Item>(state)
                        ?? throw new InvalidDataException("Auction lot state must contain an item.")));
        foreach (var (id, item) in rows) Write(db, tx, id, item);
    }

    internal static void Write(SqliteConnection db, SqliteTransaction tx, long id, AuctionStore.Item item)
    {
        using var cmd = DbSql.Command(db, tx,
            "UPDATE auction_item SET settled=$settled, auction_type=$type, run_id=$run, end_time=$end WHERE id=$id");
        cmd.Parameters.AddWithValue("$settled", item.State == AuctionState.Settled ? 1 : 0);
        cmd.Parameters.AddWithValue("$type", (int)item.Type);
        cmd.Parameters.AddWithValue("$run", item.RunId);
        cmd.Parameters.AddWithValue("$end", item.End);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
