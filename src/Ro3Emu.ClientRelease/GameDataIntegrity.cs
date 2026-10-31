using System.Security.Cryptography;

namespace Ro3Emu.ClientRelease;

public sealed record DataFileRecord(long Length, string Sha256);

/// <summary>與 GameDataExport 的核心輸出集合一致；不含資料庫、圖片、日誌及私鑰。</summary>
public static class GameDataIntegrity
{
    private static readonly string[] Folders = ["config", "sceneeditor", "skills", "nav"];
    private static readonly string[] TopFiles = ["msgtable.tsv", "loc_en.tsv", "export.json"];

    public static SortedDictionary<string, DataFileRecord> Capture(string root)
    {
        var files = new SortedDictionary<string, DataFileRecord>(StringComparer.Ordinal);
        foreach (string relative in Paths(root))
        {
            string path = SafeFile(root, relative);
            using var stream = File.OpenRead(path);
            files.Add(relative, new(stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        return files;
    }

    public static void Validate(string root, IReadOnlyDictionary<string, DataFileRecord>? files)
    {
        if (files is null || files.Count == 0) throw new InvalidDataException("缺少核心資料檔案清單。");
        var expected = files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count != files.Count || !expected.SetEquals(Paths(root)))
            throw new InvalidDataException("核心資料檔案集合與已核對清單不同。");
        foreach (var (relative, record) in files)
        {
            if (record is null || record.Length < 0 || !ClientReleaseRequirement.IsHash(record.Sha256))
                throw new InvalidDataException("核心資料檔案記錄不完整。");
            using var stream = File.OpenRead(SafeFile(root, relative));
            if (stream.Length != record.Length || Convert.ToHexStringLower(SHA256.HashData(stream)) != record.Sha256)
                throw new InvalidDataException("核心資料長度或 SHA 不符：" + relative);
        }
    }

    private static IEnumerable<string> Paths(string root)
    {
        EnsureNoLinks(root);
        foreach (string folder in Folders)
        {
            string start = Path.Combine(root, folder);
            if (!Directory.Exists(start)) throw new InvalidDataException("核心資料目錄缺失：" + folder);
            var todo = new Stack<DirectoryInfo>();
            todo.Push(new(start));
            int count = 0;
            while (todo.TryPop(out var directory))
            {
                EnsureNoLinks(directory.FullName);
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("核心資料不可使用目錄或檔案連結。");
                    if (entry is DirectoryInfo child) todo.Push(child);
                    else { count++; yield return Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'); }
                }
            }
            if (count == 0) throw new InvalidDataException("核心資料目錄為空：" + folder);
        }
        _ = SafeFile(root, "config/MapResource.json");
        foreach (string file in TopFiles) { _ = SafeFile(root, file); yield return file; }
    }

    private static string SafeFile(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or "..") || Path.IsPathRooted(relative))
            throw new InvalidDataException("核心資料路徑不安全。");
        string path = Path.Combine(Path.GetFullPath(root), relative.Replace('/', Path.DirectorySeparatorChar));
        EnsureNoLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException("缺少核心資料檔案：" + relative);
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (FileSystemInfo? part = new FileInfo(Path.GetFullPath(path)); part is not null;
            part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
        {
            if ((File.Exists(part.FullName) || Directory.Exists(part.FullName))
                && (File.GetAttributes(part.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("核心資料及其上層路徑不可使用連結。");
        }
    }
}
