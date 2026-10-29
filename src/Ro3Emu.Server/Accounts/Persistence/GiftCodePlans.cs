using System.Text;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Gift codes (docs/systems/gift-codes.md). The admin panel writes gift_code rows into this database from its own
/// process; the server only reads them, fresh on every redemption, and never writes them. gift_code_redemption is the
/// server's: one row per granted use, keyed by the same delivery key as the gift mail, written in the mail's
/// transaction. Uses are counted from these rows, never from a counter in gift_code. The whole feature's rules are
/// (inferred): the client only posts the code and shows the server's answer.
/// </summary>
internal sealed class GiftCodeSchemaPlan : DbPlan
{
    internal override int Bytes => 512;
    internal override string Label => "gift-code/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS gift_code (
                code TEXT NOT NULL PRIMARY KEY COLLATE NOCASE
                    CHECK(length(code) BETWEEN 1 AND 64 AND code = trim(code)),
                items TEXT NOT NULL,
                max_uses INTEGER NOT NULL DEFAULT 0 CHECK(max_uses >= 0),
                per_account INTEGER NOT NULL DEFAULT 1 CHECK(per_account >= 1),
                starts_utc INTEGER, expires_utc INTEGER,
                disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1)),
                created_by TEXT,
                created_utc INTEGER NOT NULL DEFAULT (CAST(strftime('%s','now') AS INTEGER)),
                note TEXT);
            CREATE TABLE IF NOT EXISTS gift_code_redemption (
                redemption_key TEXT PRIMARY KEY, code TEXT NOT NULL, account TEXT NOT NULL,
                role_id INTEGER NOT NULL, redeemed_utc INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS gift_code_redemption_code ON gift_code_redemption(code,account);
            """);
        return 1;
    }
}

/// <summary>A gift_code row as stored, with the uses counted from gift_code_redemption.</summary>
public sealed record GiftCodeState(string Code, string Items, long MaxUses, long PerAccount, long? StartsUtc,
    long? ExpiresUtc, bool Disabled, long TotalUses, long AccountUses, bool Malformed = false)
{
    /// <summary>
    /// The first rule the code breaks for one more use by this account at <paramref name="now"/> (unix seconds), or
    /// null. Order: a number column holding something else (malformed, refused rather than read as 0), disabled, not started, expired, total uses, uses of this account. (inferred) starts_utc is the
    /// first second a code works, expires_utc the first second it no longer does; max_uses 0 means unlimited.
    /// </summary>
    public int? Refusal(long now) =>
        Malformed ? ErrorCodes.GiftCardInvalidItems
        : Disabled ? ErrorCodes.GiftCardDisabled
        : StartsUtc is { } starts && now < starts ? ErrorCodes.GiftCardNotStarted
        : ExpiresUtc is { } expires && now >= expires ? ErrorCodes.GiftCardExpired
        : MaxUses > 0 && TotalUses >= MaxUses ? ErrorCodes.GiftCardExhausted
        : AccountUses >= PerAccount ? ErrorCodes.GiftCardAlreadyRedeemed
        : null;

    /// <summary>
    /// The delivery key of this account's next use, which is also its redemption_key: giftcode:&lt;code length&gt;:&lt;code&gt;:&lt;use number&gt;:&lt;account&gt;. The code is
    /// length-delimited and the account comes last, so a ':' in a code or an account name cannot make two keys equal.
    /// </summary>
    public string NextKey(string account) => $"giftcode:{Code.Length}:{Code}:{AccountUses + 1}:{account}";

    internal static GiftCodeState? Read(SqliteConnection db, SqliteTransaction? tx, string code, string account)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT g.code, g.items, g.max_uses, g.per_account, g.starts_utc, g.expires_utc, g.disabled,
                (SELECT COUNT(*) FROM gift_code_redemption r WHERE r.code = g.code),
                (SELECT COUNT(*) FROM gift_code_redemption r WHERE r.code = g.code AND r.account = $account),
                typeof(g.max_uses) || typeof(g.per_account) || typeof(g.disabled) = 'integerintegerinteger'
                    AND typeof(g.starts_utc) IN ('integer','null') AND typeof(g.expires_utc) IN ('integer','null')
            FROM gift_code g WHERE g.code = $code
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$account", account);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetInt64(6) != 0, reader.GetInt64(7), reader.GetInt64(8), Malformed: reader.GetInt64(9) == 0);
    }
}

/// <summary>Reads one gift code (matched case-insensitively) and its use counts for an account, uncached.</summary>
internal sealed class GiftCodeRead(string code, string account) : DbReadPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(code) + Encoding.UTF8.GetByteCount(account);
    internal override string Label => "gift-code/read";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx) => GiftCodeState.Read(db, tx, code, account);
}

/// <summary>
/// Records one redemption inside the gift mail's transaction. The code is read again there: if the panel disabled
/// it, or another redemption took the last use since the caller checked, the plan throws
/// <see cref="GiftCodeRefusedException"/> and the mail is rolled back with it.
/// </summary>
internal sealed class GiftCodeRedeemPlan(string redemptionKey, string code, string account, long roleId, long redeemedUtc)
    : DbPlan
{
    internal override int Bytes => 192 + Encoding.UTF8.GetByteCount(redemptionKey) + Encoding.UTF8.GetByteCount(account);
    internal override string Label => "gift-code/redeem";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var state = GiftCodeState.Read(db, tx, code, account);
        int? refusal = state == null ? ErrorCodes.GiftCardUnknown : state.Refusal(redeemedUtc);
        if (refusal == null && state!.NextKey(account) != redemptionKey) refusal = ErrorCodes.GiftCardAlreadyRedeemed;
        if (refusal is { } refused) throw new GiftCodeRefusedException(refused);
        using var insert = DbSql.Command(db, tx, """
            INSERT INTO gift_code_redemption(redemption_key,code,account,role_id,redeemed_utc)
            VALUES($key,$code,$account,$role,$now)
            """);
        insert.Parameters.AddWithValue("$key", redemptionKey);
        insert.Parameters.AddWithValue("$code", state!.Code);
        insert.Parameters.AddWithValue("$account", account);
        insert.Parameters.AddWithValue("$role", roleId);
        insert.Parameters.AddWithValue("$now", redeemedUtc);
        insert.ExecuteNonQuery();
        return 1;
    }
}

/// <summary>A redemption refused inside its transaction; <see cref="Code"/> is the ErrorCodes.GiftCard* answer.</summary>
public sealed class GiftCodeRefusedException(int code) : Exception($"Gift code redemption refused ({code}).")
{
    public int Code { get; } = code;
}
