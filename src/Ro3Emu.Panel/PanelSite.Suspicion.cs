using System.Globalization;

namespace Ro3Emu.Panel;

/// <summary>
/// The suspicious-activity list (staff): anti-cheat refusals per account and unusual gains per character over a window,
/// and, for an Admin, the item duplication check's flags. Read only; see <see cref="IPanelBackend.SuspicionAsync"/>.
/// </summary>
public sealed partial class PanelSite
{
    private const int SuspicionMaxHours = 24 * 30;

    private IEnumerable<Route> SuspicionRoutes() =>
    [
        Get("/api/admin/suspicious", Access.Moderator, SuspiciousAsync),
    ];

    /// <summary>?hours=1..720 (the configured window when absent), ?dupes=1 also runs the duplication check (Admin only).</summary>
    private async Task<Reply> SuspiciousAsync(Call call)
    {
        int? hours = int.TryParse(call.Query("hours"), NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            ? Math.Clamp(h, 1, SuspicionMaxHours) : null;
        bool dupes = call.Query("dupes") == "1" && call.Role == PanelRole.Admin;
        return Reply.Ok(await _backend.SuspicionAsync(hours, dupes));
    }
}
