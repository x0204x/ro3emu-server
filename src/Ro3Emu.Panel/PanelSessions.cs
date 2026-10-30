using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.Panel;

/// <summary>
/// Panel logins, in memory: a random id in an HttpOnly cookie maps to the account. A session ends on logout, after
/// <see cref="PanelOptions.SessionHours"/> without a request, when its account is banned or changes its password, and
/// when the server restarts. CSRF tokens are an HMAC of the cookie value under a per-process key, so an anonymous
/// visitor (login, sign-up) needs no server-side state.
/// </summary>
internal sealed class PanelSessions(TimeProvider clock, TimeSpan idle)
{
    /// <summary>Past this many sessions a new login first drops the expired ones.</summary>
    private const int PruneAt = 4096;

    private readonly ConcurrentDictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private sealed class Entry(string account, DateTimeOffset expires)
    {
        public string Account { get; } = account;
        public DateTimeOffset Expires { get; set; } = expires;
    }

    /// <summary>A new session of <paramref name="account"/>; the caller sets its id as the cookie.</summary>
    public string Create(string account)
    {
        var now = clock.GetUtcNow();
        if (_byId.Count >= PruneAt)
            foreach (var (id, entry) in _byId)
                if (entry.Expires <= now) _byId.TryRemove(id, out _);
        string sessionId = NewId();
        _byId[sessionId] = new Entry(account, now + idle);
        return sessionId;
    }

    /// <summary>The session's account, extending it; null when the id is unknown or expired.</summary>
    public string? Account(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_byId.TryGetValue(sessionId, out var entry)) return null;
        var now = clock.GetUtcNow();
        if (entry.Expires <= now)
        {
            _byId.TryRemove(sessionId, out _);
            return null;
        }
        entry.Expires = now + idle;
        return entry.Account;
    }

    public void End(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId)) _byId.TryRemove(sessionId, out _);
    }

    /// <summary>Ends every session of <paramref name="account"/> except <paramref name="keep"/>.</summary>
    public void EndAll(string account, string? keep = null)
    {
        foreach (var (id, entry) in _byId)
            if (string.Equals(entry.Account, account, StringComparison.Ordinal) && !string.Equals(id, keep, StringComparison.Ordinal))
                _byId.TryRemove(id, out _);
    }

    /// <summary>The CSRF token bound to a cookie value; <paramref name="kind"/> keeps session and visitor tokens apart.</summary>
    public string Csrf(string kind, string cookie) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(kind + ":" + cookie)));

    /// <summary>Compares a sent token with the expected one in constant time.</summary>
    public static bool Matches(string? sent, string expected) =>
        sent != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected));

    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
