using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

// 新版本使用旁表；既有 DungeonObligationFreezePlan 的欄位與獎勵收據均保持原樣。
internal sealed class DungeonTaskClearFreezePlan : DbPlan
{
    private readonly DungeonObligationFreezePlan _rewards;
    private readonly (DungeonTaskClear Clear, byte[] State)[] _clears;
    internal DungeonTaskClearFreezePlan(IReadOnlyList<DungeonRewardObligation> rewards,
        IReadOnlyList<DungeonTaskClear> clears)
    {
        if (rewards.Count != clears.Count || rewards.Select(r => r.Key).Distinct().Count() != rewards.Count
            || clears.Select(c => c.SourceKey).Distinct().Count() != clears.Count
            || clears.Any(c => !rewards.Any(r => r.Key == c.SourceKey && r.RoleId == c.RoleId
                && r.RoomId == c.RoomId && r.SceneEntry == c.SceneEntry && (r.Success || c.Targets.Count == 0))))
            throw new ArgumentException("副本獎勵與任務快照未一一對應。");
        _rewards = new(rewards);
        _clears = clears.Select(c => (c, c.Encode())).ToArray();
    }
    internal override int Bytes => _rewards.Bytes + _clears.Sum(c => c.State.Length + 128);
    internal override string Label => "dungeon-obligation/freeze-task-clears-v1";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var existed = new HashSet<string>();
        foreach (var (clear, _) in _clears)
        {
            using var read = DbSql.Command(db, tx, "SELECT 1 FROM dungeon_reward_obligation WHERE source_key=$key");
            read.Parameters.AddWithValue("$key", clear.SourceKey);
            if (read.ExecuteScalar() != null) existed.Add(clear.SourceKey);
        }
        _rewards.Execute(db, tx);
        foreach (var (clear, state) in _clears)
        {
            using var read = DbSql.Command(db, tx, "SELECT state FROM dungeon_task_clear_outbox WHERE source_key=$key");
            read.Parameters.AddWithValue("$key", clear.SourceKey);
            var old = read.ExecuteScalar() as byte[];
            if (old != null)
            {
                if (!old.AsSpan().SequenceEqual(state)) throw new InvalidDataException("副本任務快照來源衝突。");
                continue;
            }
            if (existed.Contains(clear.SourceKey))
                throw new InvalidDataException("舊副本來源缺少任務快照，不能用現有任務補建。");
            using var insert = DbSql.Command(db, tx, """
                INSERT INTO dungeon_task_clear_outbox(source_key,role_id,room_id,applied,state)
                VALUES($key,$role,$room,0,$state)
                """);
            insert.Parameters.AddWithValue("$key", clear.SourceKey);
            insert.Parameters.AddWithValue("$role", clear.RoleId);
            insert.Parameters.AddWithValue("$room", clear.RoomId);
            insert.Parameters.AddWithValue("$state", state);
            insert.ExecuteNonQuery();
        }
        return _clears.Length;
    }
}

internal sealed class DungeonTaskClearReadPlan(long role, bool earnedOnly = true) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "dungeon-task-clear/pending";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var read = DbSql.Command(db, tx, """
            SELECT o.source_key,o.room_id,o.state FROM dungeon_task_clear_outbox o
            JOIN dungeon_reward_obligation r ON r.source_key=o.source_key AND r.role_id=o.role_id AND r.room_id=o.room_id
            WHERE o.role_id=$role AND o.applied=0
            """ + (earnedOnly ? " AND r.activated=1 AND r.completed=1" : "") + " ORDER BY o.rowid");
        read.Parameters.AddWithValue("$role", role);
        using var reader = read.ExecuteReader();
        var rows = new List<DungeonTaskClear>();
        while (reader.Read())
        {
            var row = DungeonTaskClear.Decode((byte[])reader[2]);
            if (row.SourceKey != reader.GetString(0) || row.RoleId != role || row.RoomId != reader.GetInt64(1))
                throw new InvalidDataException("副本任務旁表識別不符。");
            rows.Add(row);
        }
        return rows;
    }
}

internal sealed class DungeonTaskClearAcknowledgePlan(string source, long role) : DbPlan
{
    internal override int Bytes => 128 + source.Length * 2;
    internal override string Label => "dungeon-task-clear/ack";
    internal override IReadOnlyList<long> RolePins => [role];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var update = DbSql.Command(db, tx, """
            UPDATE dungeon_task_clear_outbox SET applied=1
            WHERE source_key=$key AND role_id=$role AND applied=0
              AND EXISTS(SELECT 1 FROM dungeon_reward_obligation r
                WHERE r.source_key=$key AND r.role_id=$role AND r.activated=1 AND r.completed=1)
            """);
        update.Parameters.AddWithValue("$key", source);
        update.Parameters.AddWithValue("$role", role);
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本任務來源尚未入帳、已消費或不存在。");
        return 1;
    }
}
