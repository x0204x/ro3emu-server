using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>The server's metrics for the operator's tools: the same guard as <see cref="OpsEndpoints"/> (the operator token).</summary>
public static class MetricsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/ro3emu/metrics", (ServerMetrics metrics, SessionCensus census, PublicLineManager lines) =>
            Results.Json(metrics.Snapshot(census.Current, lines))).RequireOperator();
        app.MapPost("/ro3emu/metrics/reset", (ServerMetrics metrics) =>
            Results.Json(new { epoch = metrics.Reset() })).RequireOperator();
        // Slowest game messages by total loop time (MessageRoutes, audit F4.1).
        app.MapGet("/ro3emu/metrics/routes", (GameMessageHandler handler) =>
            Results.Json(handler.Routes.Snapshot(40))).RequireOperator();
        // Login snapshots (C/D 14/16), lazy position (C/D 18) and the owner vector cache (C/D 16).
        app.MapGet("/ro3emu/metrics/persistence", (Accounts.AccountStore accounts,
            Game.Persistence.PositionPersistence positions, Game.Persistence.SettingsPersistence settings,
            IEnumerable<IHostedService> hosted) => Results.Json(new
            {
                snapshots = accounts.Snapshots.Snapshot(), skippedWrites = accounts.Snapshots.SkippedWrites,
                positions = positions.Snapshot(), settings = settings.Snapshot(), vectorCacheHits = accounts.VectorCacheHits, vectorCacheRetries = accounts.VectorCacheRetries,
                unchangedSaves = accounts.Snapshots.PersistedSkips, backup = hosted.OfType<Accounts.DatabaseBackup>().SingleOrDefault()?.Snapshot(), loopWaits = accounts.AccessMetrics.LoopWaits(40)
            })).RequireOperator();
        app.MapGet("/ro3emu/metrics/public-key", (GateCrypto crypto) =>
            Results.Text(crypto.PublicKeyPem, "text/plain")).RequireOperator();
    }
}
