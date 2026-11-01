using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The scene editor's MapLinkData.json: each map's neighbours (a scene-editor name without "_sceneeditor" to its
/// m_kFixTravelPoints, the map ids the world map links it to) and the Kafra destinations (Capra).
/// </summary>
public static class SceneEditorLinks
{
    private const string Suffix = "_sceneeditor";

    /// <summary>The scene-editor name as MapLinkData keys it: MapConfig _kResSceneEditorName without its suffix.</summary>
    public static string Key(string sceneEditorName) =>
        sceneEditorName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? sceneEditorName[..^Suffix.Length] : sceneEditorName;

    /// <summary>Every map's neighbouring map ids by its key.</summary>
    public static IReadOnlyDictionary<string, long[]> Neighbours(JsonElement mapLinkData)
    {
        var links = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        if (mapLinkData.ValueKind != JsonValueKind.Object) return links;
        foreach (var map in mapLinkData.EnumerateObject())
            if (map.Value.ValueKind == JsonValueKind.Object && map.Value.TryGetProperty("m_kFixTravelPoints", out var points)
                && points.ValueKind == JsonValueKind.Array)
                links[map.Name] = [.. points.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Number).Select(p => p.GetInt64())];
        return links;
    }

    /// <summary>The maps a Kafra can send players to.</summary>
    public static IReadOnlySet<long> KafraDestinations(JsonElement mapLinkData)
    {
        var maps = new HashSet<long>();
        if (mapLinkData.ValueKind == JsonValueKind.Object && mapLinkData.TryGetProperty("Capra", out var capra) && capra.ValueKind == JsonValueKind.Object)
            foreach (var p in capra.EnumerateObject())
                if (long.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) maps.Add(id);
        return maps;
    }
}
