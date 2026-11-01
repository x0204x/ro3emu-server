using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// The report inbox (staff): the players' in-game reports (/bug and the Feedback button, FeedbackService) with a status,
/// an assignee and notes. The reports themselves stay as the game stored them; the triage is the server's, audited.
/// </summary>
public sealed partial class PanelSite
{
    private const int NoteMax = 1000;
    internal static readonly string[] ReportStatuses = ["new", "in-progress", "done", "wont-fix"];

    internal sealed record ReportForm(string? Status, string? Assignee, string? Note);

    private IEnumerable<Route> ReportRoutes() =>
    [
        Get("/api/admin/reports", Access.Moderator, ReportsAsync),
        Get("/api/admin/reports/{}", Access.Moderator, ReportAsync),
        Post("/api/admin/reports/{}", Access.Moderator, UpdateReportAsync),
    ];

    private async Task<Reply> ReportsAsync(Call call)
    {
        string status = call.Query("status");
        if (status.Length > 0 && !ReportStatuses.Contains(status)) status = "";
        return Reply.Ok(await _ops.ReportsAsync(status, call.Page(), PageSize));
    }

    private async Task<Reply> ReportAsync(Call call) =>
        long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) && await _ops.ReportAsync(id) is { } report
            ? Reply.Ok(report) : Reply.Error(StatusCodes.Status404NotFound, "There is no such report.");

    private async Task<Reply> UpdateReportAsync(Call call)
    {
        if (!long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            return Reply.Error(StatusCodes.Status404NotFound, "There is no such report.");
        var form = await BodyAsync<ReportForm>(call);
        string? status = string.IsNullOrEmpty(form?.Status) ? null : form.Status;
        string? assignee = form?.Assignee?.Trim();
        string? note = form?.Note?.Trim() is { Length: > 0 } n ? n : null;
        if (status != null && !ReportStatuses.Contains(status)) return Bad("The status is new, in-progress, done or wont-fix.");
        if (assignee is { Length: > NameMax } || assignee != null && assignee.Any(char.IsControl)) return Bad("Give an account name of at most 64 characters.");
        if (note is { Length: > NoteMax }) return Bad("A note has at most 1000 characters.");
        if (status == null && assignee == null && note == null) return Bad("Nothing to change.");
        if (assignee is { Length: > 0 })
        {
            if (await _backend.AccountAsync(assignee) is not { } account) return Bad("There is no such account to assign it to.");
            assignee = account.Name;
        }
        return await _ops.UpdateReportAsync(call.Signed, id, new PanelReportUpdate(status, assignee, note))
            ? Reply.Ok(new { id }) : Reply.Error(StatusCodes.Status404NotFound, "There is no such report.");
    }
}
