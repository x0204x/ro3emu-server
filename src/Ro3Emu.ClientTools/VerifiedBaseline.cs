using System.Text;

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
