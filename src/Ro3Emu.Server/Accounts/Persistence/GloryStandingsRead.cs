using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Every live role's saved territory state (role.territory_wars), for a season's Battle Merit standings.</summary>
internal sealed class GloryStandingsRead : DbReadPlan
{
    internal override int Bytes => 1024;
    internal override string Label => "territory/glory-standings";

    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = new List<(long Role, byte[] State)>();
        using var cmd = DbSql.Command(db, tx,
            "SELECT id, territory_wars FROM role WHERE deleted_time=0 AND territory_wars IS NOT NULL ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetInt64(0), (byte[])r.GetValue(1)));
        return rows;
    }
}
