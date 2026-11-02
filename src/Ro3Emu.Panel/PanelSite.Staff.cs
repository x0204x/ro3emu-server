using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ro3Emu.Panel;

/// <summary>
/// Staff roles (docs/guide/panel.md, Roles): Moderator (accounts and characters read-only, bans, reports, the audit
/// log), GM (the game tools and character actions too) and Admin (everything, with roles and server control). Every
/// route names the least role it needs; the server knows each account's role (<see cref="IPanelOperations.RoleOf"/>).
/// With Ro3Emu:Panel:RequireAdmin2FA a GM or Admin without two-factor authentication has no staff role until it is on.
/// </summary>
public sealed partial class PanelSite
{
    internal sealed record RoleForm(string? Account, string? Role);

    private IEnumerable<Route> StaffRoutes() =>
    [
        Get("/api/admin/roles", Access.Admin, async _ => Reply.Ok(new { staff = (await _ops.StaffAsync()).Select(StaffBody) })),
        Post("/api/admin/roles", Access.Admin, SetRoleAsync),
    ];

    private static PanelRole? Needed(Access access) => access switch
    {
        Access.Moderator => PanelRole.Moderator,
        Access.Gm => PanelRole.Gm,
        Access.Admin => PanelRole.Admin,
        _ => null,
    };

    private static string Refusal(PanelRole needed) => needed switch
    {
        PanelRole.Admin => "Only an Admin can open this page.",
        PanelRole.Gm => "Only a GM or an Admin can do this.",
        _ => "Only staff can open this page.",
    };

    /// <summary>True when the role needs two-factor authentication on this server (GM and Admin, when required).</summary>
    private bool NeedsTwoFactor(PanelRole role) => role >= PanelRole.Gm && _options.RequireAdmin2FA == true;

    /// <summary>
    /// The account's staff role as the routes see it: none while a required second factor is not on yet. A forwarded
    /// request comes from a player behind a proxy, not from this machine, so it never gets the Admin of an open local
    /// server (<see cref="IPanelOperations.ListedRoleOf"/>), whatever the proxy says its Host is.
    /// </summary>
    private async Task<PanelRole> StaffRoleAsync(string account, HttpRequest request)
    {
        var role = ClientAddresses.IsForwarded(request) ? _ops.ListedRoleOf(account) : _ops.RoleOf(account);
        if (NeedsTwoFactor(role) && !(await _ops.TwoFactorAsync(account)).Enabled) return PanelRole.None;
        return role;
    }

    private static object StaffBody(PanelStaff s) => new
    {
        account = s.Account, role = PanelRoles.Key(s.Role), source = s.Source, grantedBy = s.GrantedBy, granted = s.Granted,
        twoFactor = s.TwoFactor,
    };

    private async Task<Reply> SetRoleAsync(Call call)
    {
        var form = await BodyAsync<RoleForm>(call);
        string account = form?.Account?.Trim() ?? "";
        if (account.Length is 0 or > NameMax || PanelRoles.Parse(form?.Role) is not { } role)
            return Bad("Choose an account and a role: none, moderator, gm or admin.");
        if (string.Equals(account, call.Signed, StringComparison.OrdinalIgnoreCase))
            return Bad("You cannot change your own role.");
        var result = await _ops.SetRoleAsync(call.Signed, account, role);
        if (result == PanelRoleChange.Changed) LogRoleChanged(_log, call.Signed, account, PanelRoles.Key(role));
        return result switch
        {
            PanelRoleChange.Changed or PanelRoleChange.Unchanged => Reply.Ok(new { account, role = PanelRoles.Key(role) }),
            PanelRoleChange.NoSuchAccount => Reply.Error(StatusCodes.Status404NotFound, "There is no such account."),
            _ => Reply.Error(StatusCodes.Status409Conflict, "This role is set in the server's configuration; change it there."),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel: {Admin} set the staff role of {Account} to '{Role}'")]
    private static partial void LogRoleChanged(ILogger logger, string admin, string account, string role);
}
