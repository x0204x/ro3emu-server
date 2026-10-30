using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>
/// Everything the server reads from the client, exported in one go from a client folder into a data folder:
/// config/ (DataConfig, ComplexConfig, Localization_en, Localization_zh_CN and Localization_zh_TW tables), sceneeditor/, skills/, nav/, msgtable.tsv, plus
/// loc_en.tsv (the English map for the client, see <see cref="LocMapBuilder"/>) and export.json (what was exported
/// from which client). Nothing in the client folder is written.
/// - **Complete or flagged:** a table that fails to export is an error unless it is a known non-table module
///   (<see cref="ExpectedTableFailures"/>); any other failure is listed in the stamp and makes the export incomplete,
///   so <see cref="UpToDate"/> says no and the next start exports again (the player sees the warning).
/// - **Manifest:** the stamp counts the files of every exported folder; <see cref="UpToDate"/> checks them, not just
///   two files, so a damaged or half-replaced data folder is exported again.
/// - **Swap:** the new tables are staged next to the old ones. A journal (.export/swapping) is written first; then the
///   stamp and each old folder are renamed aside and the new ones renamed in; a failure puts the old ones back. The
///   stamp is written last, so an export cut anywhere in between leaves no stamp. A process that dies mid-swap leaves
///   the journal: the next export (and <see cref="UpToDate"/>) first puts the previous generation back
///   (<see cref="RecoverInterruptedSwap"/>), and only then clears its work folder.
/// - **Incomplete:** a table that could not even be decoded counts as failed too. An incomplete export still goes in
///   (the server can run on what did export), but the previous generation is kept in .export-previous.
/// </summary>
public static class GameDataExport
{
    /// <summary>Bumped when the export's output changes, so RO3Emu.exe exports again after an update of itself.</summary>
    public const int FormatVersion = 3;

    /// <summary>Client modules under the exported folders that are code, not tables: they never export, and that is fine.</summary>
    public static readonly IReadOnlySet<string> ExpectedTableFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Lua_DBManager" };

    public static readonly string[] Folders = ["config", "sceneeditor", "skills", "nav"];

    public sealed record Options
    {
        /// <summary>Our translations (src/Ro3Emu.Loc/translations/*.tsv contents, in order); none skips them.</summary>
        public IReadOnlyList<string> Translations { get; init; } = [];
        /// <summary>本程式自有繁中覆寫；有效的玩家原生繁中 SID 優先。</summary>
        public IReadOnlyList<string> TraditionalChineseOverrides { get; init; } = [];
        /// <summary>Developers: re/loc/translation_queue.tsv, applied first as build_locmap.js does.</summary>
        public string? TranslationQueue { get; init; }
        public Action<string>? Log { get; init; }
        /// <summary>(stage, done, total) for a progress bar; total 0 = no count.</summary>
        public Action<string, int, int>? Progress { get; init; }
        /// <summary>Tests: called at each step of the swap ("aside:&lt;name&gt;", "in:&lt;name&gt;"); throwing fails it there.</summary>
        internal Action<string>? SwapStepForTest { get; init; }
    }

    /// <param name="FailedTables">Tables that could not be exported (beyond <see cref="ExpectedTableFailures"/>); not
    /// empty = the export is incomplete and the server may miss content.</param>
    public sealed record Result(int ConfigTables, int SceneEditor, int Skills, int NavFiles, int Messages, int LocEntries,
        string Fingerprint, IReadOnlyCollection<string> ClientFilesRead, IReadOnlyList<string> FailedTables);

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

    /// <param name="Files">Exported files per folder (<see cref="Folders"/>) at export time; <see cref="UpToDate"/>
    /// checks them.</param>
    /// <param name="FailedTables">Tables that did not export (incomplete export), empty when complete.</param>
    public sealed record Stamp(int Format, string Fingerprint, string ClientDir, DateTime ExportedUtc, int Messages, int NavFiles, int ConfigTables,
        string? Quick = null, Dictionary<string, int>? Files = null, string[]? FailedTables = null);

    /// <summary>The single files an export writes besides <see cref="Folders"/>.</summary>
    public static readonly string[] TopFiles = ["msgtable.tsv", "loc_en.tsv"];

    /// <summary>Client script folders (and files) whose modules become exported tables: a chunk among them that does not
    /// decode is a missing table.</summary>
    private static readonly string[] TableSources =
        [@"Config\DataConfig\", @"Config\ComplexConfig\", @"Config\SceneEditor\", @"Config\Skill\", "Localization_en.", "Localization_zh_CN.", "Localization_zh_TW."];

    /// <summary>The table a <see cref="LuaChunkDecoder.DecodeTree"/> error ("&lt;relative path&gt;: message") loses, or
    /// null when the chunk is not one the export turns into a table.</summary>
    internal static string? TableOfDecodeError(string error)
    {
        string rel = error.Split(": ", 2)[0].Replace('/', '\\');
        if (!TableSources.Any(prefix => rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return null;
        string name = rel[(rel.LastIndexOf('\\') + 1)..]; // not Path.GetFileName: on Linux it does not split on '\'
        int dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// True when the data folder holds a complete export of this client's game data by this format: the stamp lists no
    /// failed table, every exported folder still has the files it had, and the quick identity of the client matches, or
    /// else the full fingerprint does (a copied or touched client with the same content).
    /// </summary>
    public static bool UpToDate(string dataDir, string clientDir)
    {
        if (File.Exists(SwapJournal(dataDir))) return false; // a swap was cut: the next export puts things right first
        if (ReadStamp(dataDir) is not { } stamp || stamp.Format != FormatVersion || stamp.FailedTables is not { Length: 0 }
            || stamp.Files is not { } files || !File.Exists(Path.Combine(dataDir, "config", "MapResource.json")))
            return false;
        if (TopFiles.Any(name => !File.Exists(Path.Combine(dataDir, name)))) return false;
        foreach (var folder in Folders)
            if (!files.TryGetValue(folder, out int expected) || CountFiles(Path.Combine(dataDir, folder)) != expected) return false;
        if (stamp.Quick != null && stamp.Quick == QuickFingerprint(clientDir)) return true;
        return stamp.Fingerprint == Fingerprint(clientDir);
    }

    private static int CountFiles(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : -1;

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
        RecoverInterruptedSwap(dataDir); // before the work folder (and the previous generation in it) is cleared
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        var luac = Path.Combine(work, "luac");
        var staged = Path.Combine(work, "out");
        var errors = new List<string>();
        var failedTables = new List<string>();

        options.Progress?.Invoke("Reading the game scripts", 0, 0);
        string fingerprint = Fingerprint(clientDir);
        var decoded = LuaChunkDecoder.DecodeTree(LuaPayloadDir(clientDir), luac, error =>
        {
            errors.Add(error);
            if (TableOfDecodeError(error) is { } table && !ExpectedTableFailures.Contains(table)) failedTables.Add(table);
        }, (d, t) => options.Progress?.Invoke("Reading the game scripts", d, t));
        log($"decoded {decoded.Ok} Lua chunks, failed {decoded.Failed}");
        if (decoded.Ok == 0) throw new ExportException("No game script could be read from the game folder.");

        (int Ok, int Failed) Tables(string stage, string outDir, params string[] dirs)
        {
            using var exporter = new LuaConfigExporter(luac);
            var result = exporter.ExportDirs(outDir, dirs, error =>
            {
                errors.Add(error);
                string module = error.Split(':', 2)[0];
                if (!ExpectedTableFailures.Contains(module)) failedTables.Add(module);
            }, (d, t) => options.Progress?.Invoke(stage, d, t));
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
        if (failedTables.Count > 0) log($"INCOMPLETE: {failedTables.Count} game table(s) could not be exported: {string.Join(", ", failedTables.Take(20))}");

        var files = Folders.ToDictionary(folder => folder, folder => CountFiles(Path.Combine(staged, folder)));
        SwapIn(dataDir, staged, Path.Combine(work, "old"), options.SwapStepForTest);
        var stamp = new Stamp(FormatVersion, fingerprint, Path.GetFullPath(clientDir), DateTime.UtcNow, messages, nav.Extracted, config.Ok,
            QuickFingerprint(clientDir), files, failedTables.ToArray());
        var stampPath = Path.Combine(dataDir, "export.json");
        File.WriteAllText(stampPath + ".tmp", JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(stampPath + ".tmp", stampPath, overwrite: true);
        var old = Path.Combine(work, "old");
        if (failedTables.Count > 0 && Directory.Exists(old) && Directory.EnumerateFileSystemEntries(old).Any())
        {
            // Incomplete: keep the generation it replaced, so nothing that worked before is lost.
            var previous = Path.Combine(dataDir, ".export-previous");
            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
            Directory.Move(old, previous);
            log($"the previous game data is kept in {previous}");
        }
        File.Delete(SwapJournal(dataDir));
        Directory.Delete(work, recursive: true);

        var read = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { LuaPayloadDir(clientDir) };
        foreach (var f in filesRead) read.Add(f);
        return new Result(config.Ok, scene.Ok, skills.Ok, nav.Extracted, messages, loc.Count, fingerprint, read, failedTables);
    }

    /// <summary>
    /// Replaces the exported folders and files of <paramref name="dataDir"/> with the staged ones. The stamp is removed
    /// first, so until the caller writes the new one the data folder never counts as up to date. Old entries are renamed
    /// into <paramref name="old"/> (same volume, no copy) and put back if any step fails.
    /// </summary>
    internal static void SwapIn(string dataDir, string staged, string old, Action<string>? stepForTest = null)
    {
        Directory.CreateDirectory(old);
        // Durable before the first rename: a process that dies from here on leaves it, and RecoverInterruptedSwap then
        // knows that old/ holds the previous generation of whatever was already replaced.
        var journal = SwapJournal(dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        using (var file = new FileStream(journal, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(System.Text.Encoding.UTF8.GetBytes(old));
            file.Flush(flushToDisk: true);
        }
        var entries = SwapEntries();
        var movedAside = new List<(string Name, bool Folder)>();
        var movedIn = new List<(string Name, bool Folder)>();
        try
        {
            foreach (var entry in entries)
            {
                string target = Path.Combine(dataDir, entry.Name), aside = Path.Combine(old, entry.Name), source = Path.Combine(staged, entry.Name);
                if (entry.Folder ? Directory.Exists(target) : File.Exists(target))
                {
                    Move(target, aside, entry.Folder);
                    movedAside.Add(entry);
                }
                stepForTest?.Invoke("aside:" + entry.Name);
                if (entry.Folder ? Directory.Exists(source) : File.Exists(source))
                {
                    Move(source, target, entry.Folder);
                    movedIn.Add(entry);
                }
                stepForTest?.Invoke("in:" + entry.Name);
            }
        }
        catch
        {
            // Back to the previous export exactly: the new entries return to staging, the old ones to their place.
            foreach (var entry in movedIn) Move(Path.Combine(dataDir, entry.Name), Path.Combine(staged, entry.Name), entry.Folder);
            foreach (var entry in movedAside) Move(Path.Combine(old, entry.Name), Path.Combine(dataDir, entry.Name), entry.Folder);
            File.Delete(journal);
            throw;
        }
    }

    /// <summary>What a swap replaces, the stamp first (renamed aside like the rest, so a recovery brings it back).</summary>
    private static List<(string Name, bool Folder)> SwapEntries() =>
        new[] { (Name: "export.json", Folder: false) }.Concat(Folders.Select(name => (Name: name, Folder: true)))
            .Concat(TopFiles.Select(name => (Name: name, Folder: false))).ToList();

    private static string SwapJournal(string dataDir) => Path.Combine(dataDir, ".export", "swapping");

    /// <summary>
    /// After a process died mid-swap (the journal is still there): every entry already renamed aside goes back in place
    /// of whatever replaced it, so the data folder is the previous generation again, stamp included. Throws if that
    /// fails, leaving the journal for the next attempt.
    /// </summary>
    internal static void RecoverInterruptedSwap(string dataDir)
    {
        var journal = SwapJournal(dataDir);
        if (!File.Exists(journal)) return;
        var old = File.ReadAllText(journal).Trim();
        try
        {
            foreach (var (name, folder) in SwapEntries())
            {
                string aside = Path.Combine(old, name), target = Path.Combine(dataDir, name);
                if (!(folder ? Directory.Exists(aside) : File.Exists(aside))) continue;
                if (folder && Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else if (!folder && File.Exists(target)) File.Delete(target);
                Move(aside, target, folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ExportException($"An earlier game data export was cut while replacing the data and could not be put back ({e.Message}). " +
                $"The previous data is in {old}.");
        }
        File.Delete(journal);
    }

    private static void Move(string from, string to, bool folder)
    {
        if (folder) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>玩家自身的三語來源；繁中與 SID 配對檔不可借用其他客戶端世代。</summary>
    public static string[] LocSourceFiles(string clientDir) =>
        new[] { "Localization_zh_CN", "Localization_en", "Localization_zh_TW" }
            .Select(n => Path.Combine(LuaPayloadDir(clientDir), n + ".lua.bytes")).ToArray();

    /// <summary>
    /// 不重建伺服器資料，只從玩家的三語來源產生英文及繁中配對檔。
    /// <paramref name="path"/> 仍是英文檔，回傳值仍為英文筆數，維持既有呼叫契約。
    /// </summary>
    public static int WriteLocMap(string clientDir, string path, Options? options = null)
    {
        options ??= new Options();
        var sources = LocSourceFiles(clientDir);
        if (sources.FirstOrDefault(s => !File.Exists(s)) is { } absent)
            throw new ExportException("The game folder is incomplete: " + Path.GetRelativePath(clientDir, absent) + " is missing.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var work = Path.Combine(directory, ".locmap-" + Guid.NewGuid().ToString("N"));
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
                exporter.ExportDirs(tables, ["Localization_zh_CN.luac", "Localization_en.luac", "Localization_zh_TW.luac"], errors.Add);
            foreach (var e in errors) options.Log?.Invoke("  " + e);
            var zh = Path.Combine(tables, "Localization_zh_CN.json");
            var en = Path.Combine(tables, "Localization_en.json");
            var tw = Path.Combine(tables, "Localization_zh_TW.json");
            if (!File.Exists(zh) || !File.Exists(en) || !File.Exists(tw))
                throw new ExportException("The game's three text tables could not be read: " + string.Join("; ", errors));
            var staged = Path.Combine(work, "loc_en.tsv");
            int count = WriteLocMapsFromTables(zh, en, tw, staged, options);
            Directory.CreateDirectory(directory);
            File.Move(staged, path, overwrite: true);
            foreach (string name in new[] { TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile })
                File.Move(Path.Combine(work, name), Path.Combine(directory, name), overwrite: true);
            return count;
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>與原生解碼後相同的純 JSON 分派；供自造資料測試，不啟動 Lua VM。</summary>
    internal static int WriteLocMapsFromTables(string zh, string en, string tw, string englishPath, Options options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(englishPath))!);
        int count = BuildLocMap(zh, en, englishPath, options).Count;
        TraditionalChineseMapBuilder.Build(zh, en, tw, Path.GetDirectoryName(Path.GetFullPath(englishPath))!,
            NativeFirstTraditionalOverrides(zh, en, tw, options));
        return count;
    }

    private static IReadOnlyList<string> NativeFirstTraditionalOverrides(string zh, string en, string tw, Options options)
    {
        using var simplified = JsonDocument.Parse(File.ReadAllBytes(zh));
        using var english = JsonDocument.Parse(File.ReadAllBytes(en));
        using var native = JsonDocument.Parse(File.ReadAllBytes(tw));
        var known = simplified.RootElement.EnumerateObject().Select(p => p.Name)
            .Concat(english.RootElement.EnumerateObject().Select(p => p.Name)).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string source in options.TraditionalChineseOverrides)
        {
            var accepted = new List<string>();
            foreach (string raw in source.TrimStart('\uFEFF').Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string[] cells = line.Split('\t');
                if (cells is ["sid", var id, _])
                {
                    // 自有覆寫只補本世代缺字，不取代有效的原生 SID，也不把別世代 SID 植入玩家檔案。
                    if (!known.Contains(id)) continue;
                    if (native.RootElement.TryGetProperty(id, out var row) && row.TryGetProperty("_kDes", out var text)
                        && text.ValueKind == JsonValueKind.String && text.GetString() is { } value
                        && !string.IsNullOrWhiteSpace(value) && value != id && value != "None"
                        && !value.StartsWith("Not Find Key", StringComparison.Ordinal)) continue;
                }
                accepted.Add(line);
            }
            result.Add(string.Join('\n', accepted));
        }
        return result;
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
