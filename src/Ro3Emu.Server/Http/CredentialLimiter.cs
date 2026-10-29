using System.Net;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The budget of HTTP credential checks (SDK login, character deletion) per remote address and minute
/// (<see cref="ServerOptions.LoginAttemptsPerMinute"/>): each check runs a 100 000-round PBKDF2 hash and a login may
/// register an account. A plain fixed window per address, no timers: ASP.NET's rate-limiter middleware keeps a cleanup
/// timer alive past the host, which held every disposed test host (and its game data) in memory. Loopback is exempt
/// unless <see cref="ServerOptions.LoginLimitIncludesLoopback"/>.
/// </summary>
public sealed class CredentialLimiter(IOptions<ServerOptions> options, TimeProvider time)
{
    private readonly FixedWindowCounter<IPAddress> _byAddress = new(time);

    /// <summary>False when <paramref name="remote"/> has used this minute's budget (answer 429 without hashing).</summary>
    public bool TryAcquire(IPAddress? remote)
    {
        if (remote == null) return true;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var o = options.Value;
        if (!o.LoginLimitIncludesLoopback && IPAddress.IsLoopback(remote)) return true;
        return _byAddress.TryAcquire(remote, o.LoginAttemptsPerMinute);
    }
}

/// <summary>
/// Gift code attempts (/gift-card/use/v2): <see cref="ServerOptions.GiftCodeAttemptsPerMinute"/> per remote address
/// and, separately, per account_id, so guessing codes costs a budget whichever address it comes from. Loopback is
/// exempt from the address budget as for <see cref="CredentialLimiter"/>; the account budget always applies.
/// </summary>
public sealed class GiftCodeLimiter(IOptions<ServerOptions> options, TimeProvider time)
{
    private readonly FixedWindowCounter<IPAddress> _byAddress = new(time);
    private readonly FixedWindowCounter<string> _byAccount = new(time, StringComparer.Ordinal);

    /// <summary>False when the address or the account has used this minute's budget. Each call counts against both.</summary>
    public bool TryAcquire(IPAddress? remote, string? account)
    {
        var o = options.Value;
        int budget = o.GiftCodeAttemptsPerMinute;
        if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        bool address = remote == null || (!o.LoginLimitIncludesLoopback && IPAddress.IsLoopback(remote))
            || _byAddress.TryAcquire(remote, budget);
        bool byAccount = string.IsNullOrEmpty(account) || _byAccount.TryAcquire(account, budget);
        return address && byAccount;
    }
}

/// <summary>A fixed one-minute window of attempts per key, pruned when it grows large. Thread-safe.</summary>
internal sealed class FixedWindowCounter<TKey>(TimeProvider time, IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private const int PruneAt = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<TKey, (long Window, int Count)> _byKey = new(comparer);

    /// <summary>Counts one attempt of <paramref name="key"/>; false once it is past <paramref name="perMinute"/> (at least 1).</summary>
    public bool TryAcquire(TKey key, int perMinute)
    {
        long window = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        lock (_sync)
        {
            if (_byKey.Count >= PruneAt)
                foreach (var stale in _byKey.Where(e => e.Value.Window != window).Select(e => e.Key).ToList()) _byKey.Remove(stale);
            var (seen, count) = _byKey.GetValueOrDefault(key);
            count = seen == window ? count + 1 : 1;
            _byKey[key] = (window, count);
            return count <= Math.Max(1, perMinute);
        }
    }
}
