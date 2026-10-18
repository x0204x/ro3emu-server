using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal static class AccountHash
{
    internal static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
}

/// <summary>Credentials of one account; <see cref="Name"/> is the stored (canonical) spelling of its name.</summary>
internal sealed record AccountCredentials(string Name, byte[] Hash, byte[] Salt);

/// <summary>
/// One account per name regardless of ASCII letter case: "Admin" finds "admin". The stored spelling is the identity
/// every session, token and permission check uses. A legacy database holding two spellings keeps both: the exact
/// spelling wins (else the older account), so neither account can reach the other.
/// </summary>
internal sealed class AccountCredentialsRead(string name) : DbReadPlan
{
    internal override int Bytes => 128 + Encoding.UTF8.GetByteCount(name);
    internal override string Label => "account/credentials";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx,
            "SELECT name,password_hash,salt FROM account WHERE name=$name COLLATE NOCASE ORDER BY name=$name DESC, rowid LIMIT 1");
        select.Parameters.AddWithValue("$name", name);
        using var row = select.ExecuteReader();
        return row.Read()
            ? new AccountCredentials(row.GetString(0), (byte[])((byte[])row[1]).Clone(), (byte[])((byte[])row[2]).Clone())
            : null;
    }
}

internal sealed class AccountRegisterPlan : DbPlan
{
    private readonly string _name, _createdUtc;
    private readonly byte[] _hash, _salt;
    internal AccountRegisterPlan(string name, byte[] hash, byte[] salt, string createdUtc)
    {
        _name = name; _createdUtc = createdUtc;
        _hash = (byte[])hash.Clone(); _salt = (byte[])salt.Clone();
    }
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(_name) + _hash.Length + _salt.Length;
    internal override string Label => "account/register";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        // A spelling that differs only in letter case is the same account, never a new one.
        using var insert = DbSql.Command(db, tx, "INSERT OR IGNORE INTO account(name,password_hash,salt,created_utc) " +
            "SELECT $name,$hash,$salt,$now WHERE NOT EXISTS (SELECT 1 FROM account WHERE name=$name COLLATE NOCASE)");
        insert.Parameters.AddWithValue("$name", _name);
        insert.Parameters.AddWithValue("$hash", _hash);
        insert.Parameters.AddWithValue("$salt", _salt);
        insert.Parameters.AddWithValue("$now", _createdUtc);
        return insert.ExecuteNonQuery() == 1;
    }
}

internal sealed class AccountTokenPlan(string kind, string account, string token, long now, long expires) : DbPlan
{
    internal override int Bytes => 192 + Encoding.UTF8.GetByteCount(account) + token.Length;
    internal override string Label => "auth_token/issue";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = DbSql.Command(db, tx, "DELETE FROM auth_token WHERE expires_utc < $now; INSERT INTO auth_token(token,kind,account,expires_utc) VALUES($token,$kind,$account,$expires)");
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$expires", expires);
        command.ExecuteNonQuery();
        return token;
    }
}

internal sealed class AccountTokenRead(string kind, string? token, long now) : DbReadPlan
{
    internal override int Bytes => 128 + (token?.Length ?? 0);
    internal override string Label => "auth_token/resolve";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var cmd = DbSql.Command(db, tx, "SELECT account FROM auth_token WHERE token=$token AND kind=$kind AND expires_utc >= $now");
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteScalar() as string;
    }
}
