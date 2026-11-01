namespace Ro3Emu.Server.Game;

/// <summary>
/// How far a client may still report its player walking (SceneService.AcceptWalkAsync, WorldCoreService flight). The
/// allowance grows by the speed times <see cref="Tolerance"/> every second, up to <see cref="CapSeconds"/> of that, and
/// each accepted walk spends its distance. A burst of walks after a lag spike spends what the lag saved up; a client
/// that reports more distance than its speed covers runs dry, however it splits it into messages. (inferred) The
/// original's own rule is not in the client: the tolerance and the cap are the 1.5 and 5 s the walk check used per walk.
/// </summary>
internal sealed class WalkAllowance
{
    /// <summary>Speed factor allowed for latency jitter, and seconds of it the allowance can save up.</summary>
    internal const double Tolerance = 1.5, CapSeconds = 5;

    /// <summary>What a held allowance keeps: the step a client takes before a stun or a cast reaches it.</summary>
    internal const double FloorMetres = 1.5;

    private double _metres = -1;
    private DateTime _since;
    private DateTime _heldUntil;

    /// <summary>The distance available at <paramref name="now"/> for <paramref name="metresPerSecond"/>.</summary>
    internal double Available(DateTime now, double metresPerSecond)
    {
        Refill(now, metresPerSecond);
        return _metres;
    }

    /// <summary>Spends <paramref name="metres"/> when available; false (nothing spent) when not.</summary>
    internal bool TrySpend(DateTime now, double metresPerSecond, double metres)
    {
        Refill(now, metresPerSecond);
        if (metres > _metres + 1e-3) return false;
        _metres = Math.Max(0, _metres - metres);
        return true;
    }

    /// <summary>Spends what is there (a walk let through in report-only mode).</summary>
    internal void Drain(DateTime now, double metresPerSecond, double metres)
    {
        Refill(now, metresPerSecond);
        _metres = Math.Max(0, _metres - metres);
    }

    /// <summary>
    /// The player may not walk until <paramref name="until"/> (a stun, a cast that roots it): nothing is saved up meanwhile,
    /// and what is left is cut to the walk in progress (<paramref name="walkingSeconds"/> of it) plus <see cref="FloorMetres"/>.
    /// </summary>
    internal void Hold(DateTime now, DateTime until, double metresPerSecond, double walkingSeconds)
    {
        Refill(now, metresPerSecond);
        _metres = Math.Min(_metres, metresPerSecond * Tolerance * Math.Max(0, walkingSeconds) + FloorMetres);
        if (until > _heldUntil) _heldUntil = until;
    }

    /// <summary>The hold ends early (the cast was cancelled).</summary>
    internal void Release(DateTime now)
    {
        if (_heldUntil > now) _heldUntil = now;
    }

    private void Refill(DateTime now, double metresPerSecond)
    {
        double cap = metresPerSecond * Tolerance * CapSeconds + FloorMetres;
        if (_metres < 0) { _metres = cap; _since = now; return; }
        var from = _since > _heldUntil ? _since : _heldUntil;
        if (now > from) _metres = Math.Min(cap, _metres + (now - from).TotalSeconds * metresPerSecond * Tolerance);
        if (now > _since) _since = now;
    }
}
