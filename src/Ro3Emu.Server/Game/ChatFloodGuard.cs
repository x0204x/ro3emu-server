using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Chat flood protection (Ro3Emu:ChatFlood, off by default). (inferred) The original server's chat limits are unknown;
/// the client only keeps its own per-channel cooldown (ChatChannelConfig _iCD, tip 34047 "You're sending messages too
/// frequently"), which a modified client skips. Per account: lines per window on the open channels and, looser, on
/// whispers, party, guild and friend groups; a line repeating one sent to the same channel within
/// <see cref="ChatFloodOptions.DuplicateSeconds"/> is dropped; going past a budget mutes the account for
/// <see cref="ChatFloodOptions.MuteSeconds"/>. Every line counts, dropped ones included, so a client that keeps
/// resending is muted. Kept by account, so a relog does not lift a mute. Thread-safe.
/// </summary>
public sealed class ChatFloodGuard(IOptions<ServerOptions> options, TimeProvider time)
{
    public enum Verdict { Pass, Duplicate, Muted, MuteStarted }

    /// <summary>Accounts kept before idle ones are dropped.</summary>
    private const int PruneAt = 4096;
    /// <summary>Recent lines kept per account for the duplicate check.</summary>
    private const int MaxRecentLines = 32;

    private readonly object _sync = new();
    private readonly Dictionary<string, State> _byAccount = new(StringComparer.Ordinal);

    private sealed class State
    {
        public readonly Queue<long> Open = new(), Private = new();
        public readonly Queue<(long At, string Channel, string Text)> Recent = new();
        public long MutedUntil;
    }

    /// <summary>
    /// Counts one line of <paramref name="account"/> to <paramref name="channel"/> (a group id or a whisper's role id);
    /// <paramref name="open"/> picks the stricter budget. The time is when the mute ends (for Muted and MuteStarted).
    /// </summary>
    public (Verdict Verdict, DateTimeOffset MutedUntil) Check(string account, string channel, bool open, string? text)
    {
        var o = options.Value.ChatFlood;
        if (!o.Enabled) return (Verdict.Pass, default);
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_sync)
        {
            if (_byAccount.Count >= PruneAt) Prune(now, o);
            if (!_byAccount.TryGetValue(account, out var s)) _byAccount[account] = s = new();
            if (s.MutedUntil > now) return (Verdict.Muted, DateTimeOffset.FromUnixTimeMilliseconds(s.MutedUntil));
            var lines = open ? s.Open : s.Private;
            Trim(lines, now - Math.Max(1, o.WindowSeconds) * 1000L);
            lines.Enqueue(now);
            if (lines.Count > Math.Max(1, open ? o.OpenLines : o.PrivateLines))
            {
                s.MutedUntil = now + Math.Max(1, o.MuteSeconds) * 1000L;
                s.Open.Clear();
                s.Private.Clear();
                return (Verdict.MuteStarted, DateTimeOffset.FromUnixTimeMilliseconds(s.MutedUntil));
            }
            return Repeats(s, now, o, channel, Normalize(text)) ? (Verdict.Duplicate, default) : (Verdict.Pass, default);
        }
    }

    /// <summary>True when the same text went to the same channel within the duplicate window; else remembers it.</summary>
    private static bool Repeats(State s, long now, ChatFloodOptions o, string channel, string text)
    {
        if (o.DuplicateSeconds <= 0 || text.Length == 0) return false;
        long since = now - o.DuplicateSeconds * 1000L;
        while (s.Recent.Count > 0 && s.Recent.Peek().At <= since) s.Recent.Dequeue();
        if (s.Recent.Any(r => r.Channel == channel && r.Text == text)) return true;
        s.Recent.Enqueue((now, channel, text));
        while (s.Recent.Count > MaxRecentLines) s.Recent.Dequeue();
        return false;
    }

    /// <summary>Letter case and runs of white space do not make a line new.</summary>
    internal static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static void Trim(Queue<long> lines, long since)
    {
        while (lines.Count > 0 && lines.Peek() <= since) lines.Dequeue();
    }

    private void Prune(long now, ChatFloodOptions o)
    {
        long window = Math.Max(Math.Max(1, o.WindowSeconds), o.DuplicateSeconds) * 1000L;
        foreach (string account in _byAccount.Where(e => e.Value.MutedUntil <= now && Idle(e.Value, now - window)).Select(e => e.Key).ToList())
            _byAccount.Remove(account);

        static bool Idle(State s, long since) =>
            s.Open.All(t => t <= since) && s.Private.All(t => t <= since) && s.Recent.All(r => r.At <= since);
    }
}
