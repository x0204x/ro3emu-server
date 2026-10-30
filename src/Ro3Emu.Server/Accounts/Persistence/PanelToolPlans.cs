using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One admin action recorded on its own (a GM command, a mail run, a broadcast): its work is elsewhere.</summary>
internal sealed class AdminAuditWritePlan(long now, string admin, string action, string target, string detail) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(admin + action + target + detail);
    internal override string Label => "panel/audit-write";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        AdminAudit.Insert(db, tx, now, admin, action, target, detail);
        return true;
    }
}

/// <summary>
/// Writes one panel_news row (<paramref name="id"/> 0 inserts) with its audit row. The new or updated id, or 0 when the
/// row to update does not exist.
/// </summary>
internal sealed class PanelNewsSavePlan(long id, string title, string body, string tag, long published, bool pinned,
    string admin, long now) : DbPlan
{
    internal override int Bytes => 512 + Encoding.UTF8.GetByteCount(title + body + admin);
    internal override string Label => "panel/news-save";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var write = DbSql.Command(db, tx, id == 0
            ? "INSERT INTO panel_news(title,body,tag,published_utc,author,pinned) VALUES($title,$body,$tag,$published,$admin,$pinned)"
            : "UPDATE panel_news SET title=$title, body=$body, tag=$tag, published_utc=$published, pinned=$pinned WHERE id=$id");
        write.Parameters.AddWithValue("$id", id);
        write.Parameters.AddWithValue("$title", title);
        write.Parameters.AddWithValue("$body", body);
        write.Parameters.AddWithValue("$tag", tag);
        write.Parameters.AddWithValue("$published", published);
        write.Parameters.AddWithValue("$admin", admin);
        write.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        if (write.ExecuteNonQuery() != 1) return 0L;
        long saved = id;
        if (id == 0)
        {
            using var last = DbSql.Command(db, tx, "SELECT last_insert_rowid()");
            saved = (long)last.ExecuteScalar()!;
        }
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.NewsSave, "news:" + saved.ToString(CultureInfo.InvariantCulture), title);
        return saved;
    }
}

/// <summary>Deletes one panel_news row with its audit row; false when it did not exist.</summary>
internal sealed class PanelNewsDeletePlan(long id, string admin, long now) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(admin);
    internal override string Label => "panel/news-delete";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var title = DbSql.Command(db, tx, "SELECT title FROM panel_news WHERE id=$id");
        title.Parameters.AddWithValue("$id", id);
        if (title.ExecuteScalar() is not string text) return false;
        using var delete = DbSql.Command(db, tx, "DELETE FROM panel_news WHERE id=$id");
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.NewsDelete, "news:" + id.ToString(CultureInfo.InvariantCulture), text);
        return true;
    }
}

/// <summary>A gift_code row for the admin's list, with its redemptions counted.</summary>
internal sealed record GiftCodeRow(string Code, string Items, long MaxUses, long PerAccount, long? StartsUtc, long? ExpiresUtc,
    bool Disabled, string? CreatedBy, long CreatedUtc, string? Note, long Uses);

/// <summary>The newest gift codes, at most <paramref name="limit"/>; a typed column holding something else reads as 0.</summary>
internal sealed class GiftCodeListRead(int limit) : DbReadPlan
{
    internal override int Bytes => 128 + limit * 256;
    internal override string Label => "panel/gift-codes";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT g.code, g.items, CAST(g.max_uses AS INTEGER), CAST(g.per_account AS INTEGER), " +
            "CAST(g.starts_utc AS INTEGER), CAST(g.expires_utc AS INTEGER), CAST(g.disabled AS INTEGER), g.created_by, " +
            "CAST(g.created_utc AS INTEGER), g.note, (SELECT COUNT(*) FROM gift_code_redemption r WHERE r.code=g.code) " +
            "FROM gift_code g ORDER BY g.created_utc DESC, g.code LIMIT $limit");
        select.Parameters.AddWithValue("$limit", limit);
        using var rows = select.ExecuteReader();
        var codes = new List<GiftCodeRow>();
        while (rows.Read())
            codes.Add(new GiftCodeRow(rows.GetString(0), rows.IsDBNull(1) ? "" : rows.GetString(1), Number(rows, 2) ?? 0,
                Number(rows, 3) ?? 1, Number(rows, 4), Number(rows, 5), Number(rows, 6) == 1,
                rows.IsDBNull(7) ? null : rows.GetString(7), Number(rows, 8) ?? 0, rows.IsDBNull(9) ? null : rows.GetString(9),
                rows.GetInt64(10)));
        return codes;
    }

    private static long? Number(SqliteDataReader rows, int column) => rows.IsDBNull(column) ? null : rows.GetInt64(column);
}

/// <summary>
/// Creates gift codes, all or none: the codes already taken (in any letter case, as gift_code's collation compares)
/// are returned and nothing is written; otherwise every row and one audit row commit together.
/// </summary>
internal sealed class GiftCodeCreatePlan(IReadOnlyList<string> codes, string items, long maxUses, long perAccount, long? starts,
    long? expires, string? note, string admin, long now) : DbPlan
{
    internal override int Bytes => 512 + codes.Sum(c => c.Length + 64) + Encoding.UTF8.GetByteCount(items + note + admin);
    internal override string Label => "panel/gift-code-create";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var taken = new List<string>();
        foreach (string code in codes)
        {
            using var exists = DbSql.Command(db, tx, "SELECT code FROM gift_code WHERE code=$code");
            exists.Parameters.AddWithValue("$code", code);
            if (exists.ExecuteScalar() is string found) taken.Add(found);
        }
        if (taken.Count > 0) return taken.ToArray();
        foreach (string code in codes)
        {
            using var insert = DbSql.Command(db, tx, "INSERT INTO gift_code(code,items,max_uses,per_account,starts_utc,expires_utc," +
                "disabled,created_by,created_utc,note) VALUES($code,$items,$max,$per,$starts,$expires,0,$admin,$now,$note)");
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$items", items);
            insert.Parameters.AddWithValue("$max", maxUses);
            insert.Parameters.AddWithValue("$per", perAccount);
            insert.Parameters.AddWithValue("$starts", starts is { } s ? s : DBNull.Value);
            insert.Parameters.AddWithValue("$expires", expires is { } e ? e : DBNull.Value);
            insert.Parameters.AddWithValue("$admin", admin);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$note", note is { Length: > 0 } ? note : DBNull.Value);
            insert.ExecuteNonQuery();
        }
        string target = codes.Count == 1 ? codes[0] : codes.Count.ToString(CultureInfo.InvariantCulture) + " codes";
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.GiftCodeCreate, target,
            items + (codes.Count > 1 ? ": " + string.Join(", ", codes.Take(20)) + (codes.Count > 20 ? ", ..." : "") : ""));
        return Array.Empty<string>();
    }
}

/// <summary>Turns a gift code off or on again with its audit row; false when no such code exists.</summary>
internal sealed class GiftCodeDisablePlan(string code, bool disabled, string admin, long now) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(code + admin);
    internal override string Label => "panel/gift-code-disable";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var update = DbSql.Command(db, tx, "UPDATE gift_code SET disabled=$disabled WHERE code=$code");
        update.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
        update.Parameters.AddWithValue("$code", code);
        if (update.ExecuteNonQuery() != 1) return false;
        AdminAudit.Insert(db, tx, now, admin, disabled ? AdminAudit.GiftCodeDisable : AdminAudit.GiftCodeEnable, code, "");
        return true;
    }
}

/// <summary>Every live character at <paramref name="minLevel"/> or above: a mass mail's recipients.</summary>
internal sealed class PanelRecipientsRead(int minLevel) : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "panel/recipients";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT id,name,account FROM role WHERE deleted_time=0 AND level >= $level ORDER BY id");
        select.Parameters.AddWithValue("$level", minLevel);
        using var rows = select.ExecuteReader();
        var roles = new List<(long Id, string Name, string Account)>();
        while (rows.Read()) roles.Add((rows.GetInt64(0), rows.GetString(1), rows.GetString(2)));
        return roles;
    }
}
