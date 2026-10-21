using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Public-map admission. MapConfig supplies creation permission and both thresholds;
/// least-loaded selection, FIFO fallback and retirement are inferred server policies.</summary>
public sealed class PublicLineManager(GameData data, World world)
{
    public sealed record Reservation(long Token, RuntimeMapKey Map);
    public sealed record LineInfo(RuntimeMapKey Map, int Players, int Reservations, int SoftLimit, int HardLimit)
    {
        public int Load => Players + Reservations;
    }
    private sealed class Line(RuntimeMapKey map) { public RuntimeMapKey Map = map; public bool Closing; }
    private readonly object _gate = new();
    private readonly Dictionary<RuntimeMapKey, Line> _lines = new();
    private readonly Dictionary<long, int> _lastLine = new();
    private readonly Dictionary<GameSession, Reservation> _reservations = new();
    private long _token;
    private bool _sweeping;
    public List<Func<RuntimeMapKey, bool>> HasPendingWork { get; } = new();
    public List<Func<RuntimeMapKey, Task>> Retiring { get; } = new();

    public (int Soft, int Hard, bool Create) Limits(long scene)
    {
        var row = data.Row("MapConfig", scene);
        // Lua_CFG_MapConfigBase.GetRoleCntLimit: absent/zero => 1000.
        int hard = row is { } r ? (int)Math.Clamp(GameData.Int(r, "_iRoleCntLimit"), 0, int.MaxValue) : 0;
        if (hard == 0) hard = 1000;
        int soft = row is { } r2 ? (int)Math.Clamp(GameData.Int(r2, "_iRoleAlert"), 0, hard) : 0;
        return (soft > 0 ? soft : hard, hard, row is { } r3 && GameData.Int(r3, "_iAllowCreateLine") == 1);
    }

    private Line AddLine(long scene)
    {
        int number = checked(_lastLine.GetValueOrDefault(scene) + 1);
        _lastLine[scene] = number;
        var line = new Line(RuntimeMapKey.Public(scene, number));
        _lines.Add(line.Map, line);
        return line;
    }

    /// <summary>Restore an active offline stall's opening line after restart (inferred).
    /// This is an economic owner pin, never a player's saved login location.</summary>
    public bool RestorePinnedLine(long scene, int number)
    {
        lock (_gate)
        {
            if (number < 1 || number > 1 && !Limits(scene).Create) return false;
            if (!_lastLine.ContainsKey(scene)) AddLine(scene);
            var key = RuntimeMapKey.Public(scene, number);
            if (_lines.TryGetValue(key, out var existing)) return !existing.Closing;
            _lines.Add(key, new Line(key));
            _lastLine[scene] = Math.Max(_lastLine[scene], number);
            return true;
        }
    }

    private int Load(Line line, GameSession? except = null) => world.PlayersIn(line.Map.WorldScene)
        .Concat(_reservations.Where(p => p.Value.Map == line.Map).Select(p => p.Key)).Distinct().Count(s => s != except);

    /// <summary>Synchronous capacity claim before any awaited leave. A session can hold only one destination.</summary>
    public Reservation? TryReserve(GameSession session, long scene, int requested = 0, int follow = 0, bool createNew = false)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(session, out var existing))
                return existing.Map.SceneEntry == scene && !createNew && (requested == 0 || requested == existing.Map.Id) ? existing : null;
            var limits = Limits(scene);
            if (!_lastLine.ContainsKey(scene)) AddLine(scene);
            var available = _lines.Values.Where(l => l.Map.SceneEntry == scene && !l.Closing).ToArray();
            Line? chosen;
            if (createNew) chosen = limits.Create ? AddLine(scene) : null;
            else if (requested > 0) chosen = available.FirstOrDefault(l => l.Map.Id == requested && Load(l, session) < limits.Hard);
            else
            {
                chosen = follow > 0 ? available.FirstOrDefault(l => l.Map.Id == follow && Load(l, session) < limits.Hard) : null;
                chosen ??= available.Where(l => Load(l, session) < limits.Soft).OrderBy(l => Load(l, session)).ThenBy(l => l.Map.Id).FirstOrDefault();
                if (chosen == null && limits.Create) chosen = AddLine(scene);
                chosen ??= available.Where(l => Load(l, session) < limits.Hard).OrderBy(l => Load(l, session)).ThenBy(l => l.Map.Id).FirstOrDefault();
            }
            if (chosen == null) return null;
            var reservation = new Reservation(++_token, chosen.Map);
            _reservations.Add(session, reservation);
            return reservation;
        }
    }

    public Reservation? Reserved(GameSession session) { lock (_gate) return _reservations.GetValueOrDefault(session); }
    public void Release(GameSession session, Reservation? expected = null)
    {
        lock (_gate)
            if (_reservations.TryGetValue(session, out var current) && (expected == null || current == expected)) _reservations.Remove(session);
    }

    public IReadOnlyList<LineInfo> Snapshot(long scene)
    {
        lock (_gate)
        {
            var limits = Limits(scene);
            return _lines.Values.Where(l => l.Map.SceneEntry == scene && !l.Closing).OrderBy(l => l.Map.Id).Select(l =>
            {
                int players = world.PlayersIn(l.Map.WorldScene).Count;
                return new LineInfo(l.Map, players, Load(l) - players, limits.Soft, limits.Hard);
            }).ToArray();
        }
    }

    /// <summary>Base line stays available. Extra line numbers are never reused during this process.
    /// Closing fences admissions before asynchronous owner cleanup; owners must account for timers and settlements.</summary>
    public async Task RetireEmptyAsync()
    {
        lock (_gate) { if (_sweeping) return; _sweeping = true; }
        try
        {
            Line[] candidates;
            lock (_gate) candidates = _lines.Values.Where(l => l.Map.Id > 1 && !l.Closing && Load(l) == 0).ToArray();
            foreach (var line in candidates)
            {
                lock (_gate)
                {
                    if (Load(line) != 0 || HasPendingWork.Any(p => p(line.Map))) continue;
                    line.Closing = true;
                }
                try
                {
                    foreach (var retire in Retiring) await retire(line.Map);
                    if (!world.TryRemoveInstance(line.Map.WorldScene))
                    {
                        lock (_gate) line.Closing = false;
                        continue; // an unaccounted owner keeps a reachable line; never orphan its map/ticker
                    }
                    lock (_gate) _lines.Remove(line.Map);
                }
                catch { lock (_gate) line.Closing = false; throw; }
            }
        }
        finally { lock (_gate) _sweeping = false; }
    }
}

/// <summary>The acknowledgment carries configuration only, so there is one outstanding generation per attachment.</summary>
public sealed record SceneHandoff(long Generation, RuntimeMapKey Destination, DateTimeOffset Deadline,
    PublicLineManager.Reservation? Reservation)
{
    public bool Completing { get; set; }
    public Func<GameSession, bool>? AdmissionValid { get; init; }
}
