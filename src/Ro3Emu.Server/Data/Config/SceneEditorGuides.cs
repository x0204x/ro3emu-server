using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The guide routes of a scene editor file (data/sceneeditor/*_sceneeditor.json, m_kGuidePoints): id -> {pointList}.
/// LC_SceneCom_GuideGfx._showGuideWayGfx draws a route as the guide arrow line (enum_FxGuideArrow) through its points.
/// </summary>
public static class SceneEditorGuides
{
    /// <summary>Each route with at least two points as its (x, y, z) list, in the scene editor's own order.</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<(float X, float Y, float Z)>> Paths(JsonElement sceneEditor)
    {
        var paths = new Dictionary<long, IReadOnlyList<(float, float, float)>>();
        if (!sceneEditor.TryGetProperty("m_kGuidePoints", out var guides) || guides.ValueKind != JsonValueKind.Object) return paths;
        foreach (var guide in guides.EnumerateObject())
        {
            if (!long.TryParse(guide.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                || !guide.Value.TryGetProperty("pointList", out var points) || points.ValueKind != JsonValueKind.Array
                || points.GetArrayLength() < 2) continue;
            if (points.EnumerateArray().Any(p => !HasCoordinates(p))) continue; // a point without x, y or z would read as 0
            paths[id] = [.. points.EnumerateArray().Select(p => (Component(p, "x"), Component(p, "y"), Component(p, "z")))];
        }
        return paths;
    }

    private static readonly string[] Axes = ["x", "y", "z"];

    private static bool HasCoordinates(JsonElement point) =>
        Axes.All(n => point.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number);

    private static float Component(JsonElement point, string name) =>
        point.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

    /// <summary>The ids of the routes with at least two points, in id order.</summary>
    public static IReadOnlyList<long> RouteIds(JsonElement sceneEditor)
    {
        if (!sceneEditor.TryGetProperty("m_kGuidePoints", out var guides) || guides.ValueKind != JsonValueKind.Object) return [];
        var ids = new List<long>();
        foreach (var guide in guides.EnumerateObject())
            if (long.TryParse(guide.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                && guide.Value.ValueKind == JsonValueKind.Object
                && guide.Value.TryGetProperty("pointList", out var points) && points.ValueKind == JsonValueKind.Array
                && points.GetArrayLength() >= 2)
                ids.Add(id);
        ids.Sort();
        return ids;
    }
}
