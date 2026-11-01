using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// Server control (Admins, audited by the server): a scheduled restart, shutdown or backup restore with countdown
/// notices in the game, maintenance mode, and the database backups (list, make one now, download). A restore never
/// touches the running database: it is a restart whose next start puts the backup back first.
/// </summary>
public sealed partial class PanelSite
{
    private static readonly string[] ScheduleKinds = ["restart", "shutdown", "restore"];
    /// <summary>The longest countdown a schedule takes: one day.</summary>
    private const int ScheduleMinutesMax = 1440;

    /// <summary>The names DatabaseBackup gives its copies (ro3emu-yyyyMMdd-HHmmss[-n].db, .db.gz).</summary>
    [GeneratedRegex(@"^ro3emu-[0-9A-Za-z_\-]{1,64}\.db(\.gz)?$")]
    private static partial Regex BackupName();

    internal sealed record ScheduleForm(string? Kind, int Minutes, string? Reason, string? Backup);
    internal sealed record MaintenanceForm(bool On, string? Note);

    private IEnumerable<Route> ServerRoutes() =>
    [
        Get("/api/admin/server", Access.Admin, async _ => Reply.Ok(await _ops.ServerStateAsync())),
        Post("/api/admin/server/schedule", Access.Admin, ScheduleAsync),
        Post("/api/admin/server/cancel", Access.Admin, async call => await _ops.CancelScheduleAsync(call.Signed)
            ? Reply.Ok(new { cancelled = true }) : Reply.Error(StatusCodes.Status404NotFound, "Nothing is scheduled.")),
        Post("/api/admin/server/maintenance", Access.Admin, MaintenanceAsync),
        Post("/api/admin/backups", Access.Admin, async call => Reply.Ok(new { name = await _ops.BackupNowAsync(call.Signed) })),
        Get("/api/admin/backups/{}", Access.Admin, DownloadBackupAsync),
    ];

    private async Task<Reply> ScheduleAsync(Call call)
    {
        var form = await BodyAsync<ScheduleForm>(call);
        string kind = form?.Kind ?? "", reason = form?.Reason?.Trim() ?? "";
        if (!ScheduleKinds.Contains(kind)) return Bad("Choose restart, shutdown or restore.");
        if (form!.Minutes is < 0 or > ScheduleMinutesMax) return Bad("Start the countdown 0 to 1440 minutes from now.");
        if (reason.Length > BroadcastMax || reason.Any(char.IsControl)) return Bad("Give a reason of at most 200 characters on one line.");
        string? backup = kind == "restore" ? form.Backup : null;
        if (kind == "restore" && (backup == null || !BackupName().IsMatch(backup))) return Bad("Choose the backup to restore.");
        var result = await _ops.ScheduleAsync(call.Signed, new PanelSchedule(kind, form.Minutes, reason, backup));
        return result.Done ? Reply.Ok(result) : Reply.Error(StatusCodes.Status409Conflict, result.Message);
    }

    private async Task<Reply> MaintenanceAsync(Call call)
    {
        var form = await BodyAsync<MaintenanceForm>(call);
        string note = form?.Note?.Trim() ?? "";
        if (form == null || note.Length > ReasonMax) return Bad("Give a note of at most 300 characters.");
        await _ops.SetMaintenanceAsync(call.Signed, form.On, note);
        return Reply.Ok(new { on = form.On });
    }

    /// <summary>A backup file as a download; only the names the backup writes, from the backup folder.</summary>
    private async Task<Reply> DownloadBackupAsync(Call call)
    {
        string name = call.Args[0];
        if (!BackupName().IsMatch(name) || await _ops.BackupFileAsync(call.Signed, name) is not { } file || !File.Exists(file))
            return Reply.Error(StatusCodes.Status404NotFound, "There is no such backup.");
        var response = call.Context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/octet-stream";
        response.Headers.ContentDisposition = "attachment; filename=\"" + name + "\"";
        response.ContentLength = new FileInfo(file).Length;
        await response.SendFileAsync(file, call.Context.RequestAborted);
        return Reply.Sent;
    }
}
