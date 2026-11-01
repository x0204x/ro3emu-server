using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    /// <summary>The anti-cheat counter the walk and flight checks report to.</summary>
    internal CheatMonitor Cheats => cheats;

    /// <summary>A walk this short is a stop or a turn where the server has the player: never checked.</summary>
    private const float StandStill = 0.05f;

    /// <summary>Cells A* may expand looking for a way around a corner the straight leg grazes (inferred bound).</summary>
    private const int DetourNodes = 4000;

    /// <summary>
    /// Checks a client walk (31082) against what the server knows: a position that is a number, the distance the speed
    /// attribute allows since the last walk (<see cref="WalkAllowance"/>), the client's nav grid and the air walls.
    /// ServerOptions.WalkCheck "enforce" snaps a refused walk back to the last good position, "log" lets it through; both
    /// count it (CheatMonitor.Walk). The client sends a walk when its direction changes (in-game 22.10: legs of about
    /// 0.9 s), so the leg from the last walk is a straight line the grid can check.
    /// </summary>
    public async Task<bool> AcceptWalkAsync(GameSession s, WalkReq walk)
    {
        var mode = options.Value.WalkCheck;
        if (mode == "off") return true;
        var now = DateTime.UtcNow;
        var from = s.Position;
        string? problem = WalkProblem(s, from, walk.Pos, now, out float spend);
        double speed = StoryWalkSpeed(s);
        if (problem == null)
        {
            s.WalkAllowance.Drain(now, speed, spend);
            s.LastWalkUtc = now;
            return true;
        }
        cheats.Refused(s, CheatMonitor.Walk, $"scene {s.SceneEntry} {from.X:F1},{from.Z:F1} -> {walk.Pos.X:F1},{walk.Pos.Y:F1},{walk.Pos.Z:F1}: {problem}");
        if (mode != "enforce" && Finite(walk.Pos))
        {
            s.WalkAllowance.Drain(now, speed, spend);
            s.LastWalkUtc = now;
            return true;
        }
        await TeleportInMapAsync(s, new MapPoint(from.X, from.Y, from.Z, s.Face));
        return false;
    }

    /// <summary>The player cannot walk until <paramref name="until"/> (a root, a stun, a cast that holds it in place).</summary>
    public static void HoldWalk(GameSession s, DateTime until, bool walking) =>
        s.WalkAllowance.Hold(DateTime.UtcNow, until, WalkSpeed(s), walking ? (DateTime.UtcNow - s.LastWalkUtc).TotalSeconds : 0);

    /// <summary>Metres a second the walk budget grows by: the speed attribute, at least 1 m/s (a slowed player still walks).</summary>
    internal static double WalkSpeed(GameSession s) => Math.Max(1, s.Speed / 100.0);

    private double StoryWalkSpeed(GameSession s) => Math.Max(1, StoryMovementSpeed?.Invoke(s) ?? WalkSpeed(s));

    /// <summary>Why the walk cannot be real, or null; <paramref name="spend"/> is the distance it costs.</summary>
    private string? WalkProblem(GameSession s, Vec3 from, Vec3 to, DateTime now, out float spend)
    {
        spend = 0;
        if (!Finite(to)) return "position is not a number";
        float dx = to.X - from.X, dz = to.Z - from.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist <= StandStill) return null;
        spend = dist;
        double speed = StoryWalkSpeed(s), available = s.WalkAllowance.Available(now, speed);
        bool bridged = WalkBridged?.Invoke(s, from, to) == true;
        if (!bridged && worldData.Map(s.SceneEntry)?.Nav is { } nav && NavProblem(nav, from, to, available, ref spend) is { } blocked)
            return blocked;
        if (WalkBlocked?.Invoke(s, from, to) == true) return "path crosses an air wall";
        return spend > available + 1e-3 ? $"too fast ({spend:F1} m, {available:F1} m allowed at {speed:F1} m/s)" : null;
    }

    /// <summary>
    /// The grid's verdict: the destination must be on or next to a walkable cell, and the leg must cross only walkable
    /// cells, or A* must find a way round within the allowance (a leg that grazes a corner). A player who stands off the
    /// grid (an arrival in scenery) is not held to it until it is back on.
    /// </summary>
    private static string? NavProblem(NavGrid nav, Vec3 from, Vec3 to, double available, ref float spend)
    {
        if (nav.NearestWalkable(to.X, to.Z, to.Y) is not { } end) return "unwalkable cell";
        if (nav.NearestWalkable(from.X, from.Z, from.Y) is not { } start) return null;
        if (nav.CanWalk(from.X, start.Y, from.Z, to.X, to.Z) || nav.CanWalk(start.X, start.Y, start.Z, end.X, end.Z)) return null;
        var route = nav.FindPath(start.X, start.Y, start.Z, end.X, end.Z, maxNodes: DetourNodes);
        if (route == null) return "path crosses unwalkable cells";
        spend = Math.Max(spend, NavGrid.RouteLength(start.X, start.Z, route));
        return spend > available + 1e-3 ? "path crosses unwalkable cells (no way round within the allowance)" : null;
    }

    private static bool Finite(Vec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
