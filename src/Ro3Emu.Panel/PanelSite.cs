using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ro3Emu.Panel;

/// <summary>
/// The web control panel (docs/guide/panel.md): the static site and its JSON API, on the panel's own port. Requests
/// that arrive on that port never reach the server's other endpoints, and the server's endpoints are not served here.
/// Public pages, a player area behind a login with the game account, and an admin area for the accounts listed in
/// Ro3Emu:GmAccounts. Every change goes through <see cref="IPanelBackend"/>.
/// </summary>
public sealed partial class PanelSite
{
    internal const string SessionCookie = "ro3p_session", VisitorCookie = "ro3p_visitor", CsrfHeader = "X-CSRF-Token";
    /// <summary>Largest request body read (every API body is a small JSON object).</summary>
    private const int MaxBody = 16 * 1024;
    /// <summary>The site's own pages: the first path segment of every route app.js draws.</summary>
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal)
    { "", "online", "rankings", "news", "login", "register", "account", "admin", "items", "monsters", "maps", "characters" };

    /// <summary>
    /// No inline script or style anywhere, so the policy allows only the site's own files. frame-ancestors and the
    /// other headers keep the pages out of frames and stop content sniffing.
    /// </summary>
    private const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PanelOptions _options;
    private readonly IPanelBackend _backend;
    private readonly IPanelAdminTools _tools;
    private readonly TimeProvider _clock;
    private readonly ILogger<PanelSite> _log;
    private readonly PanelSessions _sessions;
    private readonly PanelAssets _assets = new();
    private readonly IPAddress[] _proxies;
    private readonly Route[] _routes;

    public PanelSite(PanelOptions options, IPanelBackend backend, IPanelAdminTools tools, TimeProvider clock, ILogger<PanelSite> log)
    {
        _options = options;
        _backend = backend;
        _tools = tools;
        _clock = clock;
        _log = log;
        _sessions = new PanelSessions(clock, TimeSpan.FromHours(Math.Max(1, options.SessionHours)));
        _proxies = [.. options.TrustedProxies.Select(IPAddress.Parse).Select(Normalize)];
        _routes = [.. PublicRoutes(), .. PlayerRoutes(), .. AdminRoutes(), .. ToolRoutes(), .. DatabaseRoutes(), .. CharacterRoutes()];
    }

    /// <summary>
    /// Hands every request that arrived on <paramref name="port"/> to the panel, and nothing else. The site is resolved
    /// from the request's services, so the host builds it (and its backend) only after its own wiring.
    /// </summary>
    public static void Map(IApplicationBuilder app, int port) =>
        app.MapWhen(context => context.Connection.LocalPort == port,
            branch => branch.Run(context => context.RequestServices.GetRequiredService<PanelSite>().HandleAsync(context)));

    public async Task HandleAsync(HttpContext context)
    {
        Secure(context.Response.Headers);
        string path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await ApiAsync(context, path);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (path.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            await SendImageAsync(context, path);
            return;
        }
        if (path == LogoPath)
        {
            await SendLogoAsync(context, _backend.BrandLogoFile);
            return;
        }
        var asset = path == "/index.html" ? null : _assets.Find(path);
        int status = StatusCodes.Status200OK;
        if (asset == null)
        {
            // Every page is the one shell; app.js draws the route and its own "not found".
            string first = path.TrimStart('/').Split('/')[0];
            status = Pages.Contains(first) ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
            asset = _assets.Find("/index.html")!;
        }
        await SendAssetAsync(context, asset, status);
    }

    private static void Secure(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    private static async Task SendAssetAsync(HttpContext context, PanelAssets.Asset asset, int status)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-cache";
        response.Headers.ETag = asset.ETag;
        if (status == StatusCodes.Status200OK && context.Request.Headers.IfNoneMatch == asset.ETag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        response.StatusCode = status;
        response.ContentType = asset.ContentType;
        response.ContentLength = asset.Body.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await response.Body.WriteAsync(asset.Body, context.RequestAborted);
    }

    // ---------------------------------------------------------------- pictures

    private const string ImagePrefix = "/img/";

    /// <summary>The picture folders the data export writes (DataExport IconExport) and the names it gives the files.</summary>
    [GeneratedRegex(@"^/img/(items|monsters|classes|maps|skills)/[A-Za-z0-9_\-]+\.png$")]
    private static partial Regex ImagePath();

    /// <summary>
    /// A picture from the image folder: only the export's own folders and file names, so no request reaches another
    /// file. The pages ask with ?v=&lt;the source's hash&gt;, so a picture never changes under its address and may be kept
    /// for a year. A missing one is a plain 404; the page then draws its letter.
    /// </summary>
    private async Task SendImageAsync(HttpContext context, string path)
    {
        var response = context.Response;
        string? file = ImagePath().IsMatch(path) && _backend.ImageDir is { } dir ? Path.Combine(dir, path[ImagePrefix.Length..]) : null;
        if (file == null || !File.Exists(file))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            response.Headers.CacheControl = "no-cache";
            return;
        }
        var info = new FileInfo(file);
        string etag = "\"" + info.Length.ToString("x", CultureInfo.InvariantCulture) + "-" + info.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"";
        response.Headers.CacheControl = context.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "public, max-age=86400";
        response.Headers.ETag = etag;
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        response.ContentType = "image/png";
        response.ContentLength = info.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await response.SendFileAsync(file, context.RequestAborted);
    }

    // ---------------------------------------------------------------- the API

    private enum Access { Public, Player, Admin }

    /// <summary>One API route; a "{}" segment matches any one segment and is handed to the handler.</summary>
    private sealed record Route(string Method, string[] Segments, Access Access, Func<Call, Task<Reply>> Handler);

    /// <summary>An answer: the status and a body serialized as JSON (none for 204).</summary>
    private readonly record struct Reply(int Status, object? Body)
    {
        public static Reply Ok(object body) => new(StatusCodes.Status200OK, body);
        public static Reply Error(int status, string message) => new(status, new { error = message });
    }

    /// <summary>A request on its way through the API: the signed-in account, if any, and the route's arguments.</summary>
    private sealed class Call(HttpContext context, string? account, bool admin, string? sessionId, string[] args, IPAddress? client)
    {
        public HttpContext Context { get; } = context;
        public string? Account { get; } = account;
        public bool Admin { get; } = admin;
        public string? SessionId { get; } = sessionId;
        public string[] Args { get; } = args;
        public IPAddress? Client { get; } = client;
        /// <summary>The account of a player or admin route (DispatchAsync refused the request without one).</summary>
        public string Signed => Account ?? throw new InvalidOperationException("A signed-in route ran without an account.");
        public string Query(string key) => Context.Request.Query[key].ToString();

        public int Page() => int.TryParse(Query("page"), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int page) ? Math.Clamp(page, 1, 100_000) : 1;
    }

    private static Route Get(string path, Access access, Func<Call, Task<Reply>> handler) => new("GET", Split(path), access, handler);
    private static Route Post(string path, Access access, Func<Call, Task<Reply>> handler) => new("POST", Split(path), access, handler);
    private static string[] Split(string path) => path.Trim('/').Split('/');

    private async Task ApiAsync(HttpContext context, string path)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        string[] segments = Split(path);
        var matches = _routes.Where(r => Matches(r.Segments, segments)).ToList();
        var route = matches.FirstOrDefault(r => string.Equals(r.Method, context.Request.Method, StringComparison.OrdinalIgnoreCase));
        Reply reply;
        if (route == null)
        {
            reply = matches.Count == 0 ? Reply.Error(StatusCodes.Status404NotFound, "Not found.")
                : Reply.Error(StatusCodes.Status405MethodNotAllowed, "Method not allowed.");
        }
        else
        {
            try { reply = await DispatchAsync(context, route, segments); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            catch (PanelRefusalException refusal) { reply = Reply.Error(StatusCodes.Status400BadRequest, refusal.Message); }
            catch (Exception e)
            {
                LogFailed(_log, e, route.Method, path);
                reply = Reply.Error(StatusCodes.Status500InternalServerError, "The server could not finish this request.");
            }
        }
        response.StatusCode = reply.Status;
        if (reply.Body != null) await response.WriteAsJsonAsync(reply.Body, reply.Body.GetType(), Json, context.RequestAborted);
    }

    private static bool Matches(string[] pattern, string[] segments) =>
        pattern.Length == segments.Length && pattern.Zip(segments).All(p => p.First == "{}" || string.Equals(p.First, p.Second, StringComparison.Ordinal));

    private async Task<Reply> DispatchAsync(HttpContext context, Route route, string[] segments)
    {
        string? sessionId = context.Request.Cookies[SessionCookie];
        string? account = _sessions.Account(sessionId);
        // A ban ends the panel session too, also one made by another tool.
        if (account != null && await _backend.IsBannedAsync(account))
        {
            _sessions.EndAll(account);
            account = null;
        }
        if (account == null) sessionId = null;
        bool admin = account != null && _backend.IsAdmin(account);
        if (route.Access != Access.Public && account == null) return Reply.Error(StatusCodes.Status401Unauthorized, "Please log in.");
        if (route.Access == Access.Admin && !admin) return Reply.Error(StatusCodes.Status403Forbidden, "Only an admin can open this page.");
        if (route.Method == "POST")
        {
            if (!SameOrigin(context.Request) || !PanelSessions.Matches(context.Request.Headers[CsrfHeader], ExpectedCsrf(context, sessionId)))
                return Reply.Error(StatusCodes.Status403Forbidden, "This form has expired. Reload the page and try again.");
            if (!_backend.Accepting) return Reply.Error(StatusCodes.Status503ServiceUnavailable, "The server is stopping.");
        }
        string[] args = [.. route.Segments.Zip(segments).Where(p => p.First == "{}").Select(p => Uri.UnescapeDataString(p.Second))];
        return await route.Handler(new Call(context, account, admin, sessionId, args, ClientAddress(context)));
    }

    /// <summary>The token a POST must carry: bound to the session cookie, or to the visitor cookie before a login.</summary>
    private string ExpectedCsrf(HttpContext context, string? sessionId)
    {
        if (sessionId != null) return _sessions.Csrf("session", sessionId);
        string? visitor = context.Request.Cookies[VisitorCookie];
        // An empty expectation never matches a sent token.
        return string.IsNullOrEmpty(visitor) ? "\0" : _sessions.Csrf("visitor", visitor);
    }

    /// <summary>A browser names the page's origin on a POST; it must be this site's.</summary>
    private static bool SameOrigin(HttpRequest request)
    {
        string origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>The client: the connection's peer, or behind a trusted proxy the last address it forwarded.</summary>
    private IPAddress? ClientAddress(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } peer) return null;
        peer = Normalize(peer);
        if (!_proxies.Contains(peer)) return peer;
        string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        string last = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return IPAddress.TryParse(last, out var client) ? Normalize(client) : peer;
    }

    /// <summary>https to the browser: the connection itself, or a trusted proxy's X-Forwarded-Proto.</summary>
    private bool Https(HttpContext context) =>
        context.Request.IsHttps || context.Connection.RemoteIpAddress is { } peer && _proxies.Contains(Normalize(peer))
            && string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[^1].Trim(), "https", StringComparison.OrdinalIgnoreCase);

    private void SetCookie(HttpContext context, string name, string value) =>
        context.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Https(context), Path = "/", IsEssential = true,
        });

    private void ClearCookie(HttpContext context, string name) =>
        context.Response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Https(context), Path = "/",
        });

    /// <summary>The request's JSON body, or null when it is missing, too large or not the expected shape.</summary>
    private static async Task<T?> BodyAsync<T>(Call call) where T : class
    {
        var request = call.Context.Request;
        if (request.ContentLength > MaxBody) return null;
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, call.Context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBody) return null;
            buffer.Write(chunk, 0, read);
        }
        try { return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Json); }
        catch (JsonException) { return null; }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Panel {Method} {Path} failed")]
    private static partial void LogFailed(ILogger logger, Exception error, string method, string path);
}
