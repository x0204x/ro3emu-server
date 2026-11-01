using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.Panel;

/// <summary>
/// Panel logins, in memory: a random id in an HttpOnly cookie maps to the account. A session ends on logout, after
/// <see cref="PanelOptions.SessionHours"/> without a request, when its account is banned or changes its password, and
/// when the server restarts. CSRF tokens are an HMAC of the cookie value under a per-process key, so an anonymous
/// visitor (login, sign-up) needs no server-side state. A login of an account with two-factor authentication waits
/// here as a challenge until its code arrives.
/// </summary>
internal sealed class PanelSessions(TimeProvider clock, TimeSpan idle)
{
    /// <summary>Past this many sessions a new login first drops the expired ones.</summary>
    private const int PruneAt = 4096;
    /// <summary>(inferred) A password login waits this long for its second factor, and takes this many wrong codes.</summary>
    private static readonly TimeSpan ChallengeLife = TimeSpan.FromMinutes(5);
    private const int ChallengeTries = 5;
    /// <summary>(inferred) Wrong second-factor codes an account may send per <see cref="FailureWindow"/>, whatever the
    /// challenge: a 6-digit code cannot be guessed at this rate.</summary>
    private const int FailuresPerWindow = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Waiting> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private sealed class Entry(string account, DateTimeOffset created, string? address, string agent)
    {
        public string Account { get; } = account;
        public DateTimeOffset Created { get; } = created;
        public string? Address { get; } = address;
        public string Agent { get; } = agent;
        public DateTimeOffset Expires { get; set; }
        public DateTimeOffset LastSeen { get; set; } = created;
    }

    private sealed class Waiting(string account, DateTimeOffset expires)
    {
        public string Account { get; } = account;
        public DateTimeOffset Expires { get; } = expires;
        public int Tries;
    }

    /// <summary>One signed-in browser as its owner sees it; <see cref="Handle"/> names it without revealing the cookie.</summary>
    public sealed record Info(string Handle, DateTimeOffset Created, DateTimeOffset LastSeen, string? Address, string Agent, bool Current);

    /// <summary>A new session of <paramref name="account"/>; the caller sets its id as the cookie.</summary>
    public string Create(string account, string? address = null, string agent = "")
    {
        var now = clock.GetUtcNow();
        if (_byId.Count >= PruneAt)
            foreach (var (id, entry) in _byId)
                if (entry.Expires <= now) _byId.TryRemove(id, out _);
        string sessionId = NewId();
        _byId[sessionId] = new Entry(account, now, address, agent.Length > 120 ? agent[..120] : agent) { Expires = now + idle };
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
        entry.LastSeen = now;
        return entry.Account;
    }

    public void End(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId)) _byId.TryRemove(sessionId, out _);
    }

    /// <summary>Ends every session of <paramref name="account"/> except <paramref name="keep"/>; how many ended.</summary>
    public int EndAll(string account, string? keep = null)
    {
        int ended = 0;
        foreach (var (id, entry) in _byId)
            if (string.Equals(entry.Account, account, StringComparison.Ordinal) && !string.Equals(id, keep, StringComparison.Ordinal)
                && _byId.TryRemove(id, out _))
                ended++;
        return ended;
    }

    /// <summary>The account's live sessions, newest first; <paramref name="current"/> is marked.</summary>
    public IReadOnlyList<Info> List(string account, string? current)
    {
        var now = clock.GetUtcNow();
        return [.. _byId.Where(p => string.Equals(p.Value.Account, account, StringComparison.Ordinal) && p.Value.Expires > now)
            .OrderByDescending(p => p.Value.LastSeen)
            .Select(p => new Info(Handle(p.Key), p.Value.Created, p.Value.LastSeen, p.Value.Address, p.Value.Agent,
                string.Equals(p.Key, current, StringComparison.Ordinal)))];
    }

    /// <summary>Ends the account's session named by <paramref name="handle"/>; false when it has none such.</summary>
    public bool EndByHandle(string account, string handle)
    {
        foreach (var (id, entry) in _byId)
            if (string.Equals(entry.Account, account, StringComparison.Ordinal) && Matches(handle, Handle(id)))
                return _byId.TryRemove(id, out _);
        return false;
    }

    private string Handle(string sessionId) => Csrf("handle", sessionId)[..16].Replace('+', '-').Replace('/', '_');

    // ---------------------------------------------------------------- second factor

    /// <summary>A password that matched, waiting for the account's second factor; the id the page sends back with it.</summary>
    public string Challenge(string account)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, waiting) in _challenges)
            if (waiting.Expires <= now) _challenges.TryRemove(id, out _);
        string challenge = NewId();
        _challenges[challenge] = new Waiting(account, now + ChallengeLife);
        return challenge;
    }

    /// <summary>The account a live challenge belongs to, counting this try; null when it is unknown, expired or used up.</summary>
    public string? TryChallenge(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !_challenges.TryGetValue(id, out var waiting)) return null;
        if (waiting.Expires <= clock.GetUtcNow() || Interlocked.Increment(ref waiting.Tries) > ChallengeTries)
        {
            _challenges.TryRemove(id, out _);
            return null;
        }
        return waiting.Account;
    }

    public void EndChallenge(string id) => _challenges.TryRemove(id, out _);

    /// <summary>False while the account sent <see cref="FailuresPerWindow"/> wrong codes in the last window.</summary>
    public bool MayTryCode(string account)
    {
        if (!_failures.TryGetValue(account, out var times)) return true;
        lock (times)
        {
            var since = clock.GetUtcNow() - FailureWindow;
            while (times.Count > 0 && times.Peek() <= since) times.Dequeue();
            return times.Count < FailuresPerWindow;
        }
    }

    public void CodeFailed(string account)
    {
        var times = _failures.GetOrAdd(account, _ => new Queue<DateTimeOffset>());
        lock (times) times.Enqueue(clock.GetUtcNow());
    }

    // ---------------------------------------------------------------- tokens

    /// <summary>The CSRF token bound to a cookie value; <paramref name="kind"/> keeps session and visitor tokens apart.</summary>
    public string Csrf(string kind, string cookie) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(kind + ":" + cookie)));

    /// <summary>Compares a sent token with the expected one in constant time.</summary>
    public static bool Matches(string? sent, string expected) =>
        sent != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected));

    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
