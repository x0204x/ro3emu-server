using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

// ---------------------------------------------------------------- report inbox

internal sealed record FeedbackTriageRow(long ReportId, string Status, string? Assignee, long UpdatedUtc, string UpdatedBy, int Notes);

/// <summary>Every report's triage with its note count, by report id (reports without a row are new).</summary>
internal sealed class FeedbackTriageRead : DbReadPlan
{
    internal override int Bytes => 8192;
    internal override string Label => "panel/report-triage";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT t.report_id,t.status,t.assignee,t.updated_utc,t.updated_by," +
            "(SELECT COUNT(*) FROM feedback_note n WHERE n.report_id=t.report_id) FROM feedback_triage t");
        using var rows = select.ExecuteReader();
        var map = new Dictionary<long, FeedbackTriageRow>();
        while (rows.Read())
            map[rows.GetInt64(0)] = new FeedbackTriageRow(rows.GetInt64(0), rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2),
                rows.GetInt64(3), rows.GetString(4), rows.GetInt32(5));
        return map;
    }
}

internal sealed record FeedbackNoteRow(long Utc, string Author, string Text);

internal sealed class FeedbackNotesRead(long report) : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "panel/report-notes";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT utc,author,text FROM feedback_note WHERE report_id=$report ORDER BY id");
        select.Parameters.AddWithValue("$report", report);
        using var rows = select.ExecuteReader();
        var list = new List<FeedbackNoteRow>();
        while (rows.Read()) list.Add(new FeedbackNoteRow(rows.GetInt64(0), rows.GetString(1), rows.GetString(2)));
        return list;
    }
}

/// <summary>
/// A report's new status or assignee (null keeps it, an empty assignee clears it) and a note, with one audit row.
/// <paramref name="currentStatus"/> is the status a report without a triage row has.
/// </summary>
internal sealed class FeedbackTriageWritePlan(long report, string? status, string? assignee, string? note, string admin, long now,
    string currentStatus) : DbPlan
{
    internal override int Bytes => 512 + Encoding.UTF8.GetByteCount((note ?? "") + admin);
    internal override string Label => "panel/report-triage-write";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var write = DbSql.Command(db, tx, "INSERT INTO feedback_triage(report_id,status,assignee,updated_utc,updated_by) " +
            "VALUES($report,COALESCE($status,$current),NULLIF($assignee,''),$now,$admin) ON CONFLICT(report_id) DO UPDATE SET " +
            "status=COALESCE($status,status), assignee=CASE WHEN $assignee IS NULL THEN assignee ELSE NULLIF($assignee,'') END, " +
            "updated_utc=$now, updated_by=$admin");
        write.Parameters.AddWithValue("$report", report);
        write.Parameters.AddWithValue("$status", status is null ? DBNull.Value : status);
        write.Parameters.AddWithValue("$current", currentStatus);
        write.Parameters.AddWithValue("$assignee", assignee is null ? DBNull.Value : assignee);
        write.Parameters.AddWithValue("$now", now);
        write.Parameters.AddWithValue("$admin", admin);
        write.ExecuteNonQuery();
        if (note != null)
        {
            using var add = DbSql.Command(db, tx, "INSERT INTO feedback_note(report_id,utc,author,text) VALUES($report,$now,$admin,$text)");
            add.Parameters.AddWithValue("$report", report);
            add.Parameters.AddWithValue("$now", now);
            add.Parameters.AddWithValue("$admin", admin);
            add.Parameters.AddWithValue("$text", note);
            add.ExecuteNonQuery();
        }
        var detail = new List<string>();
        if (status != null) detail.Add("status " + status);
        if (assignee != null) detail.Add(assignee.Length == 0 ? "unassigned" : "assigned to " + assignee);
        if (note != null) detail.Add("note: " + (note.Length > 120 ? note[..120] + "..." : note));
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.Report, "report:" + report.ToString(CultureInfo.InvariantCulture), string.Join(", ", detail));
        return true;
    }
}

// ---------------------------------------------------------------- characters

internal sealed record DeletedRoleRow(long Id, string Name, int Career, int Level, long DeletedTime, bool NameFree);

/// <summary>
/// The account's deleted characters (RoleIdentityWriteKind.Delete keeps the row, renamed "name~id"), with their name
/// before the deletion and whether a living character took it since.
/// </summary>
internal sealed class DeletedRolesRead(string account) : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "panel/deleted-roles";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var select = DbSql.Command(db, tx, "SELECT id,name,career,level,deleted_time FROM role " +
            "WHERE account=$account AND deleted_time>0 ORDER BY deleted_time DESC, id DESC LIMIT 100");
        select.Parameters.AddWithValue("$account", account);
        var list = new List<DeletedRoleRow>();
        using (var rows = select.ExecuteReader())
            while (rows.Read())
                list.Add(new DeletedRoleRow(rows.GetInt64(0), PanelRoleNames.Original(rows.GetString(1), rows.GetInt64(0)), rows.GetInt32(2),
                    rows.GetInt32(3), rows.GetInt64(4), false));
        return list.Select(r => r with { NameFree = !PanelRoleNames.Taken(db, tx, r.Name, r.Id) }).ToList();
    }
}

internal static class PanelRoleNames
{
    /// <summary>The name a deleted role had: the delete appended "~id".</summary>
    internal static string Original(string stored, long id)
    {
        string suffix = "~" + id.ToString(CultureInfo.InvariantCulture);
        return stored.EndsWith(suffix, StringComparison.Ordinal) ? stored[..^suffix.Length] : stored;
    }

    /// <summary>Another role holds <paramref name="name"/> in any letter case (role.name is UNIQUE COLLATE NOCASE).</summary>
    internal static bool Taken(SqliteConnection db, SqliteTransaction tx, string name, long except)
    {
        using var taken = DbSql.Command(db, tx, "SELECT 1 FROM role WHERE name=$name COLLATE NOCASE AND id<>$id");
        taken.Parameters.AddWithValue("$name", name);
        taken.Parameters.AddWithValue("$id", except);
        return taken.ExecuteScalar() != null;
    }
}

internal enum PanelRoleResult { Done, Missing, NameTaken }

/// <summary>
/// Renames a living role with its audit row. The unique name index decides a race with a role creation; the old
/// name is free the moment this commits. Declares the identity part, so cached identities of the role are refreshed.
/// </summary>
internal sealed class PanelRoleRenamePlan(long role, string name, string admin, long now) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(name + admin);
    internal override string Label => "panel/role-rename";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.Identity)];
    internal override IReadOnlyList<RoleRevisionKey> MutatedRoleWrites(object? result) => result is PanelRoleResult.Done ? RoleWrites : [];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var old = DbSql.Command(db, tx, "SELECT name FROM role WHERE id=$id AND deleted_time=0");
        old.Parameters.AddWithValue("$id", role);
        if (old.ExecuteScalar() is not string previous) return PanelRoleResult.Missing;
        if (PanelRoleNames.Taken(db, tx, name, role)) return PanelRoleResult.NameTaken;
        using var rename = DbSql.Command(db, tx, "UPDATE role SET name=$name WHERE id=$id AND deleted_time=0");
        rename.Parameters.AddWithValue("$name", name);
        rename.Parameters.AddWithValue("$id", role);
        rename.ExecuteNonQuery();
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.CharacterRename, AdminAudit.Character(name, role),
            previous + " => " + name);
        return PanelRoleResult.Done;
    }
}

/// <summary>
/// Brings a deleted role back under its old name (refused while another role holds it): deleted_time 0 and a new
/// lifecycle epoch, so no owner state of the deleted life applies to it. The row is not versioned while deleted.
/// </summary>
internal sealed class PanelRoleRestorePlan(long role, string admin, long now) : DbPlan
{
    internal override int Bytes => 256 + Encoding.UTF8.GetByteCount(admin);
    internal override string Label => "panel/role-restore";
    internal override bool AllowsUnversionedRoleMutation => true;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var find = DbSql.Command(db, tx, "SELECT name,account FROM role WHERE id=$id AND deleted_time>0");
        find.Parameters.AddWithValue("$id", role);
        string stored, account;
        using (var row = find.ExecuteReader())
        {
            if (!row.Read()) return PanelRoleResult.Missing;
            (stored, account) = (row.GetString(0), row.GetString(1));
        }
        string name = PanelRoleNames.Original(stored, role);
        if (PanelRoleNames.Taken(db, tx, name, role)) return PanelRoleResult.NameTaken;
        using var restore = DbSql.Command(db, tx, "UPDATE role SET deleted_time=0, name=$name WHERE id=$id AND deleted_time>0");
        restore.Parameters.AddWithValue("$name", name);
        restore.Parameters.AddWithValue("$id", role);
        restore.ExecuteNonQuery();
        using var lifecycle = DbSql.Command(db, tx, "UPDATE role_lifecycle SET epoch=epoch+1, deleted=0 WHERE role_id=$id AND deleted=1");
        lifecycle.Parameters.AddWithValue("$id", role);
        if (lifecycle.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        AdminAudit.Insert(db, tx, now, admin, AdminAudit.CharacterRestore, AdminAudit.Character(name, role),
            name + " of " + account);
        return PanelRoleResult.Done;
    }
}
