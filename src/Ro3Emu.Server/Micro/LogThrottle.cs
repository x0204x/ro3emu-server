namespace Ro3Emu.Server.Micro;

/// <summary>
/// At most one log line per <paramref name="intervalMs"/> for a message anyone on the internet can trigger (an unknown
/// ticket, a broken handshake): the lines left out are counted and reported with the next one. No timer.
/// </summary>
internal sealed class LogThrottle(long intervalMs = 10_000)
{
    private long _nextMs = long.MinValue;
    private long _suppressed;

    /// <summary>True when this occurrence may be logged; <paramref name="suppressed"/> is how many were left out since the last.</summary>
    public bool Allow(out long suppressed)
    {
        long now = Environment.TickCount64;
        long next = Interlocked.Read(ref _nextMs);
        if (now < next || Interlocked.CompareExchange(ref _nextMs, now + intervalMs, next) != next)
        {
            Interlocked.Increment(ref _suppressed);
            suppressed = 0;
            return false;
        }
        suppressed = Interlocked.Exchange(ref _suppressed, 0);
        return true;
    }
}
