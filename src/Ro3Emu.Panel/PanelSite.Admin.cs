using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ro3Emu.Panel;

/// <summary>
/// The admin area, for the accounts listed in Ro3Emu:GmAccounts only: the dashboard, accounts, bans and the audit log.
/// Every change is recorded in the audit log by the server, in the transaction that makes it.
/// </summary>
public sealed partial class PanelSite
{
    private const int PageSize = 25, ReasonMax = 300;
    /// <summary>Longest timed ban: ten years; anything longer is a ban without an end.</summary>
    private const int BanHoursMax = 87_600;

    internal sealed record BanForm(string? Reason, int? Hours);
    internal sealed record UnbanForm(string? Reason);

    private IEnumerable<Route> AdminRoutes() =>
    [
        Get("/api/admin/dashboard", Access.Admin, async _ => Reply.Ok(await _backend.DashboardAsync())),
        Get("/api/admin/accounts", Access.Admin, FindAccountsAsync),
        Get("/api/admin/accounts/{}", Access.Admin, AdminAccountAsync),
        Post("/api/admin/accounts/{}/ban", Access.Admin, BanAsync),
        Post("/api/admin/accounts/{}/unban", Access.Admin, UnbanAsync),
        Get("/api/admin/audit", Access.Admin, async call => Reply.Ok(await _backend.AuditAsync(call.Page(), PageSize))),
    ];

    private async Task<Reply> FindAccountsAsync(Call call)
    {
        string query = call.Query("q").Trim();
        if (query.Length > NameMax) query = query[..NameMax];
        return Reply.Ok(await _backend.FindAccountsAsync(query, call.Page(), PageSize));
    }

    private async Task<Reply> AdminAccountAsync(Call call) =>
        await _backend.AccountAsync(call.Args[0]) is { } account
            ? Reply.Ok(new { account, admin = _backend.IsAdmin(account.Name) })
            : Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");

    private async Task<Reply> BanAsync(Call call)
    {
        var form = await BodyAsync<BanForm>(call);
        string reason = form?.Reason?.Trim() ?? "";
        if (reason.Length == 0 || reason.Length > ReasonMax)
            return Reply.Error(StatusCodes.Status400BadRequest, "Give a reason of at most 300 characters.");
        if (form?.Hours is < 1 or > BanHoursMax)
            return Reply.Error(StatusCodes.Status400BadRequest, "A timed ban lasts 1 hour to 10 years.");
        if (await _backend.AccountAsync(call.Args[0]) is not { } account)
            return Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
        if (string.Equals(account.Name, call.Account, StringComparison.Ordinal))
            return Reply.Error(StatusCodes.Status400BadRequest, "You cannot ban your own account.");
        DateTimeOffset? until = form?.Hours is { } hours ? _clock.GetUtcNow().AddHours(hours) : null;
        string admin = call.Signed;
        var outcome = await _backend.BanAsync(admin, account.Name, reason, until);
        if (!outcome.Found) return Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
        _sessions.EndAll(account.Name);
        LogBanned(_log, admin, account.Name, until, outcome.Kicked);
        return Reply.Ok(new { banned = true, until, kicked = outcome.Kicked });
    }

    private async Task<Reply> UnbanAsync(Call call)
    {
        var form = await BodyAsync<UnbanForm>(call);
        string reason = form?.Reason?.Trim() ?? "";
        if (reason.Length > ReasonMax) return Reply.Error(StatusCodes.Status400BadRequest, "Give a reason of at most 300 characters.");
        if (await _backend.AccountAsync(call.Args[0]) is not { } account)
            return Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
        string admin = call.Signed;
        if (!await _backend.UnbanAsync(admin, account.Name, reason))
            return Reply.Error(StatusCodes.Status409Conflict, "This account is not banned.");
        LogUnbanned(_log, admin, account.Name);
        return Reply.Ok(new { banned = false });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel: {Admin} banned {Account} until {Until} ({Kicked} game sessions closed)")]
    private static partial void LogBanned(ILogger logger, string admin, string account, DateTimeOffset? until, int kicked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel: {Admin} lifted the ban of {Account}")]
    private static partial void LogUnbanned(ILogger logger, string admin, string account);
}
