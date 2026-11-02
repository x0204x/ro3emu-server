using System.Net;
using Ro3Emu.Panel;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The operator's tools on the HTTP port (docs/systems/security.md): GM commands, a clean stop, who is online and the
/// in-game bug reports, plus <see cref="Diagnostics.MetricsEndpoints"/> under the same <see cref="RequireOperator{TBuilder}"/>.
/// Each answers only a local tool: a loopback peer, a Host naming this machine, no forwarding header and the data dir's
/// <see cref="OperatorToken"/>; anything else gets the 404 of an unknown path.
/// </summary>
public static class OpsEndpoints
{
    public static void Map(WebApplication app)
    {
        // Run a GM command as an online player: POST /ro3emu/gm?account=tester body "!warp 10111 150 190".
        app.MapPost("/ro3emu/gm", async (HttpContext context, SessionRegistry sessions, GmCommands gm, GameLoop loop) =>
        {
            var account = context.Request.Query["account"].ToString();
            using var reader = new StreamReader(context.Request.Body);
            var line = (await reader.ReadToEndAsync()).Trim();
            if (!line.StartsWith('!')) line = "!" + line;
            return await loop.InvokeAsync(async () =>
            {
                var session = sessions.All().FirstOrDefault(s => string.Equals(s.Account, account, StringComparison.OrdinalIgnoreCase));
                if (session?.Role == null) return Results.BadRequest("not in game");
                await gm.RunAsync(session, line, loopbackTool: true);
                return Results.Ok("done");
            });
        }).RequireOperator();
        // In-game bug reports (docs/guide/feedback.md): GET /ro3emu/reports?since=<id or ISO time>&limit=&format=text
        app.MapGet("/ro3emu/reports", (HttpContext context, FeedbackService feedback) =>
        {
            var query = context.Request.Query;
            long afterId = 0;
            DateTimeOffset? since = null;
            string sinceText = query["since"].ToString().Trim();
            if (sinceText.Length > 0)
            {
                if (long.TryParse(sinceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)) afterId = id;
                else if (DateTimeOffset.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) since = time;
                else return Results.BadRequest("since: a report id or an ISO-8601 time");
            }
            int limit = int.TryParse(query["limit"], out int l) ? Math.Clamp(l, 1, 1000) : 200;
            var reports = feedback.Store.Read(afterId, since, limit);
            return string.Equals(query["format"], "text", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(FeedbackStore.Format(reports), "text/plain; charset=utf-8")
                : Results.Json(reports, FeedbackStore.Json);
        }).RequireOperator();
        // A clean stop for RO3Emu.exe: the same shutdown as Ctrl+C, the loop drains and the database closes.
        app.MapPost("/ro3emu/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok("stopping");
        }).RequireOperator();
        // Who is online, for the admin: read on the loop, where the sessions change.
        app.MapGet("/ro3emu/online", async (SessionRegistry sessions, GameLoop loop) =>
        {
            var online = await loop.InvokeAsync(() => Task.FromResult(sessions.All().Select(s => new
            {
                account = s.Account,
                role = s.Role?.Name,
                scene = s.Role == null ? (long?)null : s.SceneEntry,
                x = s.Position.X,
                z = s.Position.Z,
                remote = s.Channel.Remote,
            }).ToArray()));
            return Results.Json(online);
        }).RequireOperator();
    }

    /// <summary>The endpoint answers only a local tool with the operator token (<see cref="Operator"/>); else 404.</summary>
    public static TBuilder RequireOperator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
            Operator(context.HttpContext, context.HttpContext.RequestServices.GetRequiredService<OperatorToken>())
                ? await next(context)
                : Results.NotFound());

    /// <summary>
    /// A local tool of the operator: the peer is loopback, the Host names this machine (no DNS rebinding), no proxy
    /// forwarded the request (a proxy on the same machine is loopback too) and it carries the operator token.
    /// </summary>
    public static bool Operator(HttpContext context, OperatorToken token) =>
        context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer)
        && ClientAddresses.LoopbackHost(context.Request) && !ClientAddresses.IsForwarded(context.Request)
        && token.Matches(context.Request.Headers[OperatorToken.Header].ToString() is { Length: > 0 } sent ? sent : null);
}
