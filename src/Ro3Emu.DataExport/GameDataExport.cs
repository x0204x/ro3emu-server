using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>
/// Everything the server reads from the client, exported in one go from a client folder into a data folder:
/// config/ (DataConfig, ComplexConfig, Localization_en, Localization_zh_CN and Localization_zh_TW tables), sceneeditor/, skills/, nav/, msgtable.tsv, plus
/// loc_en.tsv (the English map for the client, see <see cref="LocMapBuilder"/>) and export.json (what was exported
/// from which client). The new tables are written next to the old ones and swapped in at the end, so a failed or
/// cancelled export leaves the previous one intact. Nothing in the client folder is written.
/// </summary>
public static class GameDataExport
{
    /// <summary>Bumped when the export's output changes, so RO3Emu.exe exports again after an update of itself.</summary>
    public const int FormatVersion = 3;

    public static readonly string[] Folders = ["config", "sceneeditor", "skills", "nav"];

    public sealed record Options
    {
        /// <summary>Our translations (src/Ro3Emu.Loc/translations/*.tsv contents, in order); none skips them.</summary>
        public IReadOnlyList<string> Translations { get; init; } = [];
        /// <summary>Developers: re/loc/translation_queue.tsv, applied first as build_locmap.js does.</summary>
        public string? TranslationQueue { get; init; }
        public Action<string>? Log { get; init; }
        /// <summary>(stage, done, total) for a progress bar; total 0 = no count.</summary>
        public Action<string, int, int>? Progress { get; init; }
    }

    public sealed record Result(int ConfigTables, int SceneEditor, int Skills, int NavFiles, int Messages, int LocEntries,
        string Fingerprint, IReadOnlyCollection<string> ClientFilesRead);

    public static string LuaPayloadDir(string clientDir) => Path.Combine(clientDir, "ro3_Data", "StreamingAssets", "Recovery", "LuaPayload");

    public static string VfsDir(string clientDir) => Path.Combine(clientDir, "ro3_Data", "StreamingAssets", "VFS");

    /// <summary>What a client folder lacks for an export (empty = complete enough).</summary>
    public static List<string> Missing(string clientDir)
    {
        var missing = new List<string>();
        if (!Directory.Exists(LuaPayloadDir(clientDir)) || !Directory.EnumerateFiles(LuaPayloadDir(clientDir), "*.bytes", SearchOption.AllDirectories).Any())
            missing.Add(Path.Combine("ro3_Data", "StreamingAssets", "Recovery", "LuaPayload"));
        if (!File.Exists(Path.Combine(VfsDir(clientDir), "assetmeta.data")))
            missing.Add(Path.Combine("ro3_Data", "StreamingAssets", "VFS", "assetmeta.data"));
        return missing;
    }

    /// <summary>
    /// Identifies the client's game data: SHA-256 over every LuaPayload file and assetmeta.data. A client update
    /// changes it, so the data is exported again.
    /// </summary>
    public static string Fingerprint(string clientDir)
    {
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var payload = LuaPayloadDir(clientDir);
        var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(payload, f).Replace('\\', '/'), Path: f))
            .Append(("VFS/assetmeta.data", Path.Combine(VfsDir(clientDir), "assetmeta.data")))
            .OrderBy(f => f.Item1, StringComparer.Ordinal)
            .ToList();
        var hashes = new byte[files.Count][];
        Parallel.For(0, files.Count, i =>
        {
            using var s = File.OpenRead(files[i].Item2);
            hashes[i] = SHA256.HashData(s);
        });
        for (int i = 0; i < files.Count; i++)
        {
            total.AppendData(Encoding.UTF8.GetBytes(files[i].Item1 + "\n"));
            total.AppendData(hashes[i]);
        }
        return Convert.ToHexStringLower(total.GetHashAndReset());
    }

    /// <summary>
    /// A cheap identity of the same files (count, total size, newest write time): when it matches the stamp, the full
    /// <see cref="Fingerprint"/> is not needed to know the export is current.
    /// </summary>
    public static string QuickFingerprint(string clientDir)
    {
        long count = 0, bytes = 0, newest = 0;
        var files = new DirectoryInfo(LuaPayloadDir(clientDir)).EnumerateFiles("*", SearchOption.AllDirectories)
            .Append(new FileInfo(Path.Combine(VfsDir(clientDir), "assetmeta.data")));
        foreach (var f in files)
        {
            count++;
            bytes += f.Length;
            newest = Math.Max(newest, f.LastWriteTimeUtc.Ticks);
        }
        return $"{count}:{bytes}:{newest}";
    }

    /// <summary>The export.json of a data folder, or null.</summary>
    public static Stamp? ReadStamp(string dataDir)
    {
        var path = Path.Combine(dataDir, "export.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Stamp>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    public sealed record Stamp(int Format, string Fingerprint, string ClientDir, DateTime ExportedUtc, int Messages, int NavFiles, int ConfigTables,
        string? Quick = null);

    /// <summary>
    /// True when the data folder holds a complete export of this client's game data by this format: the quick identity
    /// matches, or else the full fingerprint does (a copied or touched client with the same content).
    /// </summary>
    public static bool UpToDate(string dataDir, string clientDir)
    {
        if (ReadStamp(dataDir) is not { } stamp || stamp.Format != FormatVersion
            || !File.Exists(Path.Combine(dataDir, "config", "MapResource.json")) || !File.Exists(Path.Combine(dataDir, "msgtable.tsv")))
            return false;
        if (stamp.Quick != null && stamp.Quick == QuickFingerprint(clientDir)) return true;
        return stamp.Fingerprint == Fingerprint(clientDir);
    }

    /// <summary>
    /// The VFS files that hold the walk maps, relative to the client folder with '/' separators. Needs only the Lua
    /// payload and the VFS index to answer, so a server host can download just what an export reads
    /// (RO3Emu install --server-files).
    /// </summary>
    public static List<string> NavSourceFiles(string clientDir)
    {
        var work = Directory.CreateTempSubdirectory("ro3emu-navsources-");
        try
        {
            var payload = LuaPayloadDir(clientDir);
            var mapResource = Path.Combine(payload, "Config", "DataConfig", "MapResource.lua.bytes");
            if (!File.Exists(mapResource)) throw new ExportException("The game scripts have no MapResource table.");
            // Only the one table is needed, but its module may require others: decode the whole tree (a few seconds).
            var luac = Path.Combine(work.FullName, "luac");
            LuaChunkDecoder.DecodeTree(payload, luac);
            using var exporter = new LuaConfigExporter(luac);
            if (!exporter.TryExport(Path.Combine(luac, "Config", "DataConfig", "MapResource.luac"), out var json, out var message))
                throw new ExportException("The MapResource table could not be read: " + message);
            var jsonPath = Path.Combine(work.FullName, "MapResource.json");
            File.WriteAllText(jsonPath, json);
            using var vfs = new Vfs(VfsDir(clientDir));
            return NavExporter.NavNames(jsonPath).Select(n => vfs.SourceFile(NavExporter.FileName(n))).OfType<string>()
                .Select(f => "ro3_Data/StreamingAssets/VFS/" + f).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        }
        finally { work.Delete(recursive: true); }
    }

    public static Result Run(string clientDir, string dataDir, Options? options = null)
    {
        options ??= new Options();
        var log = options.Log ?? (_ => { });
        var missing = Missing(clientDir);
        if (missing.Count > 0) throw new ExportException("The game folder is incomplete: " + string.Join(", ", missing) + " is missing.");

        Directory.CreateDirectory(dataDir);
        var work = Path.Combine(dataDir, ".export");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        var luac = Path.Combine(work, "luac");
        var staged = Path.Combine(work, "out");
        var errors = new List<string>();

        options.Progress?.Invoke("Reading the game scripts", 0, 0);
        string fingerprint = Fingerprint(clientDir);
        var decoded = LuaChunkDecoder.DecodeTree(LuaPayloadDir(clientDir), luac, errors.Add, (d, t) => options.Progress?.Invoke("Reading the game scripts", d, t));
        log($"decoded {decoded.Ok} Lua chunks, failed {decoded.Failed}");
        if (decoded.Ok == 0) throw new ExportException("No game script could be read from the game folder.");

        (int Ok, int Failed) Tables(string stage, string outDir, params string[] dirs)
        {
            using var exporter = new LuaConfigExporter(luac);
            var result = exporter.ExportDirs(outDir, dirs, errors.Add, (d, t) => options.Progress?.Invoke(stage, d, t));
            log($"{stage}: exported {result.Ok}, failed {result.Failed}");
            return result;
        }
        var config = Tables("Exporting the game tables", Path.Combine(staged, "config"), @"Config\DataConfig", @"Config\ComplexConfig");
        Tables("Exporting the English texts", Path.Combine(staged, "config"), "Localization_en.luac");
        var scene = Tables("Exporting the maps", Path.Combine(staged, "sceneeditor"), @"Config\SceneEditor");
        var skills = Tables("Exporting the skills", Path.Combine(staged, "skills"), @"Config\Skill");
        // Keep the Chinese tables next to Localization_en. Some client-visible wire strings (for example overhead NPC
        // names) bypass the patched text setters, so the server resolves their matching zh_TW text id directly.
        var locDir = Path.Combine(staged, "config");
        Tables("Exporting the Chinese texts", locDir, "Localization_zh_CN.luac");
        Tables("Exporting the Traditional Chinese texts", locDir, "Localization_zh_TW.luac");

        var mapResource = Path.Combine(staged, "config", "MapResource.json");
        if (!File.Exists(mapResource)) throw new ExportException("The game tables could not be exported (no MapResource table). Is this a complete Ragnarok 3 PC client?");

        options.Progress?.Invoke("Reading the message table", 0, 0);
        int messages = MsgTableExporter.Write(luac, Path.Combine(staged, "msgtable.tsv"));
        log($"msgtable: {messages} messages");
        if (messages < 100) throw new ExportException($"The message table has only {messages} messages; the game scripts look damaged.");

        NavExporter.Result nav;
        HashSet<string> filesRead;
        using (var vfs = new Vfs(VfsDir(clientDir)))
        {
            nav = NavExporter.Export(mapResource, VfsDir(clientDir), Path.Combine(staged, "nav"), (d, t) => options.Progress?.Invoke("Exporting the walk maps", d, t), vfs);
            filesRead = vfs.FilesRead;
        }
        log($"nav: {nav.Names} names, extracted {nav.Extracted}, missing {nav.Missing.Count} {string.Join(" ", nav.Missing)}");
        if (nav.Extracted == 0) throw new ExportException("No walk map was found in the game's VFS folder; the game files look incomplete.");

        options.Progress?.Invoke("Building the English text map", 0, 0);
        var loc = BuildLocMap(Path.Combine(locDir, "Localization_zh_CN.json"), Path.Combine(staged, "config", "Localization_en.json"),
            Path.Combine(staged, "loc_en.tsv"), options);

        foreach (var e in errors.Take(50)) log("  " + e);

        // Swap in: the old folders go away only now that the new ones are complete.
        foreach (var folder in Folders)
        {
            var target = Path.Combine(dataDir, folder);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            var source = Path.Combine(staged, folder);
            if (Directory.Exists(source)) Directory.Move(source, target);
        }
        foreach (var file in new[] { "msgtable.tsv", "loc_en.tsv" })
            File.Move(Path.Combine(staged, file), Path.Combine(dataDir, file), overwrite: true);
        var stamp = new Stamp(FormatVersion, fingerprint, Path.GetFullPath(clientDir), DateTime.UtcNow, messages, nav.Extracted, config.Ok,
            QuickFingerprint(clientDir));
        File.WriteAllText(Path.Combine(dataDir, "export.json"), JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Delete(work, recursive: true);

        var read = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { LuaPayloadDir(clientDir) };
        foreach (var f in filesRead) read.Add(f);
        return new Result(config.Ok, scene.Ok, skills.Ok, nav.Extracted, messages, loc.Count, fingerprint, read);
    }

    /// <summary>The client's two Localization chunks, the only input of the English map.</summary>
    public static string[] LocSourceFiles(string clientDir) =>
        new[] { "Localization_zh_CN", "Localization_en" }.Select(n => Path.Combine(LuaPayloadDir(clientDir), n + ".lua.bytes")).ToArray();

    /// <summary>
    /// The English map alone, without the rest of the export: what a player who joins someone else's server needs.
    /// Decodes only the two Localization chunks of <paramref name="clientDir"/>, so it takes seconds, and writes
    /// <paramref name="path"/> exactly as <see cref="Run"/> writes data/loc_en.tsv. Returns the entry count.
    /// </summary>
    public static int WriteLocMap(string clientDir, string path, Options? options = null)
    {
        options ??= new Options();
        var sources = LocSourceFiles(clientDir);
        if (sources.FirstOrDefault(s => !File.Exists(s)) is { } absent)
            throw new ExportException("The game folder is incomplete: " + Path.GetRelativePath(clientDir, absent) + " is missing.");
        var work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".locmap");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        try
        {
            var luac = Path.Combine(work, "luac");
            Directory.CreateDirectory(luac);
            foreach (var source in sources)
            {
                var bytes = File.ReadAllBytes(source);
                LuaChunkDecoder.Decode(bytes);
                File.WriteAllBytes(Path.Combine(luac, Path.GetFileName(source)[..^".lua.bytes".Length] + ".luac"), bytes);
            }
            var tables = Path.Combine(work, "tables");
            var errors = new List<string>();
            using (var exporter = new LuaConfigExporter(luac))
                exporter.ExportDirs(tables, ["Localization_zh_CN.luac", "Localization_en.luac"], errors.Add);
            foreach (var e in errors) options.Log?.Invoke("  " + e);
            var zh = Path.Combine(tables, "Localization_zh_CN.json");
            var en = Path.Combine(tables, "Localization_en.json");
            if (!File.Exists(zh) || !File.Exists(en)) throw new ExportException("The game's text tables could not be read: " + string.Join("; ", errors));
            var staged = Path.Combine(work, "loc_en.tsv");
            int count = BuildLocMap(zh, en, staged, options).Count;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.Move(staged, path, overwrite: true);
            return count;
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static LocMapBuilder BuildLocMap(string zhJson, string enJson, string outPath, Options options)
    {
        var loc = new LocMapBuilder();
        if (options.TranslationQueue is { } queue && File.Exists(queue)) loc.AddQueue(queue);
        if (File.Exists(zhJson) && File.Exists(enJson)) loc.AddClientTables(zhJson, enJson);
        loc.AddTranslations(options.Translations);
        loc.Write(outPath);
        options.Log?.Invoke($"loc_en.tsv: {loc.Count} entries ({loc.FromQueue} queue, {loc.FromTables} client tables, {loc.Ours} ours)");
        return loc;
    }
}

/// <summary>An export failure with a message for the player.</summary>
public sealed class ExportException(string message) : Exception(message);
