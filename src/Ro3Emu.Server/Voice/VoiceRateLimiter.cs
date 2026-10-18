namespace Ro3Emu.Server.Voice;

/// <summary>
/// Per-connection token bucket, one for Audio frames (<see cref="VoiceOptions.FramesPerSecond"/> /
/// <see cref="VoiceOptions.Burst"/>) and one for control frames (<see cref="VoiceOptions.ControlPerSecond"/> /
/// <see cref="VoiceOptions.ControlBurst"/>): framesPerSecond tokens refill every second, up to framesPerSecond +
/// burst banked. A frame with no token left is dropped, not queued. <see cref="RecordAndCheckFlood"/> separately tracks a sustained flood (more than 3x the frame rate
/// averaged over a trailing 5-second window) so the caller can disconnect instead of merely throttling: a short
/// burst (its total over 5 s still under the threshold) is only throttled, a client that keeps sending far above
/// its budget for the whole window is dropped.
///
/// Timed with Environment.TickCount64 (real elapsed time), not an injected TimeProvider: this has to track
/// real network pace regardless of whatever a test does with the server's business-logic clock (ticket expiry).
/// </summary>
public sealed class VoiceRateLimiter(int framesPerSecond, int burst)
{
    private const int FloodMultiplier = 3;
    private const long FloodWindowMs = 5000;

    private readonly double _capacity = framesPerSecond + burst;
    private double _tokens = framesPerSecond + burst;
    private long _lastRefillMs = Environment.TickCount64;

    private readonly Queue<long> _recent = new();
    private readonly long _floodThreshold = (long)framesPerSecond * FloodMultiplier * (FloodWindowMs / 1000);

    /// <summary>True when the frame may be forwarded; false when the bucket is empty (drop, do not disconnect).</summary>
    public bool TryTake()
    {
        Refill();
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }

    /// <summary>Call once per Audio frame received (whether or not <see cref="TryTake"/> let it through): true once
    /// the trailing 5-second count crosses 3x the configured frame rate, meaning the caller should disconnect.</summary>
    public bool RecordAndCheckFlood()
    {
        long now = Environment.TickCount64;
        _recent.Enqueue(now);
        while (_recent.Count > 0 && now - _recent.Peek() > FloodWindowMs) _recent.Dequeue();
        return _recent.Count > _floodThreshold;
    }

    private void Refill()
    {
        long now = Environment.TickCount64;
        double elapsedSeconds = (now - _lastRefillMs) / 1000.0;
        if (elapsedSeconds <= 0) return;
        _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * framesPerSecond);
        _lastRefillMs = now;
    }
}
