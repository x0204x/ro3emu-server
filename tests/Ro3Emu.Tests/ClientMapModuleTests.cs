using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.Tests;

/// <summary>
/// Every scene a DungeonConfig or MapConfig row can open requires two Lua modules on load: the map module
/// MapResource._kResource (LC_Scene_Sync._loadMapBaseConfig, LC_Scene_Sync.lua:2427-2447) and the scene-editor module
/// _kResSceneEditorName (_loadMapConfig, LC_Scene_Sync.lua:2449-2456). A require the client cannot satisfy raises in
/// SceneLoadManager's load callback: the loading screen stops at 85% and the gate drops the player (Dracula's Lair,
/// sc_pm_gef_dun02_001, 16.10). The client loads only what its recovery manifest lists, by the last part of the name
/// (LA_LuaLoader.Obf_HD), and refuses a name that is missing or shared by two modules (RecoveryLuaSourceResolver);
/// the exported client's LuaPayload holds exactly the manifest's modules, so a name resolves when exactly one file
/// there has it.
/// Map modules the build lacks are supplied by Ro3Emu.ClientHooks.MapModules (its table is read from the source here,
/// the hooks project builds against the client's own DLLs). The manifest inventory is read directly from the
/// exported client's LuaPayload; decompilation is unnecessary when only module names are under test.
/// </summary>
public sealed class ClientMapModuleTests
{
    /// <summary>Scene-editor modules no reachable scene needs: (inferred) unreachable rows, or no scene bundle at all.</summary>
    private static readonly Dictionary<string, string> KnownEditorGaps = new(StringComparer.OrdinalIgnoreCase)
    {
        // MapConfig 100, the role-creation scene (LC_LoginManager.lua:2580); role creation works in game without it.
        ["sc_creator_002_sceneeditor"] = "role creation",
        // DungeonConfig 1023001: the module is in neither the manifest nor the VFS; nothing on the server opens the row.
        ["sc_payon_001_pl02_sceneeditor"] = "not in the client at all",
        // MapConfig 10601-10606: Config/Map and Config/SceneEditor both have the name (ambiguous alias). Only Gonryun has a
        // scene bundle and no portal or MapLinkData entry leads to any of them.
        ["sc_amatsu_001_sceneeditor"] = "ambiguous, no scene bundle",
        ["sc_gonryun_001_sceneeditor"] = "ambiguous, not linked",
        ["sc_louyang_001_sceneeditor"] = "ambiguous, no scene bundle",
        ["sc_ayothaya_001_sceneeditor"] = "ambiguous, no scene bundle",
        ["sc_dewata_001_sceneeditor"] = "ambiguous, no scene bundle",
        ["sc_malaya_001_sceneeditor"] = "ambiguous, no scene bundle",
    };

    [ClientLuaFact]
    public void EverySceneHasItsMapModuleInTheClientOrTheHooks()
    {
        var shipped = Resolvable();
        var hooks = HookModules();
        var missing = Scenes().Where(s => s.Resource.Length > 0 && !shipped.Contains(s.Resource) && !hooks.Contains(s.Resource))
            .Select(s => s.Row + " -> " + s.Resource).ToList();
        Assert.True(missing.Count == 0, "map modules neither shipped nor in MapModules: " + string.Join(", ", missing));
        Assert.True(shipped.Contains("sc_pm_gef_dun02_001") || hooks.Contains("sc_pm_gef_dun02_001"));
    }

    [ClientLuaFact]
    public void HookModulesNeverShadowAShippedModule()
    {
        // MapModules.Install only skips modules already in package.loaded; a shipped one not required yet would be replaced.
        var shipped = Resolvable();
        var hooks = HookModules();
        Assert.NotEmpty(hooks);
        Assert.Equal(Array.Empty<string>(), hooks.Where(shipped.Contains).ToArray());
        var used = Scenes().Select(s => s.Resource).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Array.Empty<string>(), hooks.Where(h => !used.Contains(h)).ToArray());
    }

    [ClientLuaFact]
    public void EverySceneHasItsSceneEditorModule()
    {
        var shipped = Resolvable();
        var missing = Scenes().Where(s => s.Editor.Length > 0 && !shipped.Contains(s.Editor) && !KnownEditorGaps.ContainsKey(s.Editor))
            .Select(s => s.Row + " -> " + s.Editor).ToList();
        Assert.True(missing.Count == 0, "scene-editor modules the client cannot load: " + string.Join(", ", missing));
    }

    private sealed record Scene(string Row, string Resource, string Editor);

    private static IEnumerable<Scene> Scenes()
    {
        using var resources = Json("MapResource");
        var names = new Dictionary<long, string>();
        foreach (var row in resources.RootElement.EnumerateObject())
            names[row.Value.GetProperty("_iID").GetInt64()] = Text(row.Value, "_kResource");
        var scenes = new List<Scene>();
        foreach (var table in new[] { "DungeonConfig", "MapConfig" })
        {
            using var rows = Json(table);
            foreach (var row in rows.RootElement.EnumerateObject())
            {
                long resourceId = row.Value.TryGetProperty("_iMapResourceID", out var id) ? id.GetInt64() : 0;
                scenes.Add(new Scene(table + " " + row.Name, names.GetValueOrDefault(resourceId, ""), Text(row.Value, "_kResSceneEditorName")));
            }
        }
        return scenes;
    }

    /// <summary>Module names the client resolves: exactly one Lua file with that name (case-insensitive).</summary>
    private static HashSet<string> Resolvable()
    {
        Assert.True(Directory.Exists(ClientData.LuaPayloadDir),
            $"The exported client's LuaPayload is missing: {ClientData.LuaPayloadDir}");
        return Directory.EnumerateFiles(ClientData.LuaPayloadDir, "*.lua.bytes", SearchOption.AllDirectories)
            .GroupBy(path => Path.GetFileName(path)[..^".lua.bytes".Length], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).Select(g => g.Key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> HookModules() =>
        Regex.Matches(File.ReadAllText(Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.ClientHooks", "MapModules.cs")),
                @"new MapModule\(""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static JsonDocument Json(string table) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ClientData.RepoRoot, "data", "config", table + ".json")));

    private static string Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>
/// A fact that needs the exported game data and client Lua evidence. With D data present it must run and fail closed
/// when required Lua evidence is missing; a missing local evidence export must never turn into a skipped D test.
/// </summary>
public sealed class ClientLuaFactAttribute : FactAttribute
{
    public ClientLuaFactAttribute()
    {
        if (!ClientData.Available) Skip = ClientData.SkipReason;
    }
}
