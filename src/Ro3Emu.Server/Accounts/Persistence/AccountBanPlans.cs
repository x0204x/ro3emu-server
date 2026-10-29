using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Account bans (docs/systems/account-bans.md). The admin panel writes account_ban rows into this database from its
/// own process; the server creates the table, reads it fresh on every credential check and never writes it. account
/// holds the account's stored name and matches account.name exactly. The whole feature is (inferred): the client has
/// no ban message we know, so a banned account is refused the way a wrong credential is.
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
