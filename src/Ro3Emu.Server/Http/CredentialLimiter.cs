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
    private const int PruneAt = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, (long Window, int Count)> _byAddress = new();

    /// <summary>False when <paramref name="remote"/> has used this minute's budget (answer 429 without hashing).</summary>
    public bool TryAcquire(IPAddress? remote)
    {
        if (remote == null) return true;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var o = options.Value;
        if (!o.LoginLimitIncludesLoopback && IPAddress.IsLoopback(remote)) return true;
        long window = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        lock (_sync)
        {
            if (_byAddress.Count >= PruneAt)
                foreach (var stale in _byAddress.Where(e => e.Value.Window != window).Select(e => e.Key).ToList()) _byAddress.Remove(stale);
            var (seen, count) = _byAddress.GetValueOrDefault(remote);
            count = seen == window ? count + 1 : 1;
            _byAddress[remote] = (window, count);
            return count <= Math.Max(1, o.LoginAttemptsPerMinute);
        }
    }
}
