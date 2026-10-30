using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege's air walls (docs/systems/territory-wars.md "Air walls"). The castle's navigation grid
/// (sc_gvg_gongchengzhan_001_dynamicmapdata.bytes) is open through the gateway, the barricades and the bastions: they
/// block only through the scene editor's air walls (sc_gvg_gongchengzhan_001_dg01 m_kAirWalls), which a dungeon switches
/// on with var 133 CAirWallList, a list of wall ids with DungeonVar.groupType / groupId saying who may pass (All 0,
/// Camp 1, Guild 2, Player 3; LC_SceneCom_AirWall.lua:322-480 OnDungeonVarChanged: a wall of the group missing from the
/// new list is removed). The client lists 2210101 and 2230101 specially (file head). The server sends the active set for
/// everyone (groupType 0) and treats its cells as blocked for walks and skill lines.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private const int AirWallListVar = 133;
    /// <summary>72 "空气墙_城门阻挡(内)" and 74 "空气墙_城门阻挡(外)": the gateway's inner and outer gate walls.</summary>
    private static readonly long[] SiegeGateWalls = [72, 74];
    /// <summary>71 "空气墙_进攻方阻挡" and 264/265 "空气墙-进攻方营地墙": the attackers' camp.</summary>
    private static readonly long[] SiegeCampWalls = [71, 264, 265];
    /// <summary>
    /// Group 77 "拒马&amp;相应空气墙" (barricades and their air walls): each Obstacle's spawn point and its wall, paired by name
    /// ("刷怪点-拒马-中路-1" / "空气墙-拒马-中路-1" and so on) and position (the barricade stands inside its wall).
    /// </summary>
    private static readonly Dictionary<int, long> SiegeBarricadeWalls = new()
    {
        [321] = 322, [80] = 117, [81] = 231, [83] = 232, [90] = 233, [93] = 234, [97] = 235, [101] = 243, [103] = 245, [107] = 247,
    };
    /// <summary>
    /// (inferred) The walls of fortresses 4-6 ("空气墙_要塞4阻挡(内/左/右)" and on), each behind the Knight Guardian of the same
    /// number ("守护者刷新点4" = 41 to 6 = 43): DestoryModelPerformParams shows a bastion broken once its guardian's point
    /// has no object, so its walls fall with it.
    /// </summary>
    private static readonly Dictionary<int, long[]> SiegeFortressWalls = new()
    {
        [41] = [221, 240, 241], [42] = [224, 238, 239], [43] = [225, 236, 237],
    };

    /// <summary>
    /// The walls standing in the run (inferred: the client has no script that switches them): the gate's while it stands,
    /// the attackers' camp in Prepare, the city wall at each ladder end until a ladder stands there and then the ladder
    /// lane's rails instead (TerritoryWarsService.SiegeUnits.cs), each barricade's and each fortress's while its object
    /// stands. The other walls of the scene (29/31/61/66 along the city wall, mostly on unwalkable cells) stay off.
    /// </summary>
    private static IEnumerable<long> SiegeActiveWalls(SiegeRunState r)
    {
        if (r.Stage == SiegePrepareStage) foreach (long wall in SiegeCampWalls) yield return wall;
        if (!r.Destroyed.Contains(SiegeGatePoint)) foreach (long wall in SiegeGateWalls) yield return wall;
        foreach (var ladder in SiegeLadders)
            if (!r.PlacedLadders.Contains(ladder.Point)) yield return ladder.Wall;
            else foreach (long wall in ladder.Lane) yield return wall;
        foreach (var (point, wall) in SiegeBarricadeWalls)
            if (!r.Destroyed.Contains(point)) yield return wall;
        foreach (var (point, walls) in SiegeFortressWalls)
            if (!r.Destroyed.Contains(point)) foreach (long wall in walls) yield return wall;
    }

    private static DungeonVar SiegeWallVar(SiegeRunState r)
    {
        var walls = new DungeonVar { VarId = AirWallListVar, GroupType = 0, GroupId = 0 };
        walls.Vi64.Add(SiegeActiveWalls(r).Order());
        return walls;
    }

    /// <summary>
    /// Whether the straight line from a to b enters a cell of a standing wall (sampled every quarter metre). A wall whose
    /// footprint holds a's cell never blocks (a unit already inside cannot be shut in), nor, for a skill, one holding b's:
    /// the gate and the barricades stand inside their own walls (inferred).
    /// </summary>
    private static bool SiegeWallCrossed(SiegeRunState r, MapWorld map, Vec3 a, Vec3 b, bool skill)
    {
        var walls = SiegeActiveWalls(r).Select(id => map.AirWalls.GetValueOrDefault(id)).OfType<SceneAirWall>()
            .Where(w => !w.Contains(a.X, a.Z) && !(skill && w.Contains(b.X, b.Z))).ToList();
        if (walls.Count == 0) return false;
        float dx = b.X - a.X, dz = b.Z - a.Z;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.25f));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps, x = a.X + dx * t, z = a.Z + dz * t;
            if (walls.Any(w => w.Contains(x, z))) return true;
        }
        return false;
    }

    /// <summary>SceneService.WalkBlocked: a siege member's walk into a standing wall.</summary>
    private bool SiegeWalkBlocked(GameSession s, Vec3 from, Vec3 to) =>
        s.SiegeBattle is { Room.Closed: false } battle && s.SceneEntry == SiegeScene && _maps.Map(SiegeScene) is { } map
        && SiegeWallCrossed(battle.State, map, from, to, skill: false);
}
