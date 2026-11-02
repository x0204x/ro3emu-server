using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class PronteraDefenseSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "prontera-defense/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "CREATE TABLE IF NOT EXISTS prontera_defense_world (season_open INTEGER NOT NULL, map_id INTEGER NOT NULL, defense_point INTEGER NOT NULL, PRIMARY KEY(season_open,map_id));"
            + "CREATE TABLE IF NOT EXISTS prontera_defense_station (season_open INTEGER NOT NULL, guild_id INTEGER NOT NULL, map_id INTEGER NOT NULL, PRIMARY KEY(season_open,guild_id));"
            + "CREATE TABLE IF NOT EXISTS prontera_defense_guild_prep (season_open INTEGER NOT NULL, guild_id INTEGER NOT NULL, exp INTEGER NOT NULL, PRIMARY KEY(season_open,guild_id));");
        int changed = cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'prontera_point',season_open || ':' || map_id,'state',0 FROM prontera_defense_world;
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'prontera_station',season_open || ':' || guild_id,'state',0 FROM prontera_defense_station;
            """);
        return changed;
    }
}

internal sealed record PronteraDefenseWorldRows(
    (long Map, long Point)[] Points, (long Guild, long Map)[] Stations);

internal sealed class PronteraDefenseWorldReadPlan(long open) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "prontera-defense/read-world";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var points = new List<(long Map, long Point)>();
        using (var cmd = DbSql.Command(db, tx,
            "SELECT map_id,defense_point FROM prontera_defense_world WHERE season_open=$open"))
        {
            cmd.Parameters.AddWithValue("$open", open);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) points.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        var stations = new List<(long Guild, long Map)>();
        using (var cmd = DbSql.Command(db, tx,
            "SELECT guild_id,map_id FROM prontera_defense_station WHERE season_open=$open"))
        {
            cmd.Parameters.AddWithValue("$open", open);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) stations.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        return new PronteraDefenseWorldRows([.. points], [.. stations]);
    }
}

internal sealed class PronteraDefenseStationPlan(long open, long guild, long map, bool enlist) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "prontera-defense/station";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("prontera_station", $"{open}:{guild}")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, enlist
            ? "INSERT INTO prontera_defense_station(season_open,guild_id,map_id) VALUES($open,$guild,$map) ON CONFLICT(season_open,guild_id) DO UPDATE SET map_id=excluded.map_id"
            : "DELETE FROM prontera_defense_station WHERE season_open=$open AND guild_id=$guild AND map_id=$map");
        cmd.Parameters.AddWithValue("$open", open);
        cmd.Parameters.AddWithValue("$guild", guild);
        cmd.Parameters.AddWithValue("$map", map);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class PronteraDefensePointPlan(long open, long map, long point) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "prontera-defense/point";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("prontera_point", $"{open}:{map}")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO prontera_defense_world(season_open,map_id,defense_point) VALUES($open,$map,$hp) "
            + "ON CONFLICT(season_open,map_id) DO UPDATE SET defense_point=excluded.defense_point");
        cmd.Parameters.AddWithValue("$open", open);
        cmd.Parameters.AddWithValue("$map", map);
        cmd.Parameters.AddWithValue("$hp", point);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>A guild's preparation exp for one opening: the supplies its members donated (82976 PronteraDefenseDonate).</summary>
internal sealed class PronteraDefenseGuildPrepPlan(long open, long guild, long exp) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "prontera-defense/guild-prep";
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites =>
        [RevisionStore.Entity("prontera_prep", $"{open}:{guild}")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO prontera_defense_guild_prep(season_open,guild_id,exp) VALUES($open,$guild,$exp) "
            + "ON CONFLICT(season_open,guild_id) DO UPDATE SET exp=exp+excluded.exp");
        cmd.Parameters.AddWithValue("$open", open);
        cmd.Parameters.AddWithValue("$guild", guild);
        cmd.Parameters.AddWithValue("$exp", exp);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class PronteraDefenseGuildPrepReadPlan(long open) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "prontera-defense/read-guild-prep";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var result = new List<(long Guild, long Exp)>();
        using var cmd = DbSql.Command(db, tx, "SELECT guild_id,exp FROM prontera_defense_guild_prep WHERE season_open=$open");
        cmd.Parameters.AddWithValue("$open", open);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return result.ToArray();
    }
}
