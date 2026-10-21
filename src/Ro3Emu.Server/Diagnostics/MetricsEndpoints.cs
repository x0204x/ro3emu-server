using System.Net;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Diagnostics;

public static class MetricsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/ro3emu/metrics", (HttpContext context, ServerMetrics metrics, SessionCensus census, PublicLineManager lines) =>
            Local(context) ? Results.Json(metrics.Snapshot(census.Current, lines)) : Results.NotFound());
        app.MapPost("/ro3emu/metrics/reset", (HttpContext context, ServerMetrics metrics) =>
            Local(context) ? Results.Json(new { epoch = metrics.Reset() }) : Results.NotFound());
        // Slowest game messages by total loop time (MessageRoutes, audit F4.1).
        app.MapGet("/ro3emu/metrics/routes", (HttpContext context, GameMessageHandler handler) =>
            Local(context) ? Results.Json(handler.Routes.Snapshot(40)) : Results.NotFound());
        // Login snapshots (C/D 14/16), lazy position (C/D 18) and the owner vector cache (C/D 16).
        app.MapGet("/ro3emu/metrics/persistence", (HttpContext context, Accounts.AccountStore accounts,
            Game.Persistence.PositionPersistence positions, Game.Persistence.SettingsPersistence settings,
            IEnumerable<IHostedService> hosted) => Local(context)
            ? Results.Json(new
            {
                snapshots = accounts.Snapshots.Snapshot(), skippedWrites = accounts.Snapshots.SkippedWrites,
                positions = positions.Snapshot(), settings = settings.Snapshot(), vectorCacheHits = accounts.VectorCacheHits, vectorCacheRetries = accounts.VectorCacheRetries,
                unchangedSaves = accounts.Snapshots.PersistedSkips, backup = hosted.OfType<Accounts.DatabaseBackup>().SingleOrDefault()?.Snapshot(), loopWaits = accounts.AccessMetrics.LoopWaits(40)
            })
            : Results.NotFound());
        app.MapGet("/ro3emu/metrics/public-key", (HttpContext context, GateCrypto crypto) =>
            Local(context) ? Results.Text(crypto.PublicKeyPem, "text/plain") : Results.NotFound());
    }
    private static bool Local(HttpContext context) => context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
}
