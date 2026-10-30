using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>One air wall of a scene editor file: its id, gridLevel, group (GroupId, 0 without), name and the 1 m cells
/// (x, z) of its wallPoints.</summary>
public sealed record SceneAirWall(long Id, int GridLevel, long GroupId, string Name, IReadOnlySet<(int X, int Z)> Cells)
{
    public bool Contains(float x, float z) => Cells.Contains(((int)MathF.Floor(x), (int)MathF.Floor(z)));
}

/// <summary>
/// The air walls of a scene editor file (data/sceneeditor/*_sceneeditor.json, m_kAirWalls): id -> {gridLevel, wallPoints
/// [{x, y, z}], ...}. The client builds a wall's collision from its wallPoints cells when a dungeon switches it on
/// (Logic/Scene/Component/LC_SceneCom_AirWall.lua, dungeon var 133 CAirWallList).
/// </summary>
public static class SceneEditorAirWalls
{
    public static IReadOnlyDictionary<long, SceneAirWall> Read(JsonElement sceneEditor)
    {
        var walls = new Dictionary<long, SceneAirWall>();
        if (!sceneEditor.TryGetProperty("m_kAirWalls", out var all) || all.ValueKind != JsonValueKind.Object) return walls;
        foreach (var wall in all.EnumerateObject())
        {
            if (!long.TryParse(wall.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                || !wall.Value.TryGetProperty("wallPoints", out var points) || points.ValueKind != JsonValueKind.Array) continue;
            var cells = new HashSet<(int, int)>();
            foreach (var p in points.EnumerateArray())
                if (p.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number
                    && p.TryGetProperty("z", out var z) && z.ValueKind == JsonValueKind.Number)
                    cells.Add(((int)MathF.Floor(x.GetSingle()), (int)MathF.Floor(z.GetSingle())));
            string name = wall.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            walls[id] = new SceneAirWall(id, (int)GameData.Int(wall.Value, "gridLevel"), GameData.Int(wall.Value, "GroupId"), name, cells);
        }
        return walls;
    }
}
