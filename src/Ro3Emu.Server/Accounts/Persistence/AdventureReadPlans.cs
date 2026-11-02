using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class AdventureSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "adventure/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS activity_pre_progress (
                activity_id INTEGER NOT NULL, cycle_start INTEGER NOT NULL, area INTEGER NOT NULL,
                score INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(activity_id, cycle_start, area));
            CREATE TABLE IF NOT EXISTS activity_pre_donation_area (
                activity_id INTEGER NOT NULL, cycle_start INTEGER NOT NULL, area INTEGER NOT NULL, day INTEGER NOT NULL,
                role_id INTEGER NOT NULL, amount INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(activity_id, cycle_start, area, day, role_id));
            CREATE TABLE IF NOT EXISTS activity_eve_global (
                activity_id INTEGER NOT NULL, cycle_start INTEGER NOT NULL, blob BLOB NOT NULL,
                PRIMARY KEY(activity_id, cycle_start));
            """);
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'activity_eve_global',activity_id || ':' || cycle_start,'state',0 FROM activity_eve_global
            """);
        return 1;
    }
}

internal sealed record AdventurePreTotals(long Score, long Today, long Submitters);

internal sealed class AdventurePreTotalsReadPlan(long activity, long cycle, int area, long day, long role) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "adventure/pre-totals";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            SELECT COALESCE((SELECT score FROM activity_pre_progress WHERE activity_id=$activity AND cycle_start=$cycle AND area=$area),0),
                   COALESCE((SELECT amount FROM activity_pre_donation_area WHERE activity_id=$activity AND cycle_start=$cycle AND area=$area AND day=$day AND role_id=$role),0),
                   (SELECT COUNT(*) FROM activity_pre_donation_area WHERE activity_id=$activity AND cycle_start=$cycle AND area=$area AND day=$day AND amount>0)
            """);
        cmd.Parameters.AddWithValue("$activity", activity); cmd.Parameters.AddWithValue("$cycle", cycle);
        cmd.Parameters.AddWithValue("$area", area); cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$role", role);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new AdventurePreTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}

internal sealed class AdventureEveGlobalReadPlan(long activity, long cycle) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "adventure/eve-global";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "SELECT blob FROM activity_eve_global WHERE activity_id=$activity AND cycle_start=$cycle");
        cmd.Parameters.AddWithValue("$activity", activity);
        cmd.Parameters.AddWithValue("$cycle", cycle);
        return cmd.ExecuteScalar() is byte[] blob ? (byte[])blob.Clone() : null;
    }
}

internal sealed class ActivityInventoryReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "adventure/read-inventory";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx) => ItemStore.ReadItems(db, tx, role);
}

internal sealed class ActivityInventoryGuardPlan : DbPlan
{
    private readonly long _role;
    private readonly ItemValue[] _before;
    internal ActivityInventoryGuardPlan(long role, IReadOnlyList<InvItem> before)
    { _role = role; _before = [.. before.OrderBy(i => i.Guid).Select(ItemValue.Capture)]; }
    internal override int Bytes => 128 + _before.Sum(i => 160 +
        ((i.Attrs?.Length ?? 0) + (i.Enchantments?.Length ?? 0) + (i.StringAttrs?.Length ?? 0)) * 2);
    internal override string Label => "adventure/guard-inventory";
    internal override IReadOnlyList<long> RolePins => [_role];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (!ItemStore.ReadItems(db, tx, _role).OrderBy(i => i.Guid).Select(ItemValue.Capture).SequenceEqual(_before))
            throw new InvalidOperationException($"Adventure inventory for role {_role} changed during capture.");
        return true;
    }
}
