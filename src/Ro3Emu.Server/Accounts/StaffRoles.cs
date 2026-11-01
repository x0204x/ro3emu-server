using Microsoft.Extensions.Options;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// The web panel's staff roles (docs/guide/panel.md, Roles): Ro3Emu:Panel:Roles and Ro3Emu:GmAccounts (Admin) from the
/// configuration, which the panel cannot change, and the panel_role rows an Admin gave on the panel. The rows are kept
/// in memory (read at start, replaced after each change), so a role check never waits on the database. Maintenance
/// mode lets every staff account in. Whoever has GM in game is an Admin here too; the other roles are the panel's only.
/// </summary>
public sealed class StaffRoles(IOptions<ServerOptions> options, AccountStore accounts, TimeProvider clock)
{
    private volatile Dictionary<string, StaffRoleRow>? _panel;

    /// <summary>
    /// The configured role of <paramref name="account"/>, or null: Admin for whoever has GM in game (the same check,
    /// GmCommands.Allowed: the listed GmAccounts, or every account while the list is empty and GM is open), then
    /// Ro3Emu:Panel:Roles.
    /// </summary>
    public PanelRole? Configured(string account)
    {
        var o = options.Value;
        if (Game.GmCommands.Allowed(o, account, loopbackTool: false)) return PanelRole.Admin;
        return o.Panel.Roles.TryGetValue(account, out string? text) && PanelRoles.Parse(text) is { } role && role != PanelRole.None ? role : null;
    }

    public PanelRole RoleOf(string account) =>
        Configured(account) ?? (Rows().TryGetValue(account, out var row) && PanelRoles.Parse(row.Role) is { } role ? role : PanelRole.None);

    /// <summary>Every staff account: the configured ones and the panel's, by name.</summary>
    internal IReadOnlyList<(string Account, PanelRole Role, string Source, StaffRoleRow? Row)> All()
    {
        var o = options.Value;
        var list = new Dictionary<string, (string, PanelRole, string, StaffRoleRow?)>(StringComparer.Ordinal);
        foreach (var row in Rows().Values)
            if (PanelRoles.Parse(row.Role) is { } role and not PanelRole.None) list[row.Account] = (row.Account, role, "panel", row);
        foreach (var (account, text) in o.Panel.Roles)
            if (PanelRoles.Parse(text) is { } role and not PanelRole.None) list[account] = (account, role, "config", null);
        foreach (string account in o.GmAccounts) list[account] = (account, PanelRole.Admin, "gm-accounts", null);
        return [.. list.Values.OrderBy(s => s.Item1, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gives or removes a panel role with its audit row; the stored account name, or null when there is none.</summary>
    public async Task<string?> SetAsync(string admin, string account, PanelRole role)
    {
        string? stored = await accounts.WriteAsync<string?>(new PanelRoleWritePlan(account, PanelRoles.Key(role), admin,
            clock.GetUtcNow().ToUnixTimeSeconds()));
        if (stored != null) await LoadAsync();
        return stored;
    }

    /// <summary>Reads the panel's rows now (the host does it at start, so no request waits for it).</summary>
    public void Load() => _panel = Index(accounts.ReadSync<List<StaffRoleRow>>(new PanelRolesListRead()));

    private async Task LoadAsync() => _panel = Index(await accounts.ReadAsync<List<StaffRoleRow>>(new PanelRolesListRead()));

    private Dictionary<string, StaffRoleRow> Rows()
    {
        if (_panel is { } rows) return rows;
        Load();
        return _panel!;
    }

    private static Dictionary<string, StaffRoleRow> Index(List<StaffRoleRow> rows) => rows.ToDictionary(r => r.Account, StringComparer.Ordinal);
}
