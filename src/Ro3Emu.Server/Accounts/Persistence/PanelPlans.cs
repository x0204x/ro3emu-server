using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// The web panel's own tables (docs/guide/panel.md): admin_audit, one row per admin action, written in the transaction
/// of the action it records; panel_news, the news the public pages show (empty until an admin writes some). Neither is
/// read by the game.
/// </summary>
internal sealed class PanelSchemaPlan : DbPlan
{
    internal override int Bytes => 512;
    internal override string Label => "panel/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS admin_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                utc INTEGER NOT NULL,
                admin TEXT NOT NULL,
                action TEXT NOT NULL,
                target TEXT NOT NULL DEFAULT '',
                detail TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS panel_news (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                body TEXT NOT NULL DEFAULT '',
                tag TEXT NOT NULL DEFAULT 'news',
                published_utc INTEGER NOT NULL,
                author TEXT NOT NULL DEFAULT '');
            """);
        // Pinned news stay on top of the list whatever their date (the news editor).
        SchemaSql.AddColumn(db, tx, "panel_news", "pinned", "INTEGER NOT NULL DEFAULT 0");
        return 1;
    }
}

/// <summary>admin_audit rows, inserted by the plan that performs the action.</summary>
internal static class AdminAudit
{
    /// <summary>The action column's values.</summary>
    internal const string Ban = "ban", Unban = "unban", Mail = "mail", Gm = "gm", Broadcast = "broadcast",
        NewsSave = "news-save", NewsDelete = "news-delete", GiftCodeCreate = "gift-code-create",
        GiftCodeDisable = "gift-code-disable", GiftCodeEnable = "gift-code-enable";

    internal static void Insert(SqliteConnection db, SqliteTransaction tx, long now, string admin, string action, string target, string detail)
    {
        using var insert = DbSql.Command(db, tx, "INSERT INTO admin_audit(utc,admin,action,target,detail) VALUES($utc,$admin,$action,$target,$detail)");
        insert.Parameters.AddWithValue("$utc", now);
        insert.Parameters.AddWithValue("$admin", admin);
        insert.Parameters.AddWithValue("$action", action);
        insert.Parameters.AddWithValue("$target", target);
        insert.Parameters.AddWithValue("$detail", detail);
        insert.ExecuteNonQuery();
    }
}

internal sealed record AdminAuditRow(long Id, long Utc, string Admin, string Action, string Target, string Detail);

/// <summary>
/// A page of admin_audit, newest first, with the total row count; only one <paramref name="action"/> and one
/// <paramref name="admin"/>'s rows when they are given (the GM console's history).
/// </summary>
internal sealed class AdminAuditRead(int offset, int limit, string? action = null, string? admin = null) : DbReadPlan
{
    private const string Filter = " WHERE ($action IS NULL OR action=$action) AND ($admin IS NULL OR admin=$admin)";

    internal override int Bytes => 128 + limit * 256;
    internal override string Label => "panel/audit";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var count = DbSql.Command(db, tx, "SELECT COUNT(*) FROM admin_audit" + Filter);
        count.Parameters.AddWithValue("$action", action is null ? DBNull.Value : action);
        count.Parameters.AddWithValue("$admin", admin is null ? DBNull.Value : admin);
        int total = (int)(long)count.ExecuteScalar()!;
        using var select = DbSql.Command(db, tx, "SELECT id,utc,admin,action,target,detail FROM admin_audit" + Filter +
            " ORDER BY id DESC LIMIT $limit OFFSET $offset");
        select.Parameters.AddWithValue("$action", action is null ? DBNull.Value : action);
        select.Parameters.AddWithValue("$admin", admin is null ? DBNull.Value : admin);
        select.Parameters.AddWithValue("$limit", limit);
        select.Parameters.AddWithValue("$offset", offset);
        using var rows = select.ExecuteReader();
        var entries = new List<AdminAuditRow>();
        while (rows.Read())
            entries.Add(new AdminAuditRow(rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5)));
        return (total, entries);
    }
}

internal sealed record PanelNewsRow(long Id, string Title, string Body, string Tag, long PublishedUtc, string Author, bool Pinned);

/// <summary>The panel_news rows already published at <paramref name="now"/>, pinned first, then newest; with
/// <paramref name="scheduled"/> the ones published later too (the news editor).</summary>
internal sealed class PanelNewsRead(int limit, long now, bool scheduled = false) : DbReadPlan
{
    internal override int Bytes => 128 + limit * 1024;
    internal override string Label => "panel/news";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT id,title,body,tag,published_utc,author,pinned FROM panel_news " +
            (scheduled ? "" : "WHERE published_utc <= $now ") + "ORDER BY pinned DESC, published_utc DESC, id DESC LIMIT $limit");
        select.Parameters.AddWithValue("$now", now);
        select.Parameters.AddWithValue("$limit", limit);
        using var rows = select.ExecuteReader();
        var news = new List<PanelNewsRow>();
        while (rows.Read())
            news.Add(new PanelNewsRow(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt64(4),
                rows.GetString(5), rows.GetInt64(6) != 0));
        return news;
    }
}

/// <summary>
/// One account as the panel lists it: stored name, creation time (ISO text, as AccountRegisterPlan writes it), its
/// characters' count and latest login (unix seconds), and whether a ban is active.
/// </summary>
internal sealed record PanelAccountRow(string Name, string CreatedUtc, int Characters, long? LastLogin, bool Banned);

/// <summary>
/// Accounts for the admin's list: names containing <paramref name="query"/> in any ASCII letter case (LIKE, with the
/// wildcards of the query escaped), by name, one page, with the total. An empty query lists every account.
/// </summary>
internal sealed class PanelAccountSearch(string query, int offset, int limit, long now) : DbReadPlan
{
    internal override int Bytes => 128 + Encoding.UTF8.GetByteCount(query) + limit * 256;
    internal override string Label => "panel/account-search";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        string pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        using var count = DbSql.Command(db, tx, "SELECT COUNT(*) FROM account WHERE name LIKE $pattern ESCAPE '\\'");
        count.Parameters.AddWithValue("$pattern", pattern);
        int total = (int)(long)count.ExecuteScalar()!;
        using var select = DbSql.Command(db, tx, "SELECT " + PanelAccountRead.Columns + " FROM account a WHERE a.name LIKE $pattern ESCAPE '\\' " +
            "ORDER BY a.name COLLATE NOCASE, a.rowid LIMIT $limit OFFSET $offset");
        select.Parameters.AddWithValue("$pattern", pattern);
        select.Parameters.AddWithValue("$now", now);
        select.Parameters.AddWithValue("$limit", limit);
        select.Parameters.AddWithValue("$offset", offset);
        return (total, PanelAccountRead.Rows(select));
    }
}

/// <summary>One account by name in any letter case, as the login finds it (AccountCredentialsRead); null when none.</summary>
internal sealed class PanelAccountRead(string name, long now) : DbReadPlan
{
    /// <summary>The columns of <see cref="PanelAccountRow"/> for an account aliased a; $now dates the ban check.</summary>
    internal const string Columns = "a.name, a.created_utc, " +
        "(SELECT COUNT(*) FROM role r WHERE r.account=a.name AND r.deleted_time=0), " +
        "(SELECT MAX(r.last_login_time) FROM role r WHERE r.account=a.name AND r.deleted_time=0), " +
        "EXISTS(SELECT 1 FROM account_ban b WHERE b.account=a.name AND (b.until_utc IS NULL OR b.until_utc > $now))";

    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(name);
    internal override string Label => "panel/account";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT " + Columns +
            " FROM account a WHERE a.name=$name COLLATE NOCASE ORDER BY a.name=$name DESC, a.rowid LIMIT 1");
        select.Parameters.AddWithValue("$name", name);
        select.Parameters.AddWithValue("$now", now);
        return Rows(select).FirstOrDefault();
    }

    internal static List<PanelAccountRow> Rows(SqliteCommand select)
    {
        using var rows = select.ExecuteReader();
        var accounts = new List<PanelAccountRow>();
        while (rows.Read())
            accounts.Add(new PanelAccountRow(rows.GetString(0), rows.GetString(1), rows.GetInt32(2),
                rows.IsDBNull(3) ? null : rows.GetInt64(3), rows.GetInt64(4) != 0));
        return accounts;
    }
}

/// <summary>A character as the panel shows it; times in unix seconds, Guild empty when it has none.</summary>
internal sealed record PanelRoleRow(long Id, string Account, string Name, int Career, int Level, int JobLevel, long Exp,
    long SceneEntry, long CreateTime, long LastLoginTime, string Guild);

/// <summary>Characters (not deleted) with their guild's name: one account's, by latest login, or the
/// <paramref name="limit"/> highest by base level, then EXP inside the level, then age (the level board).</summary>
internal sealed class PanelRolesRead(string? account, int limit) : DbReadPlan
{
    internal override int Bytes => 256 + limit * 256;
    internal override string Label => account == null ? "panel/level-board" : "panel/roles";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        string sql = "SELECT r.id,r.account,r.name,r.career,r.level,r.job_level,r.exp,r.scene_entry,r.create_time,r.last_login_time," +
            "COALESCE(g.name,'') FROM role r LEFT JOIN guild_member m ON m.role_id=r.id LEFT JOIN guild g ON g.id=m.guild_id " +
            "WHERE r.deleted_time=0 " + (account == null
                ? "ORDER BY r.level DESC, r.exp DESC, r.id LIMIT $limit"
                : "AND r.account=$account ORDER BY r.last_login_time DESC, r.id LIMIT $limit");
        using var select = DbSql.Command(db, tx, sql);
        select.Parameters.AddWithValue("$limit", limit);
        if (account != null) select.Parameters.AddWithValue("$account", account);
        using var rows = select.ExecuteReader();
        var roles = new List<PanelRoleRow>();
        while (rows.Read())
            roles.Add(new PanelRoleRow(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3), rows.GetInt32(4),
                rows.GetInt32(5), rows.GetInt64(6), rows.GetInt64(7), rows.GetInt64(8), rows.GetInt64(9), rows.GetString(10)));
        return roles;
    }
}

/// <summary>Names, classes and levels of the given characters (a leaderboard's rows), by id.</summary>
internal sealed class PanelRoleNamesRead(IReadOnlyList<long> ids) : DbReadPlan
{
    internal override int Bytes => 128 + ids.Count * 64;
    internal override string Label => "panel/role-names";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var names = new Dictionary<long, (string Name, int Career, int Level)>();
        if (ids.Count == 0) return names;
        using var select = DbSql.Command(db, tx, "SELECT id,name,career,level FROM role WHERE deleted_time=0 AND id IN (" +
            string.Join(',', ids.Select((_, i) => "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")");
        for (int i = 0; i < ids.Count; i++) select.Parameters.AddWithValue("$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ids[i]);
        using var rows = select.ExecuteReader();
        while (rows.Read()) names[rows.GetInt64(0)] = (rows.GetString(1), rows.GetInt32(2), rows.GetInt32(3));
        return names;
    }
}

/// <summary>How many accounts and (not deleted) characters the database holds.</summary>
internal sealed class PanelCountsRead : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "panel/counts";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT (SELECT COUNT(*) FROM account), (SELECT COUNT(*) FROM role WHERE deleted_time=0)");
        using var row = select.ExecuteReader();
        row.Read();
        return (row.GetInt32(0), row.GetInt32(1));
    }
}
