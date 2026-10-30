using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.DataExport;

/// <summary>
/// The pictures the web panel shows, decoded from the player's own client into data/icons (never shipped with RO3Emu):
/// - items/: ItemConfig _kIcon (the bag icon) and _kPic (a card's picture, halved to at most 400 pixels; "item_null_*"
///   is the client's blank one);
/// - monsters/: NPCConfig _kHeadIcon of the monster rows (_iNPCType 1);
/// - classes/: JobConfig _kIcon and _kBigIcon;
/// - maps/: MapConfig _kBgImage[0], the map picture of the client's map window, halved to at most 512 pixels;
/// - skills/: SkillConfig _kIcon, for the character sheet.
/// Each file is named as the client names the sprite. index.json records the VFS file hash every PNG came from (and its
/// sprite atlas's), so a later export decodes only what a client update changed and removes what it dropped.
/// </summary>
public static partial class IconExport
{
    public const string Folder = "icons";

    /// <summary>The DataConfig tables <see cref="Requests"/> reads.</summary>
    public static readonly string[] Tables = ["ItemConfig", "NPCConfig", "JobConfig", "MapConfig", "SkillConfig"];
    private const string IndexFile = "index.json";

    /// <summary>One picture to export: the kind folder, the client's sprite file name, the largest side kept.</summary>
    public sealed record Request(string Kind, string File, int MaxSide = int.MaxValue)
    {
        public string Path => Kind + "/" + File;
    }

    /// <param name="Written">Decoded and written now.</param>
    /// <param name="Unchanged">Already there from the same client files.</param>
    /// <param name="Missing">Named by a table but not in the client's VFS (the game shows nothing either).</param>
    /// <param name="Bytes">Size of every PNG in the folder afterwards.</param>
    public sealed record Result(int Written, int Unchanged, int Missing, int Failed, long Bytes, IReadOnlyList<string> Errors);

    /// <summary>Where one PNG came from: its sprite bundle's file hash, and the sprite atlas it was cut from.</summary>
    private sealed record Source(string Hash, string? Atlas = null, string? AtlasHash = null);

    /// <summary>The client's sprite names are plain file names; anything else in a table is not exported.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+\.png$")]
    private static partial Regex SpriteName();

    /// <summary>The pictures the panel uses, from the exported tables in <paramref name="configDir"/>, without duplicates.</summary>
    public static List<Request> Requests(string configDir)
    {
        var requests = new List<Request>();
        void Add(string kind, string? file, int maxSide = int.MaxValue)
        {
            if (file != null && SpriteName().IsMatch(file)) requests.Add(new Request(kind, file, maxSide));
        }
        foreach (var row in Rows(configDir, "ItemConfig"))
        {
            Add("items", Text(row, "_kIcon"));
            if (Text(row, "_kPic") is { } pic && !pic.StartsWith("item_null", StringComparison.OrdinalIgnoreCase)) Add("items", pic, 400);
        }
        foreach (var row in Rows(configDir, "NPCConfig"))
            if (row.TryGetProperty("_iNPCType", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt64() == 1) Add("monsters", Text(row, "_kHeadIcon"));
        foreach (var row in Rows(configDir, "JobConfig"))
        {
            Add("classes", Text(row, "_kIcon"));
            Add("classes", Text(row, "_kBigIcon"));
        }
        foreach (var row in Rows(configDir, "MapConfig"))
            if (row.TryGetProperty("_kBgImage", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                Add("maps", images[0].ValueKind == JsonValueKind.String ? images[0].GetString() : null, 512);
        foreach (var row in Rows(configDir, "SkillConfig"))
            Add("skills", Text(row, "_kIcon"));
        return [.. requests.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<JsonElement> Rows(string configDir, string table)
    {
        var path = System.IO.Path.Combine(configDir, table + ".json");
        if (!File.Exists(path)) yield break;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().AsEnumerable() : root.EnumerateObject().Select(p => p.Value);
        foreach (var row in rows.Where(r => r.ValueKind == JsonValueKind.Object)) yield return row;
    }

    private static string? Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Writes every requested picture into <paramref name="iconsDir"/> that is not already there from the same client
    /// files, and removes the ones no table asks for any more. A picture that fails to decode is reported, not fatal.
    /// </summary>
    public static Result Export(Vfs vfs, IReadOnlyList<Request> requests, string iconsDir, Action<int, int>? progress = null, Action<string>? log = null)
    {
        Directory.CreateDirectory(iconsDir);
        var previous = ReadIndex(iconsDir);
        var index = new ConcurrentDictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentQueue<string>();
        int written = 0, unchanged = 0, missing = 0, failed = 0, done = 0;
        var todo = new List<(Request Request, ulong Hash)>();
        foreach (var request in requests)
        {
            if (vfs.FileHash(request.File) is not { } hash)
            {
                missing++;
                continue;
            }
            if (previous.TryGetValue(request.Path, out var was) && Same(vfs, was, hash) && File.Exists(System.IO.Path.Combine(iconsDir, request.Path)))
            {
                index[request.Path] = was;
                unchanged++;
                continue;
            }
            todo.Add((request, hash));
        }
        progress?.Invoke(0, todo.Count);
        // Pictures of one bundle (an atlas's sprites, a map's) go to the same worker, which keeps that bundle open.
        var groups = todo.GroupBy(t => vfs.SourceFile(t.Request.File) + "#" + t.Hash.ToString("x", CultureInfo.InvariantCulture)).Select(g => g.ToList()).ToList();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4) };
        Parallel.ForEach(groups, parallel, () => new UnityImages(vfs), (group, _, images) =>
        {
            foreach (var (request, hash) in group)
            {
                try
                {
                    if (images.Load(request.File) is not { } image)
                    {
                        Interlocked.Increment(ref missing);
                        continue;
                    }
                    WriteAtomically(System.IO.Path.Combine(iconsDir, request.Path), Png.Encode(Png.Shrink(image, request.MaxSide)));
                    string? atlas = images.LastAtlas;
                    index[request.Path] = new Source(hash.ToString("x", CultureInfo.InvariantCulture), atlas, atlas != null ? vfs.FileHash(atlas)?.ToString("x", CultureInfo.InvariantCulture) : null);
                    Interlocked.Increment(ref written);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException)
                {
                    Interlocked.Increment(ref failed);
                    errors.Enqueue(request.Path + ": " + e.Message);
                }
                progress?.Invoke(Interlocked.Increment(ref done), todo.Count);
            }
            return images;
        }, images => images.Dispose());

        RemoveStale(iconsDir, previous.Keys.Where(p => !index.ContainsKey(p)));
        WriteIndex(iconsDir, index);
        long bytes = Directory.EnumerateFiles(iconsDir, "*.png", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        log?.Invoke($"icons: {written} written, {unchanged} unchanged, {missing} not in the client, {failed} failed, {bytes / 1024} KB");
        foreach (var e in errors.Take(20)) log?.Invoke("  " + e);
        return new Result(written, unchanged, missing, failed, bytes, [.. errors]);
    }

    private static bool Same(Vfs vfs, Source was, ulong hash) =>
        was.Hash == hash.ToString("x", CultureInfo.InvariantCulture) && (was.Atlas == null || vfs.FileHash(was.Atlas)?.ToString("x", CultureInfo.InvariantCulture) == was.AtlasHash);

    /// <summary>
    /// The VFS files the pictures come from, relative to the client folder with '/' separators: each picture's bundle,
    /// and the sprite atlas of every such bundle already in <paramref name="clientDir"/> (so a download that fetches
    /// these files and asks again gets the atlases too).
    /// </summary>
    public static List<string> SourceFiles(string clientDir, IReadOnlyList<Request> requests)
    {
        string vfsDir = GameDataExport.VfsDir(clientDir);
        using var vfs = new Vfs(vfsDir);
        using var images = new UnityImages(vfs);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests.OrderBy(r => vfs.SourceFile(r.File), StringComparer.Ordinal))
        {
            if (vfs.SourceFile(request.File) is not { } source) continue;
            files.Add(source);
            if (!File.Exists(System.IO.Path.Combine(vfsDir, source))) continue;
            try
            {
                if (images.AtlasOf(request.File) is { } atlas && vfs.SourceFile(atlas) is { } atlasSource) files.Add(atlasSource);
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        return [.. files.Select(f => "ro3_Data/StreamingAssets/VFS/" + f)];
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static void RemoveStale(string iconsDir, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var file = System.IO.Path.Combine(iconsDir, path);
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static Dictionary<string, Source> ReadIndex(string iconsDir)
    {
        var path = System.IO.Path.Combine(iconsDir, IndexFile);
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Source>>(File.ReadAllBytes(path)) is { } index
                ? new Dictionary<string, Source>(index, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void WriteIndex(string iconsDir, IDictionary<string, Source> index) =>
        WriteAtomically(System.IO.Path.Combine(iconsDir, IndexFile),
            JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<string, Source>(index, StringComparer.Ordinal)));
}
