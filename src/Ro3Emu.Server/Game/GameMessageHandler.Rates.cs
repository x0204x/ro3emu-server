namespace Ro3Emu.Server.Game;

public sealed partial class GameMessageHandler
{
    private Dictionary<uint, (double PerSecond, double Burst)>? _limits;

    /// <summary>
    /// Whether the session is within its message budgets (AntiCheatOptions): the shared one and, for a message with its own,
    /// that one. A message over budget is dropped unanswered, before it reserves anything, and counted
    /// (CheatMonitor.Flood). The client's own pace is far below the budgets (inferred); the heartbeat is never limited.
    /// </summary>
    private bool WithinRate(GameSession session, uint msgId)
    {
        var settings = options.Value.AntiCheat;
        if (msgId == MsgId.Heartbeat) return true;
        var rates = session.MessageRates ??= new SessionRates(settings.MessagesPerSecond > 0
            ? new RateBucket(settings.MessagesPerSecond, Math.Max(1, settings.MessageBurst)) : null);
        long now = Environment.TickCount64;
        bool within = true;
        if (Limits(settings).TryGetValue(msgId, out var own))
        {
            if (!rates.ById.TryGetValue(msgId, out var bucket)) rates.ById[msgId] = bucket = new RateBucket(own.PerSecond, own.Burst);
            within = bucket.TryTake(now);
        }
        if (within && rates.All is { } all) within = all.TryTake(now);
        if (!within) cheats.Refused(session, CheatMonitor.Flood, $"msg {msgId} {catalog.Name(msgId)} over its rate");
        return within;
    }

    /// <summary>AntiCheatOptions.MessageLimits parsed once; entries that do not parse are left out.</summary>
    private Dictionary<uint, (double PerSecond, double Burst)> Limits(AntiCheatOptions settings)
    {
        if (_limits is { } parsed) return parsed;
        parsed = [];
        foreach (var (key, value) in settings.MessageLimits)
            if (uint.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint id)
                && RateBucket.Parse(value) is { } bucket)
                parsed[id] = (bucket.PerSecond, bucket.Burst);
        return _limits = parsed;
    }
}

/// <summary>A session's message budgets (GameMessageHandler.WithinRate), used on the game loop only.</summary>
internal sealed class SessionRates(RateBucket? all)
{
    internal RateBucket? All { get; } = all;
    internal Dictionary<uint, RateBucket> ById { get; } = [];
}
