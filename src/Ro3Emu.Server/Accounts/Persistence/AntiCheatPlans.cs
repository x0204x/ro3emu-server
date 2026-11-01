using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// The anti-cheat's tables (Game/CheatMonitor.cs, docs/systems/anticheat.md): anticheat_refusal, the requests the server
/// refused as forged or impossible, counted per account, character, kind and hour; anticheat_exp, the base and job EXP a
/// character gained per hour (EXP is no ledger asset). Plus an index on economy_entry's time, which the panel's gain
/// list reads by. Diagnostic only: nothing in the game reads them.
/// </summary>
internal sealed class AntiCheatSchemaPlan : DbPlan
{
    internal override int Bytes => 512;
    internal override string Label => "anticheat/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS anticheat_refusal (
                account TEXT NOT NULL,
                role_id INTEGER NOT NULL,
                kind TEXT NOT NULL,
                hour INTEGER NOT NULL,
                count INTEGER NOT NULL,
                last_utc INTEGER NOT NULL,
                last_detail TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(account, role_id, kind, hour));
            CREATE INDEX IF NOT EXISTS anticheat_refusal_hour ON anticheat_refusal(hour);
            CREATE TABLE IF NOT EXISTS anticheat_exp (
                role_id INTEGER NOT NULL,
                hour INTEGER NOT NULL,
                account TEXT NOT NULL,
                base_exp INTEGER NOT NULL,
                job_exp INTEGER NOT NULL,
                PRIMARY KEY(role_id, hour));
            CREATE INDEX IF NOT EXISTS anticheat_exp_hour ON anticheat_exp(hour);
            CREATE INDEX IF NOT EXISTS economy_entry_recorded ON economy_entry(recorded_utc_ms);
            """);
        return 1;
    }
}

/// <summary>One hour's refusals of one kind by one character (role 0 before a role is selected).</summary>
internal sealed record AntiCheatRefusalCount(string Account, long RoleId, string Kind, long Hour, long Count, long LastUtc, string LastDetail);

/// <summary>One hour's EXP of one character.</summary>
internal sealed record AntiCheatExpCount(long RoleId, long Hour, string Account, long BaseExp, long JobExp);

/// <summary>Adds the pending counts to their hour rows and drops rows older than <paramref name="keepFromHour"/>.</summary>
internal sealed class AntiCheatFlushPlan(IReadOnlyList<AntiCheatRefusalCount> refusals, IReadOnlyList<AntiCheatExpCount> exp,
    long keepFromHour) : DbPlan
{
    internal override int Bytes => 256 + refusals.Count * 192 + exp.Count * 64;
    internal override string Label => "anticheat/flush";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var upsert = DbSql.Command(db, tx, "INSERT INTO anticheat_refusal(account,role_id,kind,hour,count,last_utc,last_detail) " +
            "VALUES($account,$role,$kind,$hour,$count,$last,$detail) ON CONFLICT(account,role_id,kind,hour) DO UPDATE SET " +
            "count=count+excluded.count, last_utc=MAX(last_utc,excluded.last_utc), last_detail=excluded.last_detail"))
            foreach (var r in refusals)
            {
                upsert.Parameters.Clear();
                upsert.Parameters.AddWithValue("$account", r.Account);
                upsert.Parameters.AddWithValue("$role", r.RoleId);
                upsert.Parameters.AddWithValue("$kind", r.Kind);
                upsert.Parameters.AddWithValue("$hour", r.Hour);
                upsert.Parameters.AddWithValue("$count", r.Count);
                upsert.Parameters.AddWithValue("$last", r.LastUtc);
                upsert.Parameters.AddWithValue("$detail", r.LastDetail);
                upsert.ExecuteNonQuery();
            }
        using (var upsert = DbSql.Command(db, tx, "INSERT INTO anticheat_exp(role_id,hour,account,base_exp,job_exp) " +
            "VALUES($role,$hour,$account,$base,$job) ON CONFLICT(role_id,hour) DO UPDATE SET " +
            "base_exp=base_exp+excluded.base_exp, job_exp=job_exp+excluded.job_exp"))
            foreach (var e in exp)
            {
                upsert.Parameters.Clear();
                upsert.Parameters.AddWithValue("$role", e.RoleId);
                upsert.Parameters.AddWithValue("$hour", e.Hour);
                upsert.Parameters.AddWithValue("$account", e.Account);
                upsert.Parameters.AddWithValue("$base", e.BaseExp);
                upsert.Parameters.AddWithValue("$job", e.JobExp);
                upsert.ExecuteNonQuery();
            }
        foreach (string table in new[] { "anticheat_refusal", "anticheat_exp" })
        {
            using var prune = DbSql.Command(db, tx, $"DELETE FROM {table} WHERE hour < $keep");
            prune.Parameters.AddWithValue("$keep", keepFromHour);
            prune.ExecuteNonQuery();
        }
        return refusals.Count + exp.Count;
    }
}

/// <summary>An account's refusals since an hour: per character and kind, with the character's name.</summary>
internal sealed record AntiCheatRefusalRow(string Account, long RoleId, string RoleName, string Kind, long Count, long LastUtc, string LastDetail);

/// <summary>The refusals of every account since <paramref name="fromHour"/>, or of one account (any letter case).</summary>
internal sealed class AntiCheatRefusalsRead(long fromHour, string? account = null) : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "anticheat/refusals";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT a.account,a.role_id,COALESCE(r.name,''),a.kind,SUM(a.count),MAX(a.last_utc)," +
            "(SELECT b.last_detail FROM anticheat_refusal b WHERE b.account=a.account AND b.role_id=a.role_id AND b.kind=a.kind " +
            "AND b.hour>=$from ORDER BY b.last_utc DESC LIMIT 1) " +
            "FROM anticheat_refusal a LEFT JOIN role r ON r.id=a.role_id WHERE a.hour>=$from" +
            (account == null ? "" : " AND a.account=$account COLLATE NOCASE") +
            " GROUP BY a.account,a.role_id,a.kind ORDER BY a.account,a.role_id,a.kind");
        select.Parameters.AddWithValue("$from", fromHour);
        if (account != null) select.Parameters.AddWithValue("$account", account);
        using var rows = select.ExecuteReader();
        var result = new List<AntiCheatRefusalRow>();
        while (rows.Read())
            result.Add(new AntiCheatRefusalRow(rows.GetString(0), rows.GetInt64(1), rows.GetString(2), rows.GetString(3), rows.GetInt64(4),
                rows.GetInt64(5), rows.IsDBNull(6) ? "" : rows.GetString(6)));
        return result;
    }
}

/// <summary>What one character gained from the game in a window: Zeny and items from the ledger's source legs, base EXP
/// from anticheat_exp.</summary>
internal sealed record AntiCheatGainRow(long RoleId, string RoleName, string Account, long Zeny, long Items, long BaseExp);

/// <summary>
/// The gains of every character in the window: economy_entry source legs (leg_kind 1) of role holdings since
/// <paramref name="fromMs"/>, Zeny being currency 1 and items every item asset counted together, and anticheat_exp since
/// <paramref name="fromHour"/>. A database without the ledger reads EXP only.
/// </summary>
internal sealed class AntiCheatGainsRead(long fromMs, long fromHour, long zenyId) : DbReadPlan
{
    internal override int Bytes => 8192;
    internal override string Label => "anticheat/gains";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var gains = new Dictionary<long, (long Zeny, long Items, long Exp)>();
        bool ledger;
        using (var exists = DbSql.Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='economy_entry'"))
            ledger = (long)exists.ExecuteScalar()! > 0;
        if (ledger)
        {
            using var select = DbSql.Command(db, tx, "SELECT owner_id, " +
                "TOTAL(CASE WHEN asset_kind=2 AND asset_id=$zeny THEN delta END), TOTAL(CASE WHEN asset_kind=1 THEN delta END) " +
                "FROM economy_entry WHERE recorded_utc_ms>=$from AND owner_kind=1 AND leg_kind=1 AND delta>0 GROUP BY owner_id");
            select.Parameters.AddWithValue("$from", fromMs);
            select.Parameters.AddWithValue("$zeny", zenyId);
            using var rows = select.ExecuteReader();
            while (rows.Read())
                if (long.TryParse(rows.GetString(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out long role))
                    gains[role] = (Clamp(rows.GetDouble(1)), Clamp(rows.GetDouble(2)), 0);
        }
        using (var select = DbSql.Command(db, tx, "SELECT role_id, SUM(base_exp) FROM anticheat_exp WHERE hour>=$hour GROUP BY role_id"))
        {
            select.Parameters.AddWithValue("$hour", fromHour);
            using var rows = select.ExecuteReader();
            while (rows.Read())
            {
                long role = rows.GetInt64(0);
                var g = gains.GetValueOrDefault(role);
                gains[role] = (g.Zeny, g.Items, rows.GetInt64(1));
            }
        }
        var names = new Dictionary<long, (string Name, string Account)>();
        foreach (var chunk in gains.Keys.Chunk(500))
        {
            using var select = DbSql.Command(db, tx, "SELECT id,name,account FROM role WHERE id IN (" +
                string.Join(',', chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture))) + ")");
            for (int i = 0; i < chunk.Length; i++) select.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
            using var rows = select.ExecuteReader();
            while (rows.Read()) names[rows.GetInt64(0)] = (rows.GetString(1), rows.GetString(2));
        }
        return gains.Select(p => new AntiCheatGainRow(p.Key, names.GetValueOrDefault(p.Key).Name ?? "",
            names.GetValueOrDefault(p.Key).Account ?? "", p.Value.Zeny, p.Value.Items, p.Value.Exp)).ToList();
    }

    private static long Clamp(double sum) => sum >= long.MaxValue ? long.MaxValue : sum <= 0 ? 0 : (long)Math.Round(sum);
}
