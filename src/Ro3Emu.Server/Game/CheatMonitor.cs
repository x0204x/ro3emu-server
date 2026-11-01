using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Requests the server refused (or, in a report-only mode, let pass) because no unmodified client sends them: a walk
/// faster than the speed attribute, a cannon shot beyond its reach, a quest step reported from elsewhere, a flood. Each one
/// is counted per account, character, kind and hour, written to anticheat_refusal every
/// <see cref="AntiCheatOptions.FlushSeconds"/>, and logged as a warning ("Anticheat ...") the first times and then every
/// <see cref="LogEvery"/>th. The web panel's Suspicious activity page reads the counts (docs/systems/anticheat.md).
/// Also keeps the EXP characters gain per hour (anticheat_exp), which the economy ledger does not record.
/// </summary>
public sealed partial class CheatMonitor(IOptions<ServerOptions> options, AccountStore accounts, TimeProvider clock,
    ILogger<CheatMonitor> log) : BackgroundService
{
    /// <summary>The kinds of refusal (anticheat_refusal.kind).</summary>
    public const string Walk = "walk", Flight = "flight", Cannon = "cannon", QuestArea = "quest-area",
        QuestInteract = "quest-interact", Flood = "flood", RoleLimit = "role-limit", Chat = "chat", Payload = "payload";

    /// <summary>The first refusals of an account and kind are logged one by one, then every this many.</summary>
    internal const int LogFirst = 5, LogEvery = 50;

    private const long HourMs = 3_600_000;
    private readonly Lock _sync = new();
    private readonly Dictionary<(string Account, long Role, string Kind, long Hour), (long Count, long LastUtc, string Detail)> _refusals = [];
    private readonly Dictionary<(long Role, long Hour), (string Account, long Base, long Job)> _exp = [];
    private readonly Dictionary<(string Account, string Kind), long> _logged = [];

    /// <summary>Counts and logs one refused request of <paramref name="session"/>.</summary>
    public void Refused(GameSession session, string kind, string detail)
    {
        string account = session.Account ?? "";
        long role = session.Role?.Id ?? 0, now = clock.GetUtcNow().ToUnixTimeMilliseconds(), count;
        lock (_sync)
        {
            var key = (account, role, kind, now / HourMs);
            var old = _refusals.GetValueOrDefault(key);
            _refusals[key] = (old.Count + 1, now, detail);
            count = _logged[(account, kind)] = _logged.GetValueOrDefault((account, kind)) + 1;
        }
        if (count <= LogFirst || count % LogEvery == 0)
            LogRefused(log, kind, account, role, session.Role?.Name ?? "", detail, count);
    }

    /// <summary>Base and job EXP <paramref name="session"/>'s character was just credited.</summary>
    public void ExpGained(GameSession session, long baseExp, long jobExp)
    {
        if (session.Role is not { } role || baseExp <= 0 && jobExp <= 0) return;
        long hour = clock.GetUtcNow().ToUnixTimeMilliseconds() / HourMs;
        lock (_sync)
        {
            var old = _exp.GetValueOrDefault((role.Id, hour));
            _exp[(role.Id, hour)] = (session.Account ?? role.Account, old.Base + Math.Max(0, baseExp), old.Job + Math.Max(0, jobExp));
        }
    }

    /// <summary>Refusals of this kind <paramref name="account"/> had since the server started.</summary>
    internal long LoggedCount(string account, string kind)
    {
        lock (_sync) return _logged.GetValueOrDefault((account, kind));
    }

    /// <summary>Writes the pending counts (the panel calls it before it reads them).</summary>
    public async Task FlushAsync()
    {
        AntiCheatRefusalCount[] refusals;
        AntiCheatExpCount[] exp;
        lock (_sync)
        {
            refusals = [.. _refusals.Select(p => new AntiCheatRefusalCount(p.Key.Account, p.Key.Role, p.Key.Kind, p.Key.Hour,
                p.Value.Count, p.Value.LastUtc, p.Value.Detail))];
            exp = [.. _exp.Select(p => new AntiCheatExpCount(p.Key.Role, p.Key.Hour, p.Value.Account, p.Value.Base, p.Value.Job))];
            _refusals.Clear();
            _exp.Clear();
        }
        if (refusals.Length == 0 && exp.Length == 0) return;
        long keep = clock.GetUtcNow().ToUnixTimeMilliseconds() / HourMs - Math.Max(1, options.Value.AntiCheat.KeepDays) * 24L;
        try { await accounts.WriteAsync<int>(new AntiCheatFlushPlan(refusals, exp, keep)); }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            LogFlushFailed(log, e);
            lock (_sync)
            {
                foreach (var r in refusals)
                {
                    var key = (r.Account, r.RoleId, r.Kind, r.Hour);
                    var now = _refusals.GetValueOrDefault(key);
                    _refusals[key] = (now.Count + r.Count, Math.Max(now.LastUtc, r.LastUtc), now.Detail ?? r.LastDetail);
                }
                foreach (var e2 in exp)
                {
                    var now = _exp.GetValueOrDefault((e2.RoleId, e2.Hour));
                    _exp[(e2.RoleId, e2.Hour)] = (e2.Account, now.Base + e2.BaseExp, now.Job + e2.JobExp);
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.AntiCheat.FlushSeconds)), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
            await FlushAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Anticheat {Kind} account {Account} role {Role} {Name}: {Detail} (#{Count} of this kind since start)")]
    private static partial void LogRefused(ILogger logger, string kind, string account, long role, string name, string detail, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anticheat counts could not be written; kept for the next flush")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);
}

/// <summary>
/// A token bucket: <see cref="PerSecond"/> tokens a second up to <see cref="Burst"/>, full at first. Not thread safe: a
/// session's buckets are used on the game loop only.
/// </summary>
internal sealed class RateBucket(double perSecond, double burst)
{
    private double _tokens = -1;
    private long _lastMs;

    internal double PerSecond => perSecond;
    internal double Burst => burst;

    /// <summary>Takes one token at <paramref name="nowMs"/> (a monotonic clock); false when none is left.</summary>
    internal bool TryTake(long nowMs)
    {
        if (_tokens < 0) (_tokens, _lastMs) = (burst, nowMs);
        else if (nowMs > _lastMs) _tokens = Math.Min(burst, _tokens + (nowMs - _lastMs) / 1000.0 * perSecond);
        _lastMs = Math.Max(_lastMs, nowMs);
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }

    /// <summary>"perSecond/burst" (invariant culture), or null when it does not parse or is not positive.</summary>
    internal static RateBucket? Parse(string? text)
    {
        var parts = (text ?? "").Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rate)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double burst)
            && rate > 0 && burst >= 1 ? new RateBucket(rate, burst) : null;
    }
}
