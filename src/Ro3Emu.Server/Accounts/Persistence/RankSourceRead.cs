using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>The committed per-role state the leaderboards rank (Micro/RankService), online and offline roles alike.</summary>
internal sealed record RankSourceRole(long Id, string Name, int Career, int Level, byte[]? Tower, byte[]? Training,
    byte[]? Wardrobe, byte[]? TimedBattle, byte[]? Emblems, byte[]? Pets, byte[]? TerritoryWars);

internal sealed record RankSource(IReadOnlyList<RankSourceRole> Roles, IReadOnlyDictionary<long, List<InvItem>> Items);

/// <summary>One consistent read of every live role's ranked blobs and attributed item instances.</summary>
internal sealed class RankSourceRead : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "rank/source";

    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var roles = new List<RankSourceRole>();
        using (var cmd = DbSql.Command(db, tx,
            "SELECT id, name, career, level, tower, training, wardrobe, timed_battle, emblems, pets, territory_wars FROM role WHERE deleted_time=0 ORDER BY id"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                roles.Add(new RankSourceRole(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                    Blob(r, 4), Blob(r, 5), Blob(r, 6), Blob(r, 7), Blob(r, 8), Blob(r, 9), Blob(r, 10)));
        return new RankSource(roles, ItemStore.ReadAttributedItems(db, tx));
    }

    private static byte[]? Blob(SqliteDataReader r, int column) => r.IsDBNull(column) ? null : (byte[])r.GetValue(column);
}
