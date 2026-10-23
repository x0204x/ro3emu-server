using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class GuildSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS guild (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                state BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS guild_member (
                role_id INTEGER PRIMARY KEY,
                guild_id INTEGER NOT NULL REFERENCES guild(id)
            );
            CREATE INDEX IF NOT EXISTS guild_member_guild ON guild_member(guild_id);
            CREATE TABLE IF NOT EXISTS guild_market (
                id INTEGER PRIMARY KEY CHECK(id=1),
                state BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS guild_recovery (
                guild_id INTEGER PRIMARY KEY,
                state BLOB NOT NULL,
                created_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS guild_event (
                guild_id INTEGER NOT NULL,
                idx INTEGER NOT NULL,
                data BLOB NOT NULL,
                PRIMARY KEY(guild_id, idx)
            );
            """);
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'guild',id,'state',0 FROM guild;
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'guild_market',1,'state',0 FROM guild_market;
            """);
        return 1;
    }
}

internal enum GuildReadKind { Market, Guild, ForRole, List, NameExists }

internal sealed class GuildReadPlan(GuildReadKind kind, long id = 0, string name = "") : DbReadPlan
{
    internal override int Bytes => 128 + name.Length * 2;
    internal override string Label => "guild/read-" + kind;
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (kind)
        {
            case GuildReadKind.Market:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM guild_market WHERE id=1"))
                {
                    var result = cmd.ExecuteScalar() is byte[] market
                        ? JsonSerializer.Deserialize<GuildStore.MarketState>(market) ?? new() : new GuildStore.MarketState();
                    result.CapturedRevision = (OwnerRevisionValue)new OwnerRevisionReadPlan(RevisionStore.Entity("guild_market", 1))
                        .Execute(db, tx);
                    return result;
                }
            case GuildReadKind.Guild:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM guild WHERE id=$id"))
                {
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteScalar() is byte[] row
                        ? WithRevision(db, tx, JsonSerializer.Deserialize<GuildStore.State>(row)!) : null;
                }
            case GuildReadKind.ForRole:
                using (var cmd = DbSql.Command(db, tx,
                    "SELECT g.state FROM guild_member m JOIN guild g ON g.id=m.guild_id WHERE m.role_id=$role"))
                {
                    cmd.Parameters.AddWithValue("$role", id);
                    return cmd.ExecuteScalar() is byte[] row
                        ? WithRevision(db, tx, JsonSerializer.Deserialize<GuildStore.State>(row)!) : null;
                }
            case GuildReadKind.List:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM guild ORDER BY id"))
                using (var reader = cmd.ExecuteReader())
                {
                    var rows = new List<GuildStore.State>();
                    while (reader.Read()) rows.Add(JsonSerializer.Deserialize<GuildStore.State>((byte[])reader[0])!);
                    reader.Close();
                    foreach (var guild in rows) WithRevision(db, tx, guild);
                    return rows;
                }
            case GuildReadKind.NameExists:
                using (var cmd = DbSql.Command(db, tx, "SELECT 1 FROM guild WHERE name=$name COLLATE NOCASE AND id<>$id"))
                {
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteScalar() != null;
                }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static GuildStore.State WithRevision(SqliteConnection db, SqliteTransaction tx, GuildStore.State state)
    {
        state.CapturedRevision = (OwnerRevisionValue)new OwnerRevisionReadPlan(RevisionStore.Entity("guild", state.Id))
            .Execute(db, tx);
        return state;
    }
}

internal sealed class GuildMarketSavePlan(GuildStore.MarketState market) : DbPlan
{
    private readonly byte[] _state = JsonSerializer.SerializeToUtf8Bytes(market);
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "guild/market-save";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_market", 1)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO guild_market(id,state) VALUES(1,$state) ON CONFLICT(id) DO UPDATE SET state=$state");
        cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class GuildDeletePlan(long guild) : DbPlan
{
    private long[]? _members;
    internal override int Bytes => 128;
    internal override string Label => "guild/delete";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild", guild)];
    internal override void ResolveRoleWrites(AccountStore accounts) =>
        _members ??= accounts.ReadSync<long[]>(new GuildMemberIdsReadPlan(guild));
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => (_members ??
        throw new InvalidOperationException("Guild membership was not captured before deletion."))
        .Select(role => RevisionStore.Role(role, RevisionStore.GuildMembership)).ToArray();
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(WrittenCache.Guild);
        using var cmd = DbSql.Command(db, tx,
            "DELETE FROM guild_member WHERE guild_id=$id; DELETE FROM guild_event WHERE guild_id=$id; DELETE FROM guild WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", guild);
        return cmd.ExecuteNonQuery();
    }
}
