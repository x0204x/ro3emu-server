using System.Globalization;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    /// <summary>
    /// 31209 names a region of the player's current map. Returns it when its first action is server-hosted
    /// (ActionConfig _iEXECHost 1 or 2): the client only reports such regions instead of running them
    /// (re/clientB/lua-src/Logic/Asset/Systems/LC_System_Task.lua:2257-2321). The player must stand near it: within the
    /// region's larger side plus 5 m of its centre (<see cref="Near"/>; the region is a width x length box around the centre in
    /// the scene editor).
    /// </summary>
    public MapRegion? ServerHostedRegion(GameSession s, long areaId)
    {
        if (s.SceneLoading || worldData.Map(s.SceneEntry)?.Regions.TryGetValue(areaId, out var region) != true) return null;
        string op = region!.Actions.Split('|')[0];
        if (!int.TryParse(op, NumberStyles.Integer, CultureInfo.InvariantCulture, out int action)
            || data.Row("ActionConfig", action) is not { } row || GameData.Int(row, "_iEXECHost") is not (1 or 2)) return null;
        return Near(s, region) ? region : null;
    }

    /// <summary>
    /// Whether the player stands near region <paramref name="areaId"/> of its map, as <see cref="ServerHostedRegion"/> measures
    /// it; true for a region the server's scene editor data does not hold (it cannot tell, inferred). A 31209 area report
    /// from elsewhere is a forged one.
    /// </summary>
    public bool NearRegion(GameSession s, long areaId) =>
        worldData.Map(s.SceneEntry)?.Regions.TryGetValue(areaId, out var region) != true || Near(s, region!);

    /// <summary>
    /// Whether the player stands within <paramref name="reach"/> m of an NPC <paramref name="npcId"/> of its map; true when
    /// the map places none (a quest NPC the client spawns itself, inferred).
    /// </summary>
    public bool NearNpc(GameSession s, long npcId, float reach)
    {
        var placed = worldData.Map(s.SceneEntry)?.Npcs.Where(n => n.NpcId == npcId).ToList() ?? [];
        return placed.Count == 0 || placed.Any(n => (s.Position.X - n.X) * (s.Position.X - n.X) + (s.Position.Z - n.Z) * (s.Position.Z - n.Z) <= reach * reach);
    }

    /// <summary>Within the region's larger side plus 5 m of its centre (inferred tolerance for the walk report's lag).</summary>
    private static bool Near(GameSession s, MapRegion region)
    {
        float dx = s.Position.X - region.X, dz = s.Position.Z - region.Z, reach = Math.Max(region.Width, region.Length) + 5;
        return !s.SceneLoading && dx * dx + dz * dz <= reach * reach;
    }
}
