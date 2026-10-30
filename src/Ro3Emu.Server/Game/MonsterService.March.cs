using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted marches (<see cref="MonsterMarch"/>, the Battle for Prontera invaders). A monster with a goal walks there
/// leg by leg along a nav route instead of roaming at home. It fights on the way as any monster does, its leash centred
/// where it left the route, and after the fight it takes up the way again from where it stands, not from home and
/// without healing. At the goal it stands and fights whoever comes. The route search and these rules are inferred: the
/// client data gives such units no path (NavGrid.FindPath).
/// </summary>
public sealed partial class MonsterService
{
    /// <summary>Plans in a row without a way on before a monster stops marching where it stands.</summary>
    internal const int MarchGiveUp = 3;

    /// <summary>Routes already searched, by map and start/goal cell (a wave leaves one point together).</summary>
    private readonly Dictionary<(long Scene, int FromX, int FromZ, int ToX, int ToZ), List<(float X, float Y, float Z)>?> _routes = [];

    private const int RouteCacheSize = 512;

    private static float LeashX(MonsterUnit m) => m.March?.LeashX ?? m.Home.X;
    private static float LeashZ(MonsterUnit m) => m.March?.LeashZ ?? m.Home.Z;

    /// <summary>It turns to a fight: the leash is measured from here, and the route is planned again afterwards.</summary>
    private static void LeaveRoute(MonsterUnit m)
    {
        if (m.March is not { } march) return;
        march.LeashX = m.X;
        march.LeashZ = m.Z;
    }

    private void March(MonsterUnit m, MapInstance map, MonsterMarch march, long now)
    {
        if (march.Arrived || march.Halted || march.Failures >= MarchGiveUp || m.Moving || m.Template.SpeedCm <= 0
            || m.BusyUntilMs > now) return;
        if (Distance(m.X, m.Z, march.GoalX, march.GoalZ) <= march.ArriveRadius)
        {
            march.Arrived = true;
            march.Route.Clear();
            SendStop(m, now); // the last leg ended without one (MonsterService.Tick)
            return;
        }
        while (march.Route.Count > 0 && Distance(m.X, m.Z, march.Route[0].X, march.Route[0].Z) < MinChaseStep) march.Route.RemoveAt(0);
        // Each leg is walked whole, between the points the route search checked it between (NavGrid.FindPath).
        if (march.Route.Count > 0 || PlanMarch(m, march))
        {
            if (WalkTo(m, map, march.Route[0].X, march.Route[0].Z, 1f))
            {
                march.Route.RemoveAt(0);
                march.Failures = 0;
                return;
            }
            march.Route.Clear(); // no straight way on from here (a cached route of another start): plan again
        }
        if (++march.Failures >= MarchGiveUp) SendStop(m, now);
    }

    /// <summary>
    /// Holds a marching monster where it stands (it stops at once, mid-leg) or lets it take up its way again from
    /// there. True when the hold changed.
    /// </summary>
    internal async Task<bool> HoldMarchAsync(MonsterUnit m, bool hold)
    {
        if (m.March is not { } march || march.Halted == hold) return false;
        march.Halted = hold;
        if (!hold) return true;
        march.Route.Clear(); // planned again from where it stopped
        if (!m.Moving) return true;
        long now = NowMs;
        m.Advance(now);
        m.Moving = false;
        await _world.MoveUnitAsync(m, Stop(m, now));
        return true;
    }

    private bool PlanMarch(MonsterUnit m, MonsterMarch march)
    {
        if (_worldData.Map(m.SceneEntry)?.Nav is not { } nav)
        {
            march.Route.Add((march.GoalX, m.Y, march.GoalZ));
            return true;
        }
        var key = (m.SceneEntry, (int)MathF.Floor(m.X), (int)MathF.Floor(m.Z), (int)MathF.Floor(march.GoalX), (int)MathF.Floor(march.GoalZ));
        // After a failed leg the shared route of this cell is searched again from the monster's own position.
        if (march.Failures > 0 || !_routes.TryGetValue(key, out var route))
        {
            if (_routes.Count >= RouteCacheSize) _routes.Clear();
            _routes[key] = route = nav.FindPath(m.X, m.Y, m.Z, march.GoalX, march.GoalZ, MaxSegment);
        }
        if (route == null || route.Count == 0) return false;
        march.Route.AddRange(route);
        return true;
    }
}
