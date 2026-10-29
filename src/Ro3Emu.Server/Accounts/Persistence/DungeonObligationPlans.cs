using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class DungeonObligationSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "dungeon-obligation/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS dungeon_reward_obligation (
                source_key TEXT PRIMARY KEY, role_id INTEGER NOT NULL, room_id INTEGER NOT NULL,
                activated INTEGER NOT NULL CHECK(activated IN (0,1)),
                completed INTEGER NOT NULL CHECK(completed IN (0,1)), state BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS dungeon_obligation_pending_role
                ON dungeon_reward_obligation(role_id) WHERE completed=0 AND activated=1;
            CREATE INDEX IF NOT EXISTS dungeon_obligation_pending_room
                ON dungeon_reward_obligation(room_id) WHERE completed=0 AND activated=1;
            CREATE TABLE IF NOT EXISTS dungeon_task_clear_outbox (
                source_key TEXT PRIMARY KEY, role_id INTEGER NOT NULL, room_id INTEGER NOT NULL,
                applied INTEGER NOT NULL CHECK(applied IN (0,1)), state BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS dungeon_task_clear_pending_role
                ON dungeon_task_clear_outbox(role_id) WHERE applied=0;
            """);
        cmd.ExecuteNonQuery();
        return 1;
    }
}

internal sealed class DungeonObligationFreezePlan(IReadOnlyList<DungeonRewardObligation> rows) : DbPlan
{
    private readonly (DungeonRewardObligation Row, byte[] State)[] frozen = rows
        .Select(row => (row, JsonSerializer.SerializeToUtf8Bytes(row))).ToArray();
    internal override int Bytes => 256 + frozen.Sum(row => row.State.Length);
    internal override string Label => "dungeon-obligation/freeze";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (row, state) in frozen)
        {
            using var insert = DbSql.Command(db, tx,
                "INSERT OR IGNORE INTO dungeon_reward_obligation(source_key,role_id,room_id,activated,completed,state) VALUES($key,$role,$room,$active,0,$state)");
            insert.Parameters.AddWithValue("$key", row.Key);
            insert.Parameters.AddWithValue("$role", row.RoleId);
            insert.Parameters.AddWithValue("$room", row.RoomId);
            insert.Parameters.AddWithValue("$active", row.RequiresKill ? 0 : 1);
            insert.Parameters.AddWithValue("$state", state);
            if (insert.ExecuteNonQuery() != 0)
            {
                if (!row.RequiresKill) DungeonObligationLedger.Earn(db, tx, row, state);
                continue;
            }
            using var existing = DbSql.Command(db, tx,
                "SELECT role_id,activated,state FROM dungeon_reward_obligation WHERE source_key=$key");
            existing.Parameters.AddWithValue("$key", row.Key);
            using var reader = existing.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != row.RoleId ||
                (!row.RequiresKill && reader.GetInt64(1) != 1) ||
                !((byte[])reader[2]).AsSpan().SequenceEqual(state))
                throw new InvalidOperationException("Dungeon source was frozen with a different outcome.");
            bool active = reader.GetInt64(1) == 1;
            reader.Close();
            if (active) DungeonObligationLedger.VerifyEarned(db, tx, row);
        }
        return frozen.Length;
    }
}

internal sealed class DungeonObligationReadPlan(long role, long room = 0, bool includePrepared = false) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "dungeon-obligation/pending";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, room == 0
            ? "SELECT state FROM dungeon_reward_obligation WHERE role_id=$role AND completed=0 AND activated=1 ORDER BY rowid"
            : includePrepared
                ? "SELECT state FROM dungeon_reward_obligation WHERE room_id=$room AND completed=0 ORDER BY rowid"
                : "SELECT state FROM dungeon_reward_obligation WHERE room_id=$room AND completed=0 AND activated=1 ORDER BY rowid");
        if (room == 0) cmd.Parameters.AddWithValue("$role", role);
        else cmd.Parameters.AddWithValue("$room", room);
        using var reader = cmd.ExecuteReader();
        var rows = new List<DungeonRewardObligation>();
        while (reader.Read()) rows.Add(EconomyDungeonPending.Decode((byte[])reader[0]));
        return rows;
    }
}

internal sealed class DungeonObligationActivatedReadPlan(long roomId) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "dungeon-obligation/activated";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT 1 FROM dungeon_reward_obligation WHERE room_id=$room AND activated=1 LIMIT 1");
        cmd.Parameters.AddWithValue("$room", roomId);
        return cmd.ExecuteScalar() != null;
    }
}

/// <summary>The ordinary terminal kill and all recipient entitlements become earned in one transaction.</summary>
internal sealed class DungeonObligationActivatePlan(long roomId, string killSourceKey) : DbPlan
{
    internal long RoomId => roomId;
    internal string KillSourceKey => killSourceKey;
    internal override int Bytes => 256 + killSourceKey.Length * 2;
    internal override string Label => "dungeon-obligation/activate";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var read = DbSql.Command(db, tx,
            "SELECT source_key,state,activated,completed FROM dungeon_reward_obligation WHERE room_id=$room ORDER BY source_key");
        read.Parameters.AddWithValue("$room", roomId);
        using var reader = read.ExecuteReader();
        var rows = new List<(string Key, byte[] State, DungeonRewardObligation Obligation)>();
        while (reader.Read())
        {
            var state = (byte[])reader[1];
            var row = EconomyDungeonPending.Decode(state);
            if (reader.GetInt64(2) != 0 || reader.GetInt64(3) != 0 || !row.RequiresKill
                || row.RoomId != roomId || row.KillSourceKey != killSourceKey || row.Key != reader.GetString(0))
                throw new InvalidOperationException("Dungeon activation did not match its prepared kill source.");
            rows.Add((reader.GetString(0), state, row));
        }
        reader.Close();
        if (rows.Count == 0) throw new InvalidOperationException("Terminal kill has no prepared dungeon recipients.");
        foreach (var (key, state, row) in rows)
        {
            using var activate = DbSql.Command(db, tx,
                "UPDATE dungeon_reward_obligation SET activated=1 WHERE source_key=$key AND activated=0 AND completed=0");
            activate.Parameters.AddWithValue("$key", key);
            if (activate.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Prepared dungeon recipient changed before terminal kill.");
            DungeonObligationLedger.Earn(db, tx, row, state);
        }
        return 1;
    }
}

/// <summary>A failed uncommitted terminal hit can time out; no economic source was issued for these rows.</summary>
internal sealed class DungeonObligationCancelPreparedPlan(long roomId) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "dungeon-obligation/cancel-prepared";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var active = DbSql.Command(db, tx,
            "SELECT 1 FROM dungeon_reward_obligation WHERE room_id=$room AND activated=1 LIMIT 1");
        active.Parameters.AddWithValue("$room", roomId);
        if (active.ExecuteScalar() != null) return 0;
        // 尚未贏得的尾擊取消時，任務旁表也必須一併取消。
        using var clear = DbSql.Command(db, tx, """
            DELETE FROM dungeon_task_clear_outbox WHERE room_id=$room AND applied=0
              AND source_key IN (SELECT source_key FROM dungeon_reward_obligation
                WHERE room_id=$room AND activated=0 AND completed=0)
            """);
        clear.Parameters.AddWithValue("$room", roomId);
        clear.ExecuteNonQuery();
        using var cancel = DbSql.Command(db, tx,
            "DELETE FROM dungeon_reward_obligation WHERE room_id=$room AND activated=0 AND completed=0");
        cancel.Parameters.AddWithValue("$room", roomId);
        return cancel.ExecuteNonQuery();
    }
}

internal sealed class DungeonObligationCompletePlan(string key) : DbPlan
{
    internal string SourceKey => key;
    internal override int Bytes => 128 + key.Length * 2;
    internal override string Label => "dungeon-obligation/complete";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "UPDATE dungeon_reward_obligation SET completed=1 WHERE source_key=$key AND activated=1 AND completed=0");
        cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Dungeon obligation was already consumed or missing.");
        return 1;
    }
}
