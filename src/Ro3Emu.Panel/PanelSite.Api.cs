using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ro3Emu.Panel;

/// <summary>The public pages' API and the player area: session, login, sign-up, password, status, news, rankings.</summary>
public sealed partial class PanelSite
{
    /// <summary>
    /// The game accepts any account name that is not empty once trimmed (SdkEndpoints, AccountStore). A sign-up on a
    /// public website also refuses control characters and very long names, and asks for a password of a few characters:
    /// the panel's own rule (inferred), not the game's, so an account the game created can always log in here.
    /// </summary>
    internal const int NameMax = 64, PasswordMin = 6, PasswordMax = 128;
    private const string WrongCredentials = "Wrong account name or password.";
    private const string TooManyAttempts = "Too many attempts from your address. Wait a minute and try again.";

    internal sealed record Credentials(string? Account, string? Password);
    internal sealed record PasswordChange(string? Current, string? Next);

    private IEnumerable<Route> PublicRoutes() =>
    [
        Get("/api/session", Access.Public, SessionAsync),
        Post("/api/login", Access.Public, LoginAsync),
        Post("/api/logout", Access.Public, LogoutAsync),
        Post("/api/register", Access.Public, RegisterAsync),
        Get("/api/status", Access.Public, StatusAsync),
        Get("/api/news", Access.Public, NewsAsync),
        Get("/api/online", Access.Public, OnlineAsync),
        Get("/api/rankings", Access.Public, BoardsAsync),
        Get("/api/rankings/{}", Access.Public, BoardAsync),
    ];

    private IEnumerable<Route> PlayerRoutes() =>
    [
        Get("/api/account", Access.Player, AccountAsync),
        Post("/api/account/password", Access.Player, ChangePasswordAsync),
    ];

    /// <param name="role">The staff role the routes grant (none while a required second factor is off).</param>
    private object SessionBody(string? account, PanelRole role, string csrf) => new
    {
        account,
        admin = role != PanelRole.None,
        role = PanelRoles.Key(role),
        // A GM or Admin whose required second factor is not on yet: the page asks to turn it on.
        twoFactorRequired = account != null && role == PanelRole.None && NeedsTwoFactor(_ops.RoleOf(account)),
        csrf,
        registration = _options.Registration,
        server = BrandBody(),
    };

    /// <summary>Who is signed in, and the CSRF token for the next POST (a visitor cookie is set before a login).</summary>
    private Task<Reply> SessionAsync(Call call)
    {
        if (call.SessionId != null) return Task.FromResult(Reply.Ok(SessionBody(call.Account, call.Role, _sessions.Csrf("session", call.SessionId))));
        string? visitor = call.Context.Request.Cookies[VisitorCookie];
        if (string.IsNullOrEmpty(visitor) || visitor.Length > 64)
        {
            visitor = PanelSessions.NewId();
            SetCookie(call.Context, VisitorCookie, visitor);
        }
        if (call.Context.Request.Cookies.ContainsKey(SessionCookie)) ClearCookie(call.Context, SessionCookie);
        return Task.FromResult(Reply.Ok(SessionBody(null, PanelRole.None, _sessions.Csrf("visitor", visitor))));
    }

    /// <summary>
    /// A new session for <paramref name="account"/>: a fresh id (never the one sent before the login), recorded in the
    /// account's login history.
    /// </summary>
    private async Task<Reply> SignInAsync(Call call, string account)
    {
        _sessions.End(call.SessionId);
        string sessionId = _sessions.Create(account, _options.LoginAddresses ? call.Client?.ToString() : null,
            call.Context.Request.Headers.UserAgent.ToString());
        SetCookie(call.Context, SessionCookie, sessionId);
        ClearCookie(call.Context, VisitorCookie);
        await _ops.RecordLoginAsync(account, "panel", call.Client);
        LogLogin(_log, account);
        return Reply.Ok(SessionBody(account, await StaffRoleAsync(account), _sessions.Csrf("session", sessionId)));
    }

    private async Task<Reply> LoginAsync(Call call)
    {
        var form = await BodyAsync<Credentials>(call);
        string name = form?.Account?.Trim() ?? "", password = form?.Password ?? "";
        if (name.Length == 0 || password.Length == 0)
            return Reply.Error(StatusCodes.Status400BadRequest, "Enter your account name and password.");
        if (!_backend.TryAcquireCredentialCheck(call.Client)) return Reply.Error(StatusCodes.Status429TooManyRequests, TooManyAttempts);
        // Null for a wrong password, an unknown name and a banned account alike, as the game's login answers.
        if (await _backend.VerifyAsync(name, password) is not { } account)
            return Reply.Error(StatusCodes.Status401Unauthorized, WrongCredentials);
        // With a second factor the password only opens a challenge; the session comes with the code (LoginCodeAsync).
        if ((await _ops.TwoFactorAsync(account)).Enabled)
            return Reply.Ok(new { twoFactor = true, challenge = _sessions.Challenge(account), csrf = ExpectedCsrf(call.Context, null) });
        return await SignInAsync(call, account);
    }

    private Task<Reply> LogoutAsync(Call call)
    {
        _sessions.End(call.SessionId);
        ClearCookie(call.Context, SessionCookie);
        return Task.FromResult(new Reply(StatusCodes.Status200OK, new { account = (string?)null }));
    }

    private async Task<Reply> RegisterAsync(Call call)
    {
        if (!_options.Registration)
            return Reply.Error(StatusCodes.Status403Forbidden, "Sign-up is closed here. Your first login in the game creates your account.");
        var form = await BodyAsync<Credentials>(call);
        string name = form?.Account?.Trim() ?? "", password = form?.Password ?? "";
        if ((NameProblem(name) ?? PasswordProblem(password)) is { } problem) return Reply.Error(StatusCodes.Status400BadRequest, problem);
        if (!_backend.TryAcquireCredentialCheck(call.Client)) return Reply.Error(StatusCodes.Status429TooManyRequests, TooManyAttempts);
        if (_signUps.TryReserve(call.Client, out var slot) is var refusal and not PanelSignUps.Refusal.None)
        {
            bool perAddress = refusal == PanelSignUps.Refusal.Address;
            // The address only where the operator keeps addresses (PanelOptions.LoginAddresses).
            string? address = _options.LoginAddresses ? call.Client?.ToString() : null;
            LogSignUpLimited(_log, name, address, perAddress ? "per address" : "per hour");
            return Reply.Error(StatusCodes.Status429TooManyRequests, perAddress
                ? "Too many accounts were made from your network today. Try again tomorrow."
                : "Sign-up is busy right now. Try again in an hour.");
        }
        var result = await _backend.RegisterAsync(name, password);
        if (result != PanelRegistration.Created) _signUps.Release(slot);
        LogRegistration(_log, name, result);
        return result switch
        {
            PanelRegistration.Created => await SignInAsync(call, name),
            PanelRegistration.Invalid => Reply.Error(StatusCodes.Status400BadRequest, "This account name cannot be used."),
            // A banned name answers like a taken one: the page does not reveal a ban.
            _ => Reply.Error(StatusCodes.Status409Conflict, "This account name is taken."),
        };
    }

    /// <summary>
    /// A new account's name on the panel's sign-up: 4 to 24 ASCII letters, digits and underscores. (inferred) The game
    /// itself has no rule beyond "not empty once trimmed" (SdkEndpoints, AccountStore), so this is the panel's own, for
    /// names typed on a public website; it never applies to logins, so an account the game created can always log in.
    /// </summary>
    internal const int SignUpNameMin = 4, SignUpNameMax = 24;

    internal static string? NameProblem(string name) =>
        name.Length == 0 ? "Choose an account name."
        : name.Length is < SignUpNameMin or > SignUpNameMax ? "An account name has 4 to 24 characters."
        : !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? "An account name has only letters (A to Z), digits and underscores."
        : null;

    internal static string? PasswordProblem(string password) =>
        password.Length < PasswordMin ? "A password has at least 6 characters."
        : password.Length > PasswordMax ? "A password has at most 128 characters."
        : null;

    private async Task<Reply> ChangePasswordAsync(Call call)
    {
        var form = await BodyAsync<PasswordChange>(call);
        string current = form?.Current ?? "", next = form?.Next ?? "";
        if (current.Length == 0) return Reply.Error(StatusCodes.Status400BadRequest, "Enter your current password.");
        if (PasswordProblem(next) is { } problem) return Reply.Error(StatusCodes.Status400BadRequest, problem);
        if (!_backend.TryAcquireCredentialCheck(call.Client)) return Reply.Error(StatusCodes.Status429TooManyRequests, TooManyAttempts);
        string account = call.Signed;
        if (!await _backend.ChangePasswordAsync(account, current, next))
            return Reply.Error(StatusCodes.Status400BadRequest, "Your current password is not right.");
        // Other browsers signed in with the old password are signed out; this one stays.
        _sessions.EndAll(account, keep: call.SessionId);
        LogPasswordChanged(_log, account);
        return Reply.Ok(new { changed = true });
    }

    private async Task<Reply> StatusAsync(Call call)
    {
        var info = _backend.Info;
        var counts = await _backend.CountsAsync();
        return Reply.Ok(new
        {
            name = ServerName, tagline = Trimmed(Brand.Tagline, 160), area = info.Area, version = info.Version, players = _backend.PlayersOnline(),
            accounts = counts.Accounts, characters = counts.Characters,
            startedUtc = info.StartedUtc, uptimeSeconds = (long)Math.Max(0, (_clock.GetUtcNow() - info.StartedUtc).TotalSeconds),
        });
    }

    private async Task<Reply> NewsAsync(Call call)
    {
        int limit = int.TryParse(call.Query("limit"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 50) : 20;
        return Reply.Ok(new { news = await _backend.NewsAsync(limit) });
    }

    /// <summary>Characters in the world; the account behind each one only for an admin.</summary>
    private async Task<Reply> OnlineAsync(Call call)
    {
        var roles = await _backend.OnlineAsync();
        return Reply.Ok(new
        {
            players = roles.Select(r => new
            {
                name = r.Name, @class = r.Class, classIcon = r.ClassIcon, level = r.Level, map = r.Map, mapId = r.MapId,
                account = call.Admin ? r.Account : null,
            }),
        });
    }

    private Task<Reply> BoardsAsync(Call call) =>
        Task.FromResult(Reply.Ok(new { boards = _backend.Boards(), classes = _backend.Classes() }));

    private async Task<Reply> BoardAsync(Call call)
    {
        long rootClass = long.TryParse(call.Query("class"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long c) ? c : 0;
        return await _backend.BoardAsync(call.Args[0], rootClass) is { } board
            ? Reply.Ok(board) : Reply.Error(StatusCodes.Status404NotFound, "There is no such ranking.");
    }

    private async Task<Reply> AccountAsync(Call call) =>
        await _backend.AccountAsync(call.Signed) is { } account
            ? Reply.Ok(new { account.Name, account.Created, account.LastLogin, account.Characters, admin = call.Admin })
            : Reply.Error(StatusCodes.Status404NotFound, "Your account was not found.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel login {Account}")]
    private static partial void LogLogin(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel sign-up {Account}: {Result}")]
    private static partial void LogRegistration(ILogger logger, string account, PanelRegistration result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel sign-up {Account} from {Address}: refused, the {Budget} sign-up limit is used up")]
    private static partial void LogSignUpLimited(ILogger logger, string account, string? address, string budget);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel password change {Account}")]
    private static partial void LogPasswordChanged(ILogger logger, string account);
}
