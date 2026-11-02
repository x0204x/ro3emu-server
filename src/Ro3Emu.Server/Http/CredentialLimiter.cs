using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Ro3Emu.Panel;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The budget of HTTP credential checks (SDK login, character deletion, panel logins) per client address (an IPv6 one by
/// its /64 network) and minute
/// (<see cref="ServerOptions.LoginAttemptsPerMinute"/>): each check runs a 100 000-round PBKDF2 hash and a login may
/// register an account. A plain fixed window per address, no timers: ASP.NET's rate-limiter middleware keeps a cleanup
/// timer alive past the host, which held every disposed test host (and its game data) in memory. The address is the
/// client of <see cref="ClientAddresses"/> (a trusted proxy's forwarded one). Loopback is exempt only for a local tool:
/// unless <see cref="ServerOptions.LoginLimitIncludesLoopback"/>, and never for a forwarded request or on a server others
/// reach (<see cref="ServerOptions.LocalOnly"/>), since a proxy or a tunnel on the same machine is loopback too.
/// </summary>
public sealed class CredentialLimiter(IOptions<ServerOptions> options, TimeProvider time)
{
    private readonly FixedWindowCounter<IPAddress> _byAddress = new(time);
    private readonly ClientAddresses _clients = new(options.Value.Panel.TrustedProxies);

    /// <summary>False when the request's client has used this minute's budget (answer 429 without hashing).</summary>
    public bool TryAcquire(HttpContext context) => TryAcquire(_clients.Of(context));

    /// <summary>The address of the request's client: a trusted proxy's forwarded one, else the peer.</summary>
    public IPAddress? ClientAddress(HttpContext context) => _clients.Of(context).Address;

    /// <summary>False when <paramref name="client"/> has used this minute's budget (answer 429 without hashing).</summary>
    public bool TryAcquire(RequestClient client)
    {
        if (client.Address is not { } remote) return true;
        var o = options.Value;
        if (LoopbackExempt(o, client)) return true;
        return _byAddress.TryAcquire(ClientAddresses.Network(remote), o.LoginAttemptsPerMinute);
    }

    /// <summary>A local tool on loopback, not a proxy in front of the server: the address budgets let it through.</summary>
    internal static bool LoopbackExempt(ServerOptions o, RequestClient client) =>
        !o.LoginLimitIncludesLoopback && client is { Address: { } a, Forwarded: false } && IPAddress.IsLoopback(a) && o.LocalOnly();
}

/// <summary>
/// Gift code attempts (/gift-card/use/v2): <see cref="ServerOptions.GiftCodeAttemptsPerMinute"/> per client address
/// and, separately, per account_id, so guessing codes costs a budget whichever address it comes from. Loopback is
/// exempt from the address budget as for <see cref="CredentialLimiter"/>; the account budget always applies.
/// </summary>
public sealed class GiftCodeLimiter(IOptions<ServerOptions> options, TimeProvider time)
{
    private readonly FixedWindowCounter<IPAddress> _byAddress = new(time);
    private readonly FixedWindowCounter<string> _byAccount = new(time, StringComparer.Ordinal);
    private readonly ClientAddresses _clients = new(options.Value.Panel.TrustedProxies);

    /// <summary>The address budget of the request's client (no account yet: the body is read later).</summary>
    public bool TryAcquire(HttpContext context) => TryAcquire(_clients.Of(context), null);

    /// <summary>False when the address or the account has used this minute's budget. Each call counts against both.</summary>
    public bool TryAcquire(RequestClient client, string? account)
    {
        var o = options.Value;
        int budget = o.GiftCodeAttemptsPerMinute;
        bool address = client.Address is not { } remote || CredentialLimiter.LoopbackExempt(o, client)
            || _byAddress.TryAcquire(ClientAddresses.Network(remote), budget);
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
