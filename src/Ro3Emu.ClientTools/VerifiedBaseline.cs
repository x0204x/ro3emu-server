using System.Text;
using System.Globalization;
using System.Security.Cryptography;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.ClientTools;

/// <summary>
/// The complete file list of an official client after the signed Genesis manifest has been applied and verified.
/// The release publisher consumes this list so it cannot accidentally include logs, caches or unrelated local files.
/// </summary>
public static class VerifiedBaseline
{
    public const string RelativePath = ".ro3-update/verified-baseline.txt";
    private const string Header = "RO3RECOVERYVERIFIEDBASELINE1";

    public sealed record Entry(long Size, string Sha256, string Path);

    /// <summary>由原始baseline位元組讀取官方身分，不從資料夾名稱或使用者輸入猜版號。</summary>
    public static GenesisIdentity ReadIdentity(string clientDir)
    {
        byte[] bytes = File.ReadAllBytes(SafeFile(clientDir, RelativePath));
        string[] lines = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n');
        if (lines.Length < 3 || lines[0].TrimEnd('\r') != Header) throw new InvalidDataException("客戶端baseline格式不符。");
        string[] release = lines[1].TrimEnd('\r').Split('\t'), build = lines[2].TrimEnd('\r').Split('\t');
        if (release.Length != 3 || release[0] != "release" || build.Length != 3 || build[0] != "build"
            || !long.TryParse(release[1], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence))
            throw new InvalidDataException("客戶端baseline缺少完整release／build身分。");
        var identity = new GenesisIdentity(sequence, release[2], build[1], build[2].ToLowerInvariant(), Hash(bytes));
        identity.Validate();
        return identity;
    }

    /// <summary>所有原件逐檔SHA驗證；Managed已修補時，只允許同官方SHA的pristine backup。</summary>
    public static IReadOnlyList<Entry> ValidateSources(string clientDir)
    {
        var identity = ReadIdentity(clientDir);
        var entries = Read(clientDir);
        foreach (var entry in entries) _ = ResolveOriginal(clientDir, entry);
        ValidateManagedSet(clientDir, entries);
        if (ReadIdentity(clientDir) != identity) throw new InvalidDataException("核對來源時baseline已變動。");
        return entries;
    }

    public static string ResolveOriginal(string clientDir, Entry entry)
    {
        string source = SafeFile(clientDir, entry.Path);
        if (Matches(source, entry)) return source;
        if (entry.Path.StartsWith("ro3_Data/Managed/", StringComparison.OrdinalIgnoreCase))
        {
            string backup = SafeFile(clientDir, "ro3emu-backup/" + System.IO.Path.GetFileName(entry.Path));
            if (Matches(backup, entry)) return backup;
        }
        throw new InvalidDataException("客戶端原件與官方baseline SHA不符：" + entry.Path);
    }

    private static bool Matches(string path, Entry entry) => File.Exists(path) && new FileInfo(path).Length == entry.Size
        && HashFile(path) == entry.Sha256;

    private static void ValidateManagedSet(string clientDir, IReadOnlyList<Entry> entries)
    {
        const string prefix = "ro3_Data/Managed/";
        var expected = entries.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0) throw new InvalidDataException("官方baseline缺少Managed檔案集合。");
        var allowedExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { prefix + "Ro3Emu.ClientHooks.dll", prefix + "Ro3Emu.Loc.dll" };
        string managed = SafeFile(clientDir, prefix.TrimEnd('/'));
        var pending = new Stack<string>();
        pending.Push(managed);
        while (pending.TryPop(out string? folder))
            foreach (string file in Directory.EnumerateFileSystemEntries(folder))
            {
                string relative = System.IO.Path.GetRelativePath(clientDir, file).Replace('\\', '/');
                _ = SafeFile(clientDir, relative);
                if (Directory.Exists(file)) { pending.Push(file); continue; }
                if (!expected.Contains(relative) && !allowedExtras.Contains(relative))
                    throw new InvalidDataException("Managed含有未列入官方來源或允許修補的檔案：" + relative);
            }
    }

    internal static string SafeFile(string clientDir, string relative)
    {
        if (!SafePath(relative)) throw new InvalidDataException("不合法的客戶端相對路徑。");
        string root = System.IO.Path.GetFullPath(clientDir), path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("客戶端路徑超出來源目錄。");
        for (string? at = path; at != null; at = System.IO.Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("客戶端來源不允許reparse point：" + at);
        return path;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static IReadOnlyList<Entry> Read(string clientDir)
    {
        string path = System.IO.Path.Combine(clientDir, RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException("not an installed RO3 client (no verified baseline)", path);
        var lines = File.ReadAllLines(path);
        if (lines.Length < 3 || lines[0] != Header) throw new InvalidDataException($"unknown baseline format in {path}");

        var result = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(3))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length != 3 || !long.TryParse(parts[0], out long size) || size < 0 || !IsSha256(parts[1]) || !SafePath(parts[2]))
                throw new InvalidDataException($"bad baseline line: {line}");
            if (!seen.Add(parts[2])) throw new InvalidDataException($"duplicate baseline path: {parts[2]}");
            result.Add(new Entry(size, parts[1].ToLowerInvariant(), parts[2]));
        }
        if (result.Count == 0) throw new InvalidDataException($"empty baseline in {path}");
        return result;
    }

    public static void Write(string clientDir, string releaseId, long sequence, string buildId, string contentSha256,
        IEnumerable<Entry> entries)
    {
        string directory = System.IO.Path.Combine(clientDir, ".ro3-update");
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "verified-baseline.txt");
        string temporary = path + ".new";
        var rows = entries.ToList();
        if (rows.Count == 0) throw new InvalidDataException("cannot write an empty verified baseline");
        if (rows.Any(e => e.Size < 0 || !IsSha256(e.Sha256) || !SafePath(e.Path)))
            throw new InvalidDataException("cannot write an invalid verified baseline entry");
        if (rows.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new InvalidDataException("cannot write duplicate verified baseline paths");

        using (var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.NewLine = "\n";
            writer.WriteLine(Header);
            writer.WriteLine($"release\t{sequence}\t{releaseId}");
            writer.WriteLine($"build\t{buildId}\t{contentSha256}");
            foreach (var entry in rows) writer.WriteLine($"{entry.Size}\t{entry.Sha256.ToLowerInvariant()}\t{entry.Path.Replace('\\', '/')}");
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool SafePath(string path) => path.Length > 0 && !path.StartsWith('/') && !path.StartsWith('\\') && !path.Contains(':')
        && path.Split('/', '\\').All(part => part.Length > 0 && part != "." && part != "..");
}
