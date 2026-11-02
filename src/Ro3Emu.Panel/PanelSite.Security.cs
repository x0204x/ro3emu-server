using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Net.Codecrete.QrCodeGenerator;

namespace Ro3Emu.Panel;

/// <summary>
/// Account security (docs/guide/panel.md, Account security): the login history, the signed-in browsers with "log out
/// everywhere", and two-factor authentication with TOTP codes (RFC 6238) and recovery codes. A code is checked by the
/// server, good once, and limited per challenge, per account and per address; no code or secret is ever logged.
/// </summary>
public sealed partial class PanelSite
{
    private const int LoginHistory = 30;
    private const string WrongCode = "That code is not right. Try the current one from your app, or a recovery code.";
    private const string TooManyCodes = "Too many wrong codes. Wait a few minutes and try again.";

    internal sealed record CodeForm(string? Challenge, string? Code);

    private IEnumerable<Route> SecurityRoutes() =>
    [
        Post("/api/login/2fa", Access.Public, LoginCodeAsync),
        Get("/api/account/security", Access.Player, SecurityAsync),
        Post("/api/account/2fa/begin", Access.Player, BeginTwoFactorAsync),
        Post("/api/account/2fa/enable", Access.Player, EnableTwoFactorAsync),
        Post("/api/account/2fa/disable", Access.Player, DisableTwoFactorAsync),
        Post("/api/account/2fa/recovery", Access.Player, RecoveryCodesAsync),
        Post("/api/account/sessions/{}/end", Access.Player, EndSessionAsync),
        Post("/api/account/sessions/end-others", Access.Player, EndOtherSessionsAsync),
        Get("/api/admin/accounts/{}/logins", Access.Moderator, AccountLoginsAsync),
        Post("/api/admin/accounts/{}/logout", Access.Admin, LogOutAccountAsync),
        Post("/api/admin/accounts/{}/2fa-reset", Access.Admin, ResetTwoFactorAsync),
    ];

    /// <summary>The second step of a login with 2FA: the challenge from /api/login and a code.</summary>
    private async Task<Reply> LoginCodeAsync(Call call)
    {
        var form = await BodyAsync<CodeForm>(call);
        if (_sessions.TryChallenge(form?.Challenge) is not { } account)
            return Reply.Error(StatusCodes.Status401Unauthorized, "This login has expired. Enter your password again.");
        if (await CheckCodeAsync(call, account, form?.Code) is { } refused) return refused;
        _sessions.EndChallenge(form!.Challenge!);
        return await SignInAsync(call, account);
    }

    /// <summary>
    /// Checks a code against the account's 2FA within every limit: the account's failure window and the address's
    /// credential budget. Null when it matched, else the answer to send.
    /// </summary>
    private async Task<Reply?> CheckCodeAsync(Call call, string account, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return Bad("Enter the 6-digit code from your app, or a recovery code.");
        if (!_sessions.MayTryCode(account) || !_backend.TryAcquireCredentialCheck(call.From))
            return Reply.Error(StatusCodes.Status429TooManyRequests, TooManyCodes);
        if (await _ops.CheckTwoFactorAsync(account, code)) return null;
        _sessions.CodeFailed(account);
        LogCodeRefused(_log, account);
        return Reply.Error(StatusCodes.Status401Unauthorized, WrongCode);
    }

    private async Task<Reply> SecurityAsync(Call call)
    {
        string account = call.Signed;
        var twoFactor = await _ops.TwoFactorAsync(account);
        return Reply.Ok(new
        {
            twoFactor = new { twoFactor.Enabled, twoFactor.Pending, twoFactor.RecoveryLeft, required = NeedsTwoFactor(_ops.RoleOf(account)) },
            sessions = _sessions.List(account, call.SessionId).Select(s => new
            {
                id = s.Handle, s.Created, s.LastSeen, address = _options.LoginAddresses ? s.Address : null, agent = s.Agent, s.Current,
            }),
            logins = await _ops.LoginsAsync(account, LoginHistory),
            addresses = _options.LoginAddresses,
        });
    }

    private async Task<Reply> BeginTwoFactorAsync(Call call)
    {
        string account = call.Signed;
        if (await _ops.BeginTwoFactorAsync(account) is not { } secret)
            return Reply.Error(StatusCodes.Status409Conflict, "Two-factor authentication is already on.");
        // The secret is shown once, to its owner, over this page; it is never logged.
        string uri = Totp.Uri(ServerName, account, secret);
        return Reply.Ok(new { secret = Totp.ToBase32(secret), uri, qr = QrCodeOf(uri) });
    }

    /// <summary>
    /// The QR code of an otpauth URI for the authenticator app's camera: the side in modules (with the 4-module quiet
    /// zone the standard asks for) and one SVG path of the dark modules, which the page draws in its own SVG.
    /// </summary>
    internal static object QrCodeOf(string text)
    {
        const int Border = 4;
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        return new { size = qr.Size + 2 * Border, path = qr.ToGraphicsPath(Border) };
    }

    private async Task<Reply> EnableTwoFactorAsync(Call call)
    {
        var form = await BodyAsync<CodeForm>(call);
        string account = call.Signed;
        if (string.IsNullOrWhiteSpace(form?.Code)) return Bad("Enter the 6-digit code your app shows.");
        if (!_sessions.MayTryCode(account) || !_backend.TryAcquireCredentialCheck(call.From))
            return Reply.Error(StatusCodes.Status429TooManyRequests, TooManyCodes);
        if (await _ops.EnableTwoFactorAsync(account, form.Code) is not { } recovery)
        {
            _sessions.CodeFailed(account);
            return Reply.Error(StatusCodes.Status400BadRequest, "That code is not right. Check the time on your phone and try the current one.");
        }
        // Other browsers signed in with the password alone are signed out.
        _sessions.EndAll(account, keep: call.SessionId);
        LogTwoFactor(_log, account, "on");
        return Reply.Ok(new { enabled = true, recoveryCodes = recovery });
    }

    private async Task<Reply> DisableTwoFactorAsync(Call call)
    {
        var form = await BodyAsync<CodeForm>(call);
        string account = call.Signed;
        if (await CheckCodeAsync(call, account, form?.Code) is { } refused) return refused;
        await _ops.DisableTwoFactorAsync(account, null);
        LogTwoFactor(_log, account, "off");
        return Reply.Ok(new { enabled = false });
    }

    private async Task<Reply> RecoveryCodesAsync(Call call)
    {
        var form = await BodyAsync<CodeForm>(call);
        string account = call.Signed;
        if (await CheckCodeAsync(call, account, form?.Code) is { } refused) return refused;
        return await _ops.NewRecoveryCodesAsync(account) is { } codes
            ? Reply.Ok(new { recoveryCodes = codes }) : Reply.Error(StatusCodes.Status409Conflict, "Two-factor authentication is off.");
    }

    private Task<Reply> EndSessionAsync(Call call) => Task.FromResult(_sessions.EndByHandle(call.Signed, call.Args[0])
        ? Reply.Ok(new { ended = 1 }) : Reply.Error(StatusCodes.Status404NotFound, "That browser is no longer signed in."));

    private Task<Reply> EndOtherSessionsAsync(Call call) =>
        Task.FromResult(Reply.Ok(new { ended = _sessions.EndAll(call.Signed, keep: call.SessionId) }));

    private async Task<Reply> AccountLoginsAsync(Call call)
    {
        if (await _backend.AccountAsync(call.Args[0]) is not { } account) return Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
        var logins = await _ops.LoginsAsync(account.Name, LoginHistory);
        var twoFactor = await _ops.TwoFactorAsync(account.Name);
        return Reply.Ok(new { logins, twoFactor, sessions = _sessions.List(account.Name, null).Count, addresses = _options.LoginAddresses });
    }

    private async Task<Reply> LogOutAccountAsync(Call call)
    {
        if (await _backend.AccountAsync(call.Args[0]) is not { } account) return Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
        int ended = _sessions.EndAll(account.Name, keep: account.Name == call.Signed ? call.SessionId : null);
        await _ops.RecordPanelLogoutAsync(call.Signed, account.Name, ended);
        return Reply.Ok(new { ended });
    }

    /// <summary>An Admin turns off another account's 2FA (a lost phone without recovery codes); audited by the server.</summary>
    private async Task<Reply> ResetTwoFactorAsync(Call call)
    {
        if (await _backend.AccountAsync(call.Args[0]) is not { } account) return Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
        if (!await _ops.DisableTwoFactorAsync(account.Name, call.Signed))
            return Reply.Error(StatusCodes.Status409Conflict, "This account has no two-factor authentication.");
        _sessions.EndAll(account.Name);
        LogTwoFactor(_log, account.Name, "reset by " + call.Signed);
        return Reply.Ok(new { enabled = false });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel: a wrong second-factor code for {Account}")]
    private static partial void LogCodeRefused(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel: two-factor authentication of {Account} {State}")]
    private static partial void LogTwoFactor(ILogger logger, string account, string state);
}
