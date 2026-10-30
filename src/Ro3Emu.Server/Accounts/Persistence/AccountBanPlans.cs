using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Account bans (docs/systems/account-bans.md). The server's web panel bans and unbans through the server's writer
/// (<see cref="AccountBanWritePlan"/>, <see cref="AccountUnbanPlan"/>); an external tool may still write rows into this
/// database from its own process, so the server reads the table fresh on every credential check. account holds the
/// account's stored name and matches account.name exactly. The whole feature is (inferred): the client has no ban
/// message we know, so a banned account is refused the way a wrong credential is.
/// </summary>
internal sealed class AccountBanSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "account-ban/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS account_ban (
                account TEXT NOT NULL PRIMARY KEY,
                reason TEXT NOT NULL DEFAULT '',
                banned_utc INTEGER NOT NULL,
                until_utc INTEGER,
                banned_by TEXT);
            """);
        return 1;
    }
}

/// <summary>
/// True when <paramref name="account"/> has an active ban at <paramref name="now"/> (unix seconds): its row exists and
/// until_utc is null or later than now. Uncached. <paramref name="anyCase"/>: match the name ignoring case, as the
/// account lookup and the registration's uniqueness do (AccountPlans), for a name about to be registered.
/// </summary>
internal sealed class AccountBanRead(string account, long now, bool anyCase = false) : DbReadPlan
{
    internal override int Bytes => 128 + Encoding.UTF8.GetByteCount(account);
    internal override string Label => "account-ban/read";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT EXISTS(SELECT 1 FROM account_ban WHERE account=$account " +
            (anyCase ? "COLLATE NOCASE " : "") + "AND (until_utc IS NULL OR until_utc > $now))");
        select.Parameters.AddWithValue("$account", account);
        select.Parameters.AddWithValue("$now", now);
        return (long)select.ExecuteScalar()! != 0;
    }
}

/// <summary>The account's ban row when it is active at <paramref name="now"/>, else null.</summary>
internal sealed class AccountBanRowRead(string account, long now) : DbReadPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(account);
    internal override string Label => "account-ban/row";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT reason,banned_utc,until_utc,banned_by FROM account_ban " +
            "WHERE account=$account AND (until_utc IS NULL OR until_utc > $now)");
        select.Parameters.AddWithValue("$account", account);
        select.Parameters.AddWithValue("$now", now);
        using var row = select.ExecuteReader();
        return row.Read()
            ? new AccountBanRow(account, row.GetString(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetInt64(2),
                row.IsDBNull(3) ? null : row.GetString(3))
            : null;
    }
}

/// <summary>One account_ban row; times in unix seconds, <see cref="UntilUtc"/> null = no end.</summary>
public sealed record AccountBanRow(string Account, string Reason, long BannedUtc, long? UntilUtc, string? BannedBy);

/// <summary>
/// Bans <paramref name="account"/> (insert or replace, as the table's documented writers do) and records the admin
/// action in admin_audit in the same transaction. False, and nothing written, when the account does not exist.
/// </summary>
internal sealed class AccountBanWritePlan(string account, string reason, long now, long? until, string admin) : DbPlan
{
    internal override int Bytes => 512 + Encoding.UTF8.GetByteCount(account + reason + admin);
    internal override string Label => "account-ban/ban";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var exists = DbSql.Command(db, tx, "SELECT EXISTS(SELECT 1 FROM account WHERE name=$account)"))
        {
            exists.Parameters.AddWithValue("$account", account);
            if ((long)exists.ExecuteScalar()! == 0) return false;
        }
        using var ban = DbSql.Command(db, tx, "INSERT OR REPLACE INTO account_ban(account,reason,banned_utc,until_utc,banned_by) " +
            "VALUES($account,$reason,$now,$until,$admin)");
        ban.Parameters.AddWithValue("$account", account);
        ban.Parameters.AddWithValue("$reason", reason);
        ban.Parameters.AddWithValue("$now", now);
        ban.Parameters.AddWithValue("$until", until is { } u ? u : DBNull.Value);
        ban.Parameters.AddWithValue("$admin", admin);
        ban.ExecuteNonQuery();
        string detail = (until is { } end ? "until " + DateTimeOffset.FromUnixTimeSeconds(end).ToString("u", System.Globalization.CultureInfo.InvariantCulture)
            : "no end") + ": " + reason;
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.Ban, account, detail);
        return true;
    }
}

/// <summary>Lifts the account's active ban (deletes its row) and records it; false when no active ban was there.</summary>
internal sealed class AccountUnbanPlan(string account, string reason, long now, string admin) : DbPlan
{
    internal override int Bytes => 512 + Encoding.UTF8.GetByteCount(account + reason + admin);
    internal override string Label => "account-ban/unban";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var delete = DbSql.Command(db, tx, "DELETE FROM account_ban WHERE account=$account AND (until_utc IS NULL OR until_utc > $now)");
        delete.Parameters.AddWithValue("$account", account);
        delete.Parameters.AddWithValue("$now", now);
        if (delete.ExecuteNonQuery() == 0) return false;
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.Unban, account, reason);
        return true;
    }
}
