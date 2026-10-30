using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Startup schema creation stays on the writer before any territory snapshot is read.</summary>
internal sealed class TerritorySchemaPlan(RealmStateKind kind) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "territory/schema-" + kind;

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        string sql = kind switch
        {
            RealmStateKind.TerritoryWars => """
                CREATE TABLE IF NOT EXISTS territory_wars(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS territory_wars_seasons(epoch TEXT PRIMARY KEY,state BLOB NOT NULL)
                """,
            RealmStateKind.TerritoryEscort =>
                "CREATE TABLE IF NOT EXISTS territory_escort(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL)",
            RealmStateKind.Tavern => "CREATE TABLE IF NOT EXISTS tavern(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL)",
            RealmStateKind.GuildLeague =>
                "CREATE TABLE IF NOT EXISTS guild_league(id INTEGER PRIMARY KEY CHECK(id=1),state BLOB NOT NULL)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var cmd = DbSql.Command(db, tx, sql);
        int changed = cmd.ExecuteNonQuery();
        string table = kind switch
        {
            RealmStateKind.TerritoryWars => "territory_wars",
            RealmStateKind.TerritoryEscort => "territory_escort",
            RealmStateKind.Tavern => "tavern",
            _ => "guild_league",
        };
        using var realm = DbSql.Command(db, tx, $"""
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'realm',$kind,'state',0 FROM {table} WHERE id=1
            """);
        realm.Parameters.AddWithValue("$kind", kind.ToString());
        realm.ExecuteNonQuery();
        if (kind == RealmStateKind.TerritoryWars)
            SchemaSql.Execute(db, tx, """
                INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                    SELECT 'territory_season',epoch,'state',0 FROM territory_wars_seasons
                """);
        return changed;
    }
}

/// <summary>A detached copy of the committed realm blob, including the absent-row case.</summary>
internal sealed class TerritoryRealmReadPlan(RealmStateKind kind) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "territory/read-" + kind;

    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        string table = kind switch
        {
            RealmStateKind.TerritoryWars => "territory_wars",
            RealmStateKind.TerritoryEscort => "territory_escort",
            RealmStateKind.Tavern => "tavern",
            RealmStateKind.GuildLeague => "guild_league",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var cmd = DbSql.Command(db, tx, $"SELECT state FROM {table} WHERE id=1");
        return cmd.ExecuteScalar() is byte[] bytes ? (byte[])bytes.Clone() : null;
    }
}

/// <summary>Tests and recovery may inspect whether an epoch is archived or currently active.</summary>
internal sealed class TerritoryWarsSeasonArchiveReadPlan(string epoch) : DbReadPlan
{
    internal override int Bytes => 64 + epoch.Length * sizeof(char);
    internal override string Label => "territory/read-season-archive";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT state FROM territory_wars_seasons WHERE epoch=$epoch");
        cmd.Parameters.AddWithValue("$epoch", epoch);
        return cmd.ExecuteScalar() is byte[] bytes ? (byte[])bytes.Clone() : null;
    }
}

/// <summary>Archive, target-epoch decode, and realm replacement share the guild refund transaction.</summary>
internal sealed class TerritoryWarsSeasonTransitionPlan : DbPlan
{
    private readonly string _oldEpoch, _nextEpoch;
    private readonly byte[] _oldState, _fallback;

    internal TerritoryWarsSeasonTransitionPlan(string oldEpoch, byte[] oldState, string nextEpoch, byte[] fallback)
    {
        _oldEpoch = oldEpoch;
        _oldState = (byte[])oldState.Clone();
        _nextEpoch = nextEpoch;
        _fallback = (byte[])fallback.Clone();
    }

    internal override int Bytes => 128 + (_oldEpoch.Length + _nextEpoch.Length) * sizeof(char)
        + _oldState.Length + _fallback.Length;
    internal override string Label => "territory/season-transition";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("realm", RealmStateKind.TerritoryWars),
         RevisionStore.Entity("territory_season", _oldEpoch),
         RevisionStore.Entity("territory_season", _nextEpoch)];

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var archive = DbSql.Command(db, tx, """
            INSERT INTO territory_wars_seasons(epoch,state) VALUES($epoch,$state)
            ON CONFLICT(epoch) DO UPDATE SET state=$state
            """))
        {
            archive.Parameters.AddWithValue("$epoch", _oldEpoch);
            archive.Parameters.AddWithValue("$state", _oldState);
            archive.ExecuteNonQuery();
        }
        byte[] next;
        using (var read = DbSql.Command(db, tx, "SELECT state FROM territory_wars_seasons WHERE epoch=$epoch"))
        {
            read.Parameters.AddWithValue("$epoch", _nextEpoch);
            if (read.ExecuteScalar() is byte[] bytes)
            {
                // The old transaction decoded this value before commit. Keep malformed archived state from
                // committing either the archive or any enlisted guild refund.
                var restored = JsonSerializer.Deserialize<TerritoryWarsService.WorldState>(bytes)
                    ?? throw new JsonException("Territory season archive is null.");
                next = JsonSerializer.SerializeToUtf8Bytes(restored);
            }
            else next = _fallback;
        }
        // A restored epoch has one holding location: its active row. The next departure archives it again.
        using (var remove = DbSql.Command(db, tx, "DELETE FROM territory_wars_seasons WHERE epoch=$epoch"))
        {
            remove.Parameters.AddWithValue("$epoch", _nextEpoch);
            remove.ExecuteNonQuery();
        }
        using (var save = DbSql.Command(db, tx, """
            INSERT INTO territory_wars(id,state) VALUES(1,$state)
            ON CONFLICT(id) DO UPDATE SET state=excluded.state
            """))
        {
            save.Parameters.AddWithValue("$state", next);
            save.ExecuteNonQuery();
        }
        return (byte[])next.Clone();
    }
}
