using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>
/// client-scenes.json: what keeps the client from entering a map. "Missing" lists the MapResource scenes (_kResource)
/// whose Unity scene the client's VFS does not have. The client loads a map as "&lt;_kResource&gt;.unity" (Lua_CFG_MapConfigBase.GetSceneResName -> MapResource._kResource,
/// LC_TransitionBase.LoadNewSceneAsyncReal -> UT_ResourceLoadManager.LoadSceneToID, SceneLoadManager asks the asset
/// index for "&lt;name&gt;.unity"). Without it every try fails: the loading screen shows "Failed to load the area.
/// Retrying..." (UIWords 28114, SceneChangeLoadFail) and retries, then gives up and asks for Prontera (30012 to 10100,
/// LoadNewSceneAsyncFailToLastScene). The server sends nobody to such a scene. The Genesis client of October 2026 ships
/// the tables, scene editor files and walk maps of Glast Heim and five eastern towns, but not their Unity scenes.
/// "UnloadableModules" lists the scene-editor modules (_kResSceneEditorName) the client's Lua loader refuses. Once a
/// scene is up, LC_TransitionState_PostLoading sets 85% and LC_TransitionBase.CreateSceneLogicInstance runs
/// LC_Scene_Sync.LoadSceneEditorConfig, whose bare require (_loadMapConfig) raises for such a module: 30005 is never sent
/// and the screen stays at 85%. RecoveryLuaSourceResolver.TryResolve finds a module by the last part of its name and
/// refuses one that no module has (reconstructed_module_not_admitted) or two share
/// (ambiguous_reconstructed_module_alias: the October 2026 client has Config/Map/sc_gonryun_001_sceneeditor and
/// Config/SceneEditor/sc_gonryun_001_sceneeditor). Map modules (_kResource) are not listed: the ones the client lacks
/// come from Ro3Emu.ClientHooks.MapModules (ClientMapModuleTests.EverySceneHasItsMapModuleInTheClientOrTheHooks).
/// </summary>
public static class ClientScenes
{
    public const string FileName = "client-scenes.json";

    /// <summary>A module's file in the client's LuaPayload: its logical name plus this.</summary>
    private const string ModuleSuffix = ".lua.bytes";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The distinct _kResource values of <paramref name="mapResourceJson"/> whose "&lt;name&gt;.unity" the VFS
    /// lacks, in table order.</summary>
    public static List<string> Missing(string mapResourceJson, Vfs vfs)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in TextColumn(mapResourceJson, "_kResource"))
        {
            if (!seen.Add(name)) continue;
            if (!vfs.Has(name + ".unity")) missing.Add(name);
        }
        return missing;
    }

    /// <summary>
    /// The distinct _kResSceneEditorName values of MapConfig.json and DungeonConfig.json in <paramref name="configDir"/>
    /// that the client's Lua loader refuses, read from its modules' files. RecoveryLuaSourceResolver answers from the
    /// signed manifest, which a server-only install does not download; LuaPayload is always there and holds the
    /// manifest's modules, one file each. (A module at the payload's root would win over a name two others share; no
    /// scene-editor module is at the root.)
    /// </summary>
    public static List<string> UnloadableModules(string luaPayloadDir, string configDir)
    {
        var modules = Directory.EnumerateFiles(luaPayloadDir, "*" + ModuleSuffix, SearchOption.AllDirectories)
            .GroupBy(file => Path.GetFileName(file)[..^ModuleSuffix.Length], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var refused = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in new[] { "MapConfig.json", "DungeonConfig.json" })
        {
            var path = Path.Combine(configDir, table);
            if (!File.Exists(path)) continue;
            foreach (var name in TextColumn(path, "_kResSceneEditorName"))
                if (seen.Add(name) && modules.GetValueOrDefault(name) != 1) refused.Add(name);
        }
        return refused;
    }

    /// <summary>A table's non-empty text values of one field, in table order.</summary>
    private static List<string> TextColumn(string tableJson, string field)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tableJson));
        List<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? [.. doc.RootElement.EnumerateArray()]
            : [.. doc.RootElement.EnumerateObject().Select(p => p.Value)];
        var values = new List<string>();
        foreach (var row in rows)
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } text)
                values.Add(text);
        return values;
    }

    /// <summary>Writes the lists ({"Missing": [...], "UnloadableModules": [...]}) and returns the first.</summary>
    public static List<string> Write(string mapResourceJson, Vfs vfs, string outFile, IReadOnlyList<string>? unloadableModules = null)
    {
        var missing = Missing(mapResourceJson, vfs);
        File.WriteAllText(outFile, JsonSerializer.Serialize(new { Missing = missing, UnloadableModules = unloadableModules ?? [] }, Indented));
        return missing;
    }
}
