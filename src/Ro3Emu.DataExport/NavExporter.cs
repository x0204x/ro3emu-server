using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>
/// Writes the nav grid (*_dynamicmapdata.bytes) of every MapResource row from the client's VFS into a folder.
/// MapResource._kNavDataName "X data.bytes" is loaded by the client as "X_dynamicmapdata.bytes"
/// (RoSpace.LoadSpaceDirect). A port of tools/WorldData/navexport.js.
/// </summary>
public static class NavExporter
{
    public sealed record Result(int Names, int Extracted, IReadOnlyList<string> Missing);

    /// <param name="mapResourceJson">data/config/MapResource.json (exported first).</param>
    /// <param name="vfsDir">&lt;client&gt;/ro3_Data/StreamingAssets/VFS.</param>
    public static Result Export(string mapResourceJson, string vfsDir, string outDir, Action<int, int>? progress = null, Vfs? vfs = null)
    {
        var names = NavNames(mapResourceJson);
        Directory.CreateDirectory(outDir);
        bool own = vfs == null;
        vfs ??= new Vfs(vfsDir);
        try
        {
            int ok = 0, done = 0;
            var missing = new List<string>();
            foreach (var n in names)
            {
                var file = FileName(n);
                var data = vfs.Extract(file);
                if (data != null)
                {
                    ok++;
                    File.WriteAllBytes(Path.Combine(outDir, file), data);
                }
                else missing.Add(n);
                progress?.Invoke(++done, names.Count);
            }
            return new Result(names.Count, ok, missing);
        }
        finally
        {
            if (own) vfs.Dispose();
        }
    }

    /// <summary>The distinct _kNavDataName values, in table order.</summary>
    public static List<string> NavNames(string mapResourceJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(mapResourceJson));
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : [.. doc.RootElement.EnumerateObject().Select(p => p.Value)];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_kNavDataName", out var v) || v.ValueKind != JsonValueKind.String) continue;
            var name = v.GetString();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            names.Add(name);
        }
        return names;
    }

    /// <summary>"X data.bytes" -> "X_dynamicmapdata.bytes" (first occurrence, as the JS export did).</summary>
    public static string FileName(string navName)
    {
        if (navName.Contains("_dynamicmapdata.bytes", StringComparison.Ordinal)) return navName;
        int at = navName.IndexOf("data.bytes", StringComparison.Ordinal);
        return at < 0 ? navName : navName[..at] + "_dynamicmapdata.bytes" + navName[(at + "data.bytes".Length)..];
    }
}
