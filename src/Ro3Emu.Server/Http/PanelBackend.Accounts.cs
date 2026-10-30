using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Http;

/// <summary>The panel's accounts: logins, sign-up, password, the account pages, bans and the audit log.</summary>
public sealed partial class PanelBackend
{
    public Task<string?> VerifyAsync(string account, string password) => accounts.VerifiedAccountAsync(account, password);

    public Task<bool> IsBannedAsync(string account) => accounts.IsBannedAsync(account);

    public Task<bool> ChangePasswordAsync(string account, string current, string replacement) =>
        accounts.ChangePasswordAsync(account, current, replacement);

    /// <summary>The game's own registration (AccountStore.RegisterAsync); the name rule is the SDK login's: trimmed, not empty.</summary>
    public async Task<PanelRegistration> RegisterAsync(string account, string password)
    {
        if (account.Length == 0 || account != account.Trim() || password.Length == 0) return PanelRegistration.Invalid;
        return await accounts.RegisterAsync(account, password) switch
        {
            AccountStore.Registration.Created => PanelRegistration.Created,
            AccountStore.Registration.Banned => PanelRegistration.Banned,
            _ => PanelRegistration.Taken,
        };
    }

    public async Task<PanelAccount?> AccountAsync(string account)
    {
        long now = Now;
        if (await accounts.ReadAsync<PanelAccountRow?>(new PanelAccountRead(account, now)) is not { } row) return null;
        var roles = await accounts.ReadAsync<List<PanelRoleRow>>(new PanelRolesRead(row.Name, 100));
        var online = (await OnlineRowsAsync()).Select(r => r.RoleId).ToHashSet();
        var ban = await accounts.ReadAsync<AccountBanRow?>(new AccountBanRowRead(row.Name, now));
        return new PanelAccount(row.Name, Created(row.CreatedUtc), Unix(row.LastLogin),
            [.. roles.Select(r => new PanelCharacter(r.Id, r.Name, ClassName(r.Career), r.Level, r.JobLevel, MapName(r.SceneEntry), r.Guild,
                DateTimeOffset.FromUnixTimeSeconds(r.CreateTime), Unix(r.LastLoginTime), online.Contains(r.Id), ClassIcon(r.Career)))],
            ban == null ? null : new PanelBan(ban.Reason, DateTimeOffset.FromUnixTimeSeconds(ban.BannedUtc), Unix(ban.UntilUtc), ban.BannedBy));
    }

    /// <summary>account.created_utc is ISO-8601 text (AccountRegisterPlan); a row another tool wrote may hold anything.</summary>
    private static DateTimeOffset? Created(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : null;

    private static DateTimeOffset? Unix(long? seconds) => seconds is > 0 and var s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    public async Task<PanelAccountPage> FindAccountsAsync(string query, int page, int pageSize)
    {
        var (total, rows) = await accounts.ReadAsync<(int, List<PanelAccountRow>)>(
            new PanelAccountSearch(query, (page - 1) * pageSize, pageSize, Now));
        return new PanelAccountPage([.. rows.Select(r => new PanelAccountSummary(r.Name, Created(r.CreatedUtc), r.Characters, Unix(r.LastLogin), r.Banned))],
            total, page, pageSize);
    }

    /// <summary>The ban row and its audit row commit first; then the account's game sessions close on the loop.</summary>
    public async Task<PanelBanOutcome> BanAsync(string admin, string account, string reason, DateTimeOffset? until)
    {
        if (!await accounts.WriteAsync<bool>(new AccountBanWritePlan(account, reason, Now, until?.ToUnixTimeSeconds(), admin)))
            return new PanelBanOutcome(false, 0);
        return new PanelBanOutcome(true, await KickAsync(account));
    }

    /// <summary>Closes every game session of <paramref name="account"/>; how many there were.</summary>
    private async Task<int> KickAsync(string account)
    {
        try
        {
            return await loop.InvokeAsync(() =>
            {
                var own = sessions.All().Where(s => string.Equals(s.Account, account, StringComparison.Ordinal)).ToList();
                foreach (var session in own) reconnect.KickNow(session);
                return Task.FromResult(own.Count);
            });
        }
        catch (OperationCanceledException) { return 0; } // stopping: every session closes anyway
    }

    public Task<bool> UnbanAsync(string admin, string account, string reason) =>
        accounts.WriteAsync<bool>(new AccountUnbanPlan(account, reason, Now, admin));

    public async Task<PanelAuditPage> AuditAsync(int page, int pageSize)
    {
        var (total, rows) = await accounts.ReadAsync<(int, List<AdminAuditRow>)>(new AdminAuditRead((page - 1) * pageSize, pageSize));
        return new PanelAuditPage([.. rows.Select(r => new PanelAuditEntry(r.Id, DateTimeOffset.FromUnixTimeSeconds(r.Utc), r.Admin, r.Action, r.Target, r.Detail))],
            total, page, pageSize);
    }

    public async Task<PanelDashboard> DashboardAsync()
    {
        var (accountCount, characterCount) = await accounts.ReadAsync<(int, int)>(new PanelCountsRead());
        var m = metrics.Headline();
        var current = census.Current;
        var maps = current.Roles.GroupBy(r => r.Map.SceneEntry).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new PanelMapCount(MapName(g.Key), g.Count()));
        return new PanelDashboard(m.Phase, current.Roles.Count(r => !r.Loading), current.Roles.Count(r => r.Loading), current.Login,
            m.Connections, accountCount, characterCount, m.TicksPerSecond, m.Tick.P50, m.Tick.P99, m.Tick.Max, m.Overruns, m.QueueLength,
            m.WorkingSetBytes, m.CpuSeconds, m.Gen2Collections, m.Exceptions, m.ErrorLogs, m.Commits, [.. maps]);
    }
}
