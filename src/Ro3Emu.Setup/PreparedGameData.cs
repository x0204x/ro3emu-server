using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup;

/// <summary>已整理／匯入資料的獨立契約；不冒充原生完整匯出戳記，也不執行 Lua。</summary>
public static class PreparedGameData
{
    public const string MarkerName = "curated-import.json";
    private const int FormatVersion = 1;
    private const string LuaPrefix = "ro3_Data/StreamingAssets/Recovery/LuaPayload/";
    private const string AssetMeta = "ro3_Data/StreamingAssets/VFS/assetmeta.data";
    private const string Proto = "ro3_Data/StreamingAssets/LocalProto/proto.pb.bytes";
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public sealed record FileRecord(long Length, string Sha256);
    public sealed record ClientIdentity(string BaselineSha256, long Sequence, string ReleaseId, string BuildId, string ContentSha256);
    public sealed record Manifest(int Format, ClientIdentity Client, SortedDictionary<string, FileRecord> Files);

    internal static bool UseIfPresent(string dataDir, string clientDir, bool force)
    {
        string marker = Path.Combine(dataDir, MarkerName);
        if (!File.Exists(marker) && !Directory.Exists(marker)) return false;
        try
        {
            if (force) throw new InvalidDataException("匯入資料不可由 --reexport 自動覆寫；請指定另一個匯出目錄。");
            if (new FileInfo(marker).Length > 16 * 1024 * 1024) throw new InvalidDataException("匯入資料清單過大。");
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(SafeFile(dataDir, MarkerName)), Json)
                ?? throw new InvalidDataException("匯入資料清單不可為 null。");
            Validate(manifest, dataDir, clientDir);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
        {
            throw new SetupException("匯入資料驗證失敗，已停止且未執行原生匯出：" + e.Message,
                "請還原同一發布版的資料、curated-import.json 與客戶端來源，或另選空目錄明確匯出。");
        }
    }

    /// <summary>僅供明確封裝流程建立 sidecar；讀取已驗 baseline 與資料，不寫標記或修改原件。</summary>
    public static Manifest CreateManifest(string dataDir, string clientDir)
    {
        var (identity, entries) = ReadClient(clientDir);
        CheckClientInputs(clientDir, entries);
        var files = new SortedDictionary<string, FileRecord>(StringComparer.Ordinal);
        foreach (string rel in DataPaths(dataDir))
        {
            string path = SafeFile(dataDir, rel);
            files.Add(rel, new(new FileInfo(path).Length, Hash(path)));
        }
        return new(FormatVersion, identity, files);
    }

    /// <summary>明確封裝入口：完整驗證後原子寫入獨立 sidecar；不改 export.json 或資料檔。</summary>
    public static Manifest WriteManifest(string dataDir, string clientDir)
    {
        var manifest = CreateManifest(dataDir, clientDir);
        string marker = Path.Combine(dataDir, MarkerName), temporary = marker + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, marker, overwrite: true);
        return manifest;
    }

    private static void Validate(Manifest manifest, string dataDir, string clientDir)
    {
        if (manifest.Format != FormatVersion || manifest.Client == null || manifest.Files == null || manifest.Files.Count == 0)
            throw new InvalidDataException("未知或不完整的匯入資料清單。");
        var (identity, entries) = ReadClient(clientDir);
        if (manifest.Client != identity) throw new InvalidDataException("客戶端 release／build／content 或 baseline SHA 不相符。");
        CheckClientInputs(clientDir, entries);
        var expected = manifest.Files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count != manifest.Files.Count || !expected.SetEquals(DataPaths(dataDir)))
            throw new InvalidDataException("資料檔案集合已變更。");
        foreach (var (rel, record) in manifest.Files)
        {
            if (record == null) throw new InvalidDataException("資料檔案記錄不可為 null。");
            CheckFile(SafeFile(dataDir, rel), record.Length, record.Sha256);
        }
    }

    private static (ClientIdentity Identity, IReadOnlyList<VerifiedBaseline.Entry> Entries) ReadClient(string clientDir)
    {
        string path = SafeFile(clientDir, VerifiedBaseline.RelativePath);
        // VerifiedBaseline.Write 只寫固定header、release/build與相對路徑／長度／SHA；
        // 不含機器根路徑、時間或檔案mtime，因此同一signed manifest產物可跨機器使用。
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("客戶端 baseline 過大。");
        string baselineHash = Hash(path);
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 3) throw new InvalidDataException("客戶端 baseline 不完整。");
        string[] release = lines[1].Split('\t'), build = lines[2].Split('\t');
        if (release.Length != 3 || release[0] != "release" || !long.TryParse(release[1], NumberStyles.None,
            CultureInfo.InvariantCulture, out long sequence) || sequence <= 0 || string.IsNullOrWhiteSpace(release[2])
            || build.Length != 3 || build[0] != "build" || string.IsNullOrWhiteSpace(build[1]) || !Sha(build[2]))
            throw new InvalidDataException("客戶端 release／build identity 不完整。");
        var entries = VerifiedBaseline.Read(clientDir);
        if (Hash(path) != baselineHash) throw new InvalidDataException("讀取時客戶端 baseline 已變更。");
        return (new(baselineHash, sequence, release[2], build[1], build[2].ToLowerInvariant()), entries);
    }

    private static void CheckClientInputs(string clientDir, IReadOnlyList<VerifiedBaseline.Entry> entries)
    {
        var sources = entries.Where(e => e.Path.StartsWith(LuaPrefix, StringComparison.Ordinal)
            || e.Path == AssetMeta || e.Path == Proto).ToArray();
        if (!sources.Any(e => e.Path.StartsWith(LuaPrefix, StringComparison.Ordinal))
            || !sources.Any(e => e.Path == AssetMeta) || !sources.Any(e => e.Path == Proto))
            throw new InvalidDataException("baseline 缺少資料來源 Lua／VFS／protobuf。");
        var expected = sources.Where(e => e.Path.StartsWith(LuaPrefix, StringComparison.Ordinal)).Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(TreePaths(clientDir, LuaPrefix.TrimEnd('/'))))
            throw new InvalidDataException("客戶端 Lua 來源檔案集合已變更。");
        foreach (var entry in sources) CheckFile(SafeFile(clientDir, entry.Path), entry.Size, entry.Sha256);
    }

    private static IEnumerable<string> DataPaths(string dataDir)
    {
        foreach (string folder in GameDataExport.Folders)
        {
            var paths = TreePaths(dataDir, folder).ToArray();
            if (paths.Length == 0) throw new InvalidDataException("資料目錄缺失或為空：" + folder);
            foreach (string path in paths) yield return path;
        }
        foreach (string name in GameDataExport.TopFiles.Append("export.json"))
        {
            _ = SafeFile(dataDir, name);
            yield return name;
        }
    }

    private static IEnumerable<string> TreePaths(string root, string relative)
    {
        string start = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(start)) throw new DirectoryNotFoundException("缺少目錄：" + relative);
        var todo = new Stack<DirectoryInfo>(); todo.Push(new(start));
        while (todo.TryPop(out var directory))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("匯入來源不可使用連結：" + relative);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("匯入來源不可使用連結：" + entry.Name);
                if (entry is DirectoryInfo child) todo.Push(child);
                else yield return Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            }
        }
    }

    private static string SafeFile(string root, string relative)
    {
        if (!Install.ReleaseManifest.SafePath(relative) || relative.Contains('\\')) throw new InvalidDataException("資料路徑不安全。");
        string path = Path.Combine(Path.GetFullPath(root), relative.Replace('/', Path.DirectorySeparatorChar));
        for (FileSystemInfo? part = new FileInfo(path); part != null && part.FullName != Path.GetFullPath(root);
            part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("資料路徑不可使用連結。");
        if (!File.Exists(path)) throw new FileNotFoundException("缺少已核對的檔案。", path);
        return path;
    }

    private static void CheckFile(string path, long length, string sha)
    {
        if (length < 0 || !Sha(sha) || new FileInfo(path).Length != length || !Hash(path).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("檔案長度或 SHA-256 已變更：" + Path.GetFileName(path));
    }
    private static bool Sha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
