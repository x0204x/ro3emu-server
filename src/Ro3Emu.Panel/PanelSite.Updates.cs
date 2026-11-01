using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// Updates (Admins, audited by the server): the installed version against the newest releases, a checked download,
/// and an install or rollback planned like a restart, with the countdown in the game. Nothing installs by itself.
/// </summary>
public sealed partial class PanelSite
{
    /// <summary>A release tag as the releases name them (v1.0.24, v1.1.0-rc.1).</summary>
    [GeneratedRegex(@"^v?\d{1,6}\.\d{1,6}\.\d{1,6}(-[0-9A-Za-z.\-]{1,32})?$")]
    private static partial Regex ReleaseTag();

    internal sealed record DownloadForm(string? Tag);
    internal sealed record UpdatePlanForm(int Minutes, string? Reason, bool RestoreDatabase);

    private IEnumerable<Route> UpdateRoutes() =>
    [
        Get("/api/admin/updates", Access.Admin, async _ => Reply.Ok(await _updates.StateAsync())),
        Post("/api/admin/updates/check", Access.Admin, async call => Reply.Ok(await _updates.CheckAsync(call.Signed))),
        Post("/api/admin/updates/download", Access.Admin, DownloadUpdateAsync),
        Post("/api/admin/updates/cancel", Access.Admin, async call => Done(await _updates.CancelDownloadAsync(call.Signed))),
        Post("/api/admin/updates/discard", Access.Admin, async call => Done(await _updates.DiscardAsync(call.Signed))),
        Post("/api/admin/updates/install", Access.Admin, async call => await UpdatePlanAsync(call, _updates.InstallAsync)),
        Post("/api/admin/updates/rollback", Access.Admin, async call => await UpdatePlanAsync(call, _updates.RollbackAsync)),
    ];

    private async Task<Reply> DownloadUpdateAsync(Call call)
    {
        string tag = (await BodyAsync<DownloadForm>(call))?.Tag?.Trim() ?? "";
        if (!ReleaseTag().IsMatch(tag)) return Bad("Choose a release.");
        return Done(await _updates.DownloadAsync(call.Signed, tag));
    }

    private static async Task<Reply> UpdatePlanAsync(Call call, Func<string, PanelUpdatePlan, Task<PanelActionResult>> plan)
    {
        var form = await BodyAsync<UpdatePlanForm>(call);
        string reason = form?.Reason?.Trim() ?? "";
        if (form == null || form.Minutes is < 0 or > ScheduleMinutesMax) return Bad("Start the countdown 0 to 1440 minutes from now.");
        if (reason.Length > BroadcastMax || reason.Any(char.IsControl)) return Bad("Give a reason of at most 200 characters on one line.");
        return Done(await plan(call.Signed, new PanelUpdatePlan(form.Minutes, reason, form.RestoreDatabase)));
    }

    /// <summary>A refused update action is a conflict with the state the page showed: its message says why.</summary>
    private static Reply Done(PanelActionResult result) =>
        result.Done ? Reply.Ok(result) : Reply.Error(StatusCodes.Status409Conflict, result.Message);
}
