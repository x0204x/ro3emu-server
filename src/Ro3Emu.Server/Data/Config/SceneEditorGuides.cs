using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The guide routes of a scene editor file (data/sceneeditor/*_sceneeditor.json, m_kGuidePoints): id -> {pointList}.
/// LC_SceneCom_GuideGfx._showGuideWayGfx draws a route as the guide arrow line (enum_FxGuideArrow) through its points.
/// </summary>
public static class SceneEditorGuides
{
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
