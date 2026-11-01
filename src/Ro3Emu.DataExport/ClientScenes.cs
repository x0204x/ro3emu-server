using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>
/// client-scenes.json: the MapResource scenes (_kResource) whose Unity scene the client's VFS does not have. The client
/// loads a map as "&lt;_kResource&gt;.unity" (Lua_CFG_MapConfigBase.GetSceneResName -> MapResource._kResource,
/// LC_TransitionBase.LoadNewSceneAsyncReal -> UT_ResourceLoadManager.LoadSceneToID, SceneLoadManager asks the asset
/// index for "&lt;name&gt;.unity"). Without it every try fails: the loading screen shows "Failed to load the area.
/// Retrying..." (UIWords 28114, SceneChangeLoadFail) and retries, then gives up and asks for Prontera (30012 to 10100,
/// LoadNewSceneAsyncFailToLastScene). The server sends nobody to such a scene. The Genesis client of October 2026 ships
/// the tables, scene editor files and walk maps of Glast Heim and five eastern towns, but not their Unity scenes.
/// </summary>
public static class ClientScenes
{
    public const string FileName = "client-scenes.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The distinct _kResource values of <paramref name="mapResourceJson"/> whose "&lt;name&gt;.unity" the VFS
    /// lacks, in table order.</summary>
    public static List<string> Missing(string mapResourceJson, Vfs vfs)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(mapResourceJson));
        List<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? [.. doc.RootElement.EnumerateArray()]
            : [.. doc.RootElement.EnumerateObject().Select(p => p.Value)];
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_kResource", out var v) || v.ValueKind != JsonValueKind.String) continue;
            var name = v.GetString();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            if (!vfs.Has(name + ".unity")) missing.Add(name);
        }
        return missing;
    }

    /// <summary>Writes the list ({"Missing": [...]}) and returns it.</summary>
    public static List<string> Write(string mapResourceJson, Vfs vfs, string outFile)
    {
        var missing = Missing(mapResourceJson, vfs);
        File.WriteAllText(outFile, JsonSerializer.Serialize(new { Missing = missing }, Indented));
        return missing;
    }
}
