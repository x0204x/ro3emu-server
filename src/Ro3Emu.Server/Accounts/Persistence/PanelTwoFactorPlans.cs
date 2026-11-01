using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>An account's account_totp row: the secret, whether it is confirmed, the last step a code was taken
/// from (a code is good once) and the SHA-256 hashes of the unused recovery codes.</summary>
internal sealed record TotpRow(byte[] Secret, bool Enabled, long LastStep, IReadOnlyList<string> Recovery);

internal static class TotpColumns
{
    internal static IReadOnlyList<string> Recovery(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

internal sealed class TotpRead(string account) : DbReadPlan
{
    internal override int Bytes => 512;
    internal override string Label => "panel/totp";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT secret,enabled,last_step,recovery FROM account_totp WHERE account=$account");
        select.Parameters.AddWithValue("$account", account);
        using var row = select.ExecuteReader();
        if (!row.Read()) return null;
        return new TotpRow((byte[])row.GetValue(0), row.GetInt64(1) != 0, row.GetInt64(2), TotpColumns.Recovery(row.GetString(3)));
    }
}

/// <summary>Which accounts have 2FA on (the staff list).</summary>
internal sealed class TotpEnabledRead : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "panel/totp-enabled";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT account FROM account_totp WHERE enabled=1");
        using var rows = select.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read()) set.Add(rows.GetString(0));
        return set;
    }
}

internal enum TotpWriteKind { Begin, Enable, Use, UseRecovery, ReplaceRecovery, Disable }

/// <summary>
/// The account_totp changes, each one conditional so two requests cannot both win: Begin stores a waiting secret
/// (not over an enabled one), Enable confirms it with the matched step and the recovery hashes, Use moves the last
/// used step forward only, UseRecovery removes one hash, ReplaceRecovery swaps the hashes, Disable deletes the row
/// (an Admin's reset with its audit row). True when the row changed.
/// </summary>
internal sealed class TotpWritePlan(TotpWriteKind kind, string account, long now, byte[]? secret = null, long step = 0,
    string recovery = "", string? admin = null) : DbPlan
{
    internal override int Bytes => 512 + Encoding.UTF8.GetByteCount(account + recovery);
    internal override string Label => "panel/totp-" + kind.ToString().ToLowerInvariant();
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var write = DbSql.Command(db, tx, kind switch
        {
            TotpWriteKind.Begin => "INSERT INTO account_totp(account,secret,enabled,last_step,recovery,created_utc) VALUES($account,$secret,0,0,'',$now) " +
                "ON CONFLICT(account) DO UPDATE SET secret=$secret, created_utc=$now WHERE enabled=0",
            TotpWriteKind.Enable => "UPDATE account_totp SET enabled=1, last_step=$step, recovery=$recovery WHERE account=$account AND enabled=0",
            TotpWriteKind.Use => "UPDATE account_totp SET last_step=$step WHERE account=$account AND enabled=1 AND last_step<$step",
            TotpWriteKind.UseRecovery => "UPDATE account_totp SET recovery=TRIM(REPLACE(','||recovery||',',','||$recovery||',',','),',') " +
                "WHERE account=$account AND enabled=1 AND instr(','||recovery||',',','||$recovery||',')>0",
            TotpWriteKind.ReplaceRecovery => "UPDATE account_totp SET recovery=$recovery WHERE account=$account AND enabled=1",
            _ => "DELETE FROM account_totp WHERE account=$account",
        });
        write.Parameters.AddWithValue("$account", account);
        write.Parameters.AddWithValue("$secret", secret ?? []);
        write.Parameters.AddWithValue("$now", now);
        write.Parameters.AddWithValue("$step", step);
        write.Parameters.AddWithValue("$recovery", recovery);
        bool changed = write.ExecuteNonQuery() == 1;
        if (changed && kind == TotpWriteKind.Disable && admin != null)
            AdminAudit.Insert(db, tx, now, admin, AdminAudit.TwoFactorReset, account, "two-factor authentication turned off");
        return changed;
    }
}
