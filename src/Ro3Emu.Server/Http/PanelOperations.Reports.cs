using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The report inbox: the reports are FeedbackService's own file (DataDir/reports/feedback.jsonl, read off the loop),
/// their triage (status, assignee, notes) the database's, written with an audit row. A report without triage is new.
/// </summary>
public sealed partial class PanelOperations
{
    private const string NewStatus = "new";
    private const int SummaryText = 200;

    private Task<Dictionary<long, FeedbackTriageRow>> TriageAsync() =>
        accounts.ReadAsync<Dictionary<long, FeedbackTriageRow>>(new FeedbackTriageRead());

    private static string StatusOf(Dictionary<long, FeedbackTriageRow> triage, long id) =>
        triage.TryGetValue(id, out var row) ? row.Status : NewStatus;

    public async Task<PanelReportPage> ReportsAsync(string status, int page, int pageSize)
    {
        var reports = await Task.Run(() => feedback.Store.Read());
        var triage = await TriageAsync();
        var counts = reports.GroupBy(r => StatusOf(triage, r.Id)).ToDictionary(g => g.Key, g => g.Count());
        var matching = reports.Where(r => status.Length == 0 || StatusOf(triage, r.Id) == status).OrderByDescending(r => r.Id).ToList();
        var rows = matching.Skip((page - 1) * pageSize).Take(pageSize).Select(r =>
        {
            triage.TryGetValue(r.Id, out var t);
            string text = r.Text.Length > SummaryText ? r.Text[..SummaryText] + "..." : r.Text;
            return new PanelReportSummary(r.Id, r.Time, r.Source, r.Account, r.RoleName, r.Level, r.Map, text,
                t?.Status ?? NewStatus, t?.Assignee, t?.Notes ?? 0);
        });
        return new PanelReportPage([.. rows], matching.Count, page, pageSize, counts);
    }

    public async Task<PanelReport?> ReportAsync(long id)
    {
        var report = await Task.Run(() => feedback.Store.Read(id - 1, null, 1).FirstOrDefault(r => r.Id == id));
        if (report == null) return null;
        (await TriageAsync()).TryGetValue(id, out var t);
        var notes = await accounts.ReadAsync<List<FeedbackNoteRow>>(new FeedbackNotesRead(id));
        return new PanelReport(report.Id, report.Time, report.Source, report.Account, report.RoleId, report.RoleName, report.Level,
            report.Career, report.Scene, report.Map, report.Instance, report.Line, report.X, report.Y, report.Z, report.Text, report.Truncated,
            report.Context, t?.Status ?? NewStatus, t?.Assignee, t == null ? null : DateTimeOffset.FromUnixTimeSeconds(t.UpdatedUtc), t?.UpdatedBy,
            [.. notes.Select(n => new PanelReportNote(DateTimeOffset.FromUnixTimeSeconds(n.Utc), n.Author, n.Text))]);
    }

    public async Task<bool> UpdateReportAsync(string admin, long id, PanelReportUpdate update)
    {
        if (await Task.Run(() => feedback.Store.Read(id - 1, null, 1).Any(r => r.Id == id)) is false) return false;
        return await accounts.WriteAsync<bool>(new FeedbackTriageWritePlan(id, update.Status, update.Assignee, update.Note, admin, Now, NewStatus));
    }
}
