using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// The web panel's staff and security tables (docs/guide/panel.md): panel_role (staff roles an Admin gave on the panel),
/// account_login (the login history), account_totp (two-factor secrets and hashed recovery codes), feedback_triage and
/// feedback_note (the report inbox's status, assignee and notes) and panel_setting (maintenance mode). None is read by
/// the game, except that the gate asks for maintenance and staff through the services that load them.
/// </summary>
internal sealed class PanelSecuritySchemaPlan : DbPlan
{
    internal override int Bytes => 512;
    internal override string Label => "panel/security-schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS panel_role (
                account TEXT PRIMARY KEY,
                role TEXT NOT NULL,
                granted_by TEXT NOT NULL,
                granted_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS account_login (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                account TEXT NOT NULL,
                utc INTEGER NOT NULL,
                source TEXT NOT NULL,
                address TEXT);
            CREATE INDEX IF NOT EXISTS account_login_account ON account_login(account, id);
            CREATE TABLE IF NOT EXISTS account_totp (
                account TEXT PRIMARY KEY,
                secret BLOB NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 0,
                last_step INTEGER NOT NULL DEFAULT 0,
                recovery TEXT NOT NULL DEFAULT '',
                created_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS feedback_triage (
                report_id INTEGER PRIMARY KEY,
                status TEXT NOT NULL,
                assignee TEXT,
                updated_utc INTEGER NOT NULL,
                updated_by TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS feedback_note (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                report_id INTEGER NOT NULL,
                utc INTEGER NOT NULL,
                author TEXT NOT NULL,
                text TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS feedback_note_report ON feedback_note(report_id, id);
            CREATE TABLE IF NOT EXISTS panel_setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        return 1;
    }
}

// ---------------------------------------------------------------- staff roles

internal sealed record StaffRoleRow(string Account, string Role, string GrantedBy, long GrantedUtc);

internal sealed class PanelRolesListRead : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "panel/staff";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT account,role,granted_by,granted_utc FROM panel_role ORDER BY account");
        using var rows = select.ExecuteReader();
        var list = new List<StaffRoleRow>();
        while (rows.Read()) list.Add(new StaffRoleRow(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetInt64(3)));
        return list;
    }
}

/// <summary>
/// Gives an existing account (any letter case) a staff role, or removes it (<paramref name="role"/> empty), with its
/// audit row. The account's stored name, or null when there is no such account.
/// </summary>
internal sealed class PanelRoleWritePlan(string account, string role, string admin, long now) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(account + admin);
    internal override string Label => "panel/role-write";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var find = DbSql.Command(db, tx, "SELECT name FROM account WHERE name=$name COLLATE NOCASE ORDER BY name=$name DESC, rowid LIMIT 1");
        find.Parameters.AddWithValue("$name", account);
        if (find.ExecuteScalar() is not string stored) return null;
        using var write = DbSql.Command(db, tx, role.Length == 0
            ? "DELETE FROM panel_role WHERE account=$account"
            : "INSERT INTO panel_role(account,role,granted_by,granted_utc) VALUES($account,$role,$admin,$now) " +
              "ON CONFLICT(account) DO UPDATE SET role=$role, granted_by=$admin, granted_utc=$now");
        write.Parameters.AddWithValue("$account", stored);
        write.Parameters.AddWithValue("$role", role);
        write.Parameters.AddWithValue("$admin", admin);
        write.Parameters.AddWithValue("$now", now);
        write.ExecuteNonQuery();
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.Role, stored, role.Length == 0 ? "no staff role" : "role " + role);
        return stored;
    }
}

// ---------------------------------------------------------------- login history

internal sealed record AccountLoginRow(long Utc, string Source, string? Address);

/// <summary>One successful login; the account keeps its newest <see cref="Keep"/> rows.</summary>
internal sealed class AccountLoginWritePlan(string account, long now, string source, string? address) : DbPlan
{
    internal const int Keep = 100;
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(account);
    internal override string Label => "panel/login-record";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var insert = DbSql.Command(db, tx, "INSERT INTO account_login(account,utc,source,address) VALUES($account,$now,$source,$address)");
        insert.Parameters.AddWithValue("$account", account);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$source", source);
        insert.Parameters.AddWithValue("$address", address is null ? DBNull.Value : address);
        insert.ExecuteNonQuery();
        using var prune = DbSql.Command(db, tx, "DELETE FROM account_login WHERE account=$account AND id NOT IN " +
            "(SELECT id FROM account_login WHERE account=$account ORDER BY id DESC LIMIT $keep)");
        prune.Parameters.AddWithValue("$account", account);
        prune.Parameters.AddWithValue("$keep", Keep);
        prune.ExecuteNonQuery();
        return true;
    }
}

internal sealed class AccountLoginsRead(string account, int limit) : DbReadPlan
{
    internal override int Bytes => 128 + limit * 96;
    internal override string Label => "panel/logins";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT utc,source,address FROM account_login WHERE account=$account ORDER BY id DESC LIMIT $limit");
        select.Parameters.AddWithValue("$account", account);
        select.Parameters.AddWithValue("$limit", limit);
        using var rows = select.ExecuteReader();
        var list = new List<AccountLoginRow>();
        while (rows.Read()) list.Add(new AccountLoginRow(rows.GetInt64(0), rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2)));
        return list;
    }
}

// ---------------------------------------------------------------- settings

internal sealed class PanelSettingRead(string key) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "panel/setting";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT value FROM panel_setting WHERE key=$key");
        select.Parameters.AddWithValue("$key", key);
        return select.ExecuteScalar() as string;
    }
}

/// <summary>Sets one panel_setting row with the audit row of the admin who changed it.</summary>
internal sealed class PanelSettingWritePlan(string key, string value, string admin, string action, string detail, long now) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(value + detail);
    internal override string Label => "panel/setting-write";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var write = DbSql.Command(db, tx, "INSERT INTO panel_setting(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value");
        write.Parameters.AddWithValue("$key", key);
        write.Parameters.AddWithValue("$value", value);
        write.ExecuteNonQuery();
        AdminAudit.Insert(db, tx, now, admin, action, "server", detail);
        return true;
    }
}

internal static class PanelSettingKeys
{
    internal const string Maintenance = "maintenance";
}
