using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class MvpSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "mvp/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS mvp_state (cfg_id INTEGER PRIMARY KEY, state INTEGER NOT NULL, state_time INTEGER NOT NULL, dead_ms TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS mvp_line_cooldown (cfg_id INTEGER PRIMARY KEY, death_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS mvp_settlement (
                source_key TEXT PRIMARY KEY, cfg_id INTEGER NOT NULL, death_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS mvp_player (player_id INTEGER PRIMARY KEY, first_num INTEGER NOT NULL, end_num INTEGER NOT NULL, join_num INTEGER NOT NULL, team_num INTEGER NOT NULL, last_time INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS mvp_award_obligation (
                source_key TEXT PRIMARY KEY, cfg_id INTEGER NOT NULL, death_ms INTEGER NOT NULL,
                player_id INTEGER NOT NULL, awards BLOB NOT NULL, result BLOB NOT NULL,
                delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
            CREATE INDEX IF NOT EXISTS mvp_award_pending_player ON mvp_award_obligation(player_id,delivered);
            """);
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'mvp_state',cfg_id,'state',0 FROM mvp_state
            """);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'mvp_cooldown',cfg_id,'state',0 FROM mvp_line_cooldown
            """);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'role',player_id,'mvp_counter',0 FROM mvp_player
                WHERE player_id IN (SELECT role_id FROM role_lifecycle)
            """);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'mvp_obligation',source_key,'state',0 FROM mvp_award_obligation
            """);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'mvp_settlement',source_key,'state',0 FROM mvp_settlement
            """);
        return 1;
    }
}

internal sealed record MvpStateRow(int Config, int Phase, long Time, string Dead);
internal sealed record MvpCounterRow(long First, long End, long Join, long Team, long Last);
/// <summary>One mvp_award_obligation row (frozen awards and the 30002 result); the shared RoleObligation plans read,
/// insert and mark it.</summary>
internal sealed record MvpObligationRow(string SourceKey, int Config, long DeathMs, long PlayerId,
    byte[] Awards, byte[] Result) : IRoleObligationRow<MvpObligationRow>
{
    public static RoleObligationTable Table { get; } = new("mvp_award_obligation", "mvp_obligation", "player_id",
        ["source_key", "cfg_id", "death_ms", "player_id", "awards", "result"], "death_ms,source_key", "mvp",
        "mvp/read-pending");
    public static MvpObligationRow Read(SqliteDataReader reader) => new(reader.GetString(0), reader.GetInt32(1),
        reader.GetInt64(2), reader.GetInt64(3), (byte[])reader.GetValue(4), (byte[])reader.GetValue(5));
    public long RoleId => PlayerId;
    public object[] Values() => [SourceKey, Config, DeathMs, PlayerId, Awards, Result];
    public MvpObligationRow Detach() => this with { Awards = (byte[])Awards.Clone(), Result = (byte[])Result.Clone() };
    public bool SameFrozen(MvpObligationRow stored) => stored.PlayerId == PlayerId && stored.Config == Config
        && stored.DeathMs == DeathMs && stored.Awards.AsSpan().SequenceEqual(Awards) && stored.Result.AsSpan().SequenceEqual(Result);
}
internal sealed record MvpSettlementSnapshot(long DeathMs, MvpObligationRow[] Obligations);

/// <summary>Stable source claim, including kills whose capped participants receive no economic goods.</summary>
internal sealed class MvpSettlementPlan(string sourceKey, int config, long deathMs) : DbPlan
{
    internal override int Bytes => 128 + sourceKey.Length * 2;
    internal override string Label => "mvp/source";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("mvp_settlement", sourceKey)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO mvp_settlement(source_key,cfg_id,death_ms) VALUES($s,$c,$d)");
        cmd.Parameters.AddWithValue("$s", sourceKey);
        cmd.Parameters.AddWithValue("$c", config);
        cmd.Parameters.AddWithValue("$d", deathMs);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>One reader snapshot distinguishes a refused source from a committed frozen settlement.</summary>
internal sealed class MvpSettlementProbeReadPlan(string sourceKey) : DbReadPlan
{
    internal override int Bytes => 256 + sourceKey.Length * 2;
    internal override string Label => "mvp/probe-source";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long death;
        using (var source = DbSql.Command(db, tx,
            "SELECT death_ms FROM mvp_settlement WHERE source_key=$s"))
        {
            source.Parameters.AddWithValue("$s", sourceKey);
            if (source.ExecuteScalar() is not long found) return null;
            death = found;
        }
        using var cmd = DbSql.Command(db, tx, $"""
            SELECT {MvpObligationRow.Table.Select}
            FROM mvp_award_obligation WHERE source_key LIKE $prefix ORDER BY player_id
            """);
        cmd.Parameters.AddWithValue("$prefix", sourceKey + "/%");
        using var rows = cmd.ExecuteReader();
        var obligations = new List<MvpObligationRow>();
        while (rows.Read()) obligations.Add(MvpObligationRow.Read(rows));
        return new MvpSettlementSnapshot(death, obligations.ToArray());
    }
}
internal enum MvpReadKind { States, Cooldowns, Counters }

internal sealed class MvpReadPlan(MvpReadKind kind, long player = 0) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "mvp/read-" + kind;
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (kind)
        {
            case MvpReadKind.States:
                using (var cmd = DbSql.Command(db, tx, "SELECT cfg_id,state,state_time,dead_ms FROM mvp_state"))
                using (var reader = cmd.ExecuteReader())
                {
                    var rows = new List<MvpStateRow>();
                    while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3)));
                    return rows;
                }
            case MvpReadKind.Cooldowns:
                using (var cmd = DbSql.Command(db, tx, "SELECT cfg_id,death_ms FROM mvp_line_cooldown"))
                using (var reader = cmd.ExecuteReader())
                {
                    var rows = new List<(int Config, long Death)>();
                    while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetInt64(1)));
                    return rows;
                }
            case MvpReadKind.Counters:
                using (var cmd = DbSql.Command(db, tx,
                    "SELECT first_num,end_num,join_num,team_num,last_time FROM mvp_player WHERE player_id=$p"))
                {
                    cmd.Parameters.AddWithValue("$p", player);
                    using var reader = cmd.ExecuteReader();
                    return reader.Read() ? new MvpCounterRow(reader.GetInt64(0), reader.GetInt64(1),
                        reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)) : null;
                }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}

internal enum MvpWriteKind { State, Deadline, Counters }

internal sealed class MvpWritePlan : DbPlan
{
    private readonly MvpWriteKind _kind;
    private readonly int _config, _phase;
    private readonly long _id, _time;
    private readonly string _dead;
    private readonly MvpCounterRow? _counters;
    internal MvpWritePlan(int config, int phase, long time, string dead)
    { _kind = MvpWriteKind.State; _config = config; _phase = phase; _time = time; _dead = dead; }
    internal MvpWritePlan(int config, long death)
    { _kind = MvpWriteKind.Deadline; _config = config; _time = death; _dead = ""; }
    internal MvpWritePlan(long player, MvpCounterRow counters)
    { _kind = MvpWriteKind.Counters; _id = player; _counters = counters; _dead = ""; }
    internal override int Bytes => 128 + _dead.Length * 2;
    internal override string Label => "mvp/write-" + _kind;
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => _kind == MvpWriteKind.Counters
        ? [RevisionStore.Role(_id, RevisionStore.MvpCounter)] : [];
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => _kind switch
    {
        MvpWriteKind.State => [RevisionStore.Entity("mvp_state", _config)],
        MvpWriteKind.Deadline => [RevisionStore.Entity("mvp_cooldown", _config)],
        _ => []
    };
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (_kind)
        {
            case MvpWriteKind.State:
                using (var cmd = DbSql.Command(db, tx,
                    "INSERT OR REPLACE INTO mvp_state (cfg_id,state,state_time,dead_ms) VALUES($c,$s,$t,$d)"))
                {
                    cmd.Parameters.AddWithValue("$c", _config); cmd.Parameters.AddWithValue("$s", _phase);
                    cmd.Parameters.AddWithValue("$t", _time); cmd.Parameters.AddWithValue("$d", _dead);
                    return cmd.ExecuteNonQuery();
                }
            case MvpWriteKind.Deadline:
                using (var cmd = DbSql.Command(db, tx, "INSERT INTO mvp_line_cooldown(cfg_id,death_ms) VALUES($c,$d) "
                    + "ON CONFLICT(cfg_id) DO UPDATE SET death_ms=max(death_ms,excluded.death_ms)"))
                {
                    cmd.Parameters.AddWithValue("$c", _config); cmd.Parameters.AddWithValue("$d", _time);
                    return cmd.ExecuteNonQuery();
                }
            case MvpWriteKind.Counters:
                using (var cmd = DbSql.Command(db, tx,
                    "INSERT OR REPLACE INTO mvp_player(player_id,first_num,end_num,join_num,team_num,last_time) VALUES($p,$f,$e,$j,$t,$l)"))
                {
                    cmd.Parameters.AddWithValue("$p", _id);
                    cmd.Parameters.AddWithValue("$f", _counters!.First); cmd.Parameters.AddWithValue("$e", _counters.End);
                    cmd.Parameters.AddWithValue("$j", _counters.Join); cmd.Parameters.AddWithValue("$t", _counters.Team);
                    cmd.Parameters.AddWithValue("$l", _counters.Last);
                    return cmd.ExecuteNonQuery();
                }
            default: throw new ArgumentOutOfRangeException();
        }
    }
}
