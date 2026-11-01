using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Ro3Emu.Server.Updates;

/// <summary>An update step that cannot go on; its message is shown to the admin as it is.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// A release package on its way to the staging folder: downloaded and checked against the SHA-256 GitHub lists for it
/// (refused without one), unpacked with every entry checked (regular files under the package's one folder, no link,
/// no path out of it, bounded sizes), and the result checked: the server and the RO3Emu tool are there and the server
/// says it is the release's version.
/// </summary>
public static class UpdatePackage
{
    /// <summary>Bounds on a package (a release is about 100 MB packed, 250 MB unpacked, 8 files).</summary>
    public const long ArchiveMax = 1L << 30, UnpackedMax = 2L << 30;
    public const int EntriesMax = 1000;

    /// <summary>
    /// Downloads <paramref name="asset"/> to <paramref name="target"/> (written as .part and renamed once checked). The
    /// size must be the one GitHub lists and the SHA-256 its digest; a mismatch deletes the download.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, ReleaseAsset asset, string target, string userAgent, IProgress<long> progress,
        CancellationToken ct)
    {
        if (asset.Sha256 is not { } expected)
            throw new UpdateException("GitHub lists no SHA-256 digest for " + asset.Name + ", so the download could not be checked. It was not downloaded.");
        if (asset.Size <= 0 || asset.Size > ArchiveMax) throw new UpdateException(asset.Name + " is not the size of a release package.");
        string partial = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            string actual = await CopyAsync(http, asset, partial, userAgent, progress, ct);
            long length = new FileInfo(partial).Length;
            if (length != asset.Size)
                throw new UpdateException($"The download has {length} bytes, the release lists {asset.Size}. It was deleted.");
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The download's SHA-256 is not the one GitHub lists for " + asset.Name + ". It was deleted.");
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Streams the asset to <paramref name="file"/>, hashing as it goes; its SHA-256 in lower case hex.</summary>
    private static async Task<string> CopyAsync(HttpClient http, ReleaseAsset asset, string file, string userAgent, IProgress<long> progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The download failed: HTTP {(int)response.StatusCode}."));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        byte[] buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            done += read;
            if (done > asset.Size) throw new UpdateException("The download is larger than the release lists. It was deleted.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            progress.Report(done);
        }
        await target.FlushAsync(ct);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The file's SHA-256 in lower case hex.</summary>
    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Unpacks the package into <paramref name="staging"/> (emptied first) and returns its folder: staging/RO3Emu-server
    /// or staging/RO3Emu. Executable bits of the tarball are kept on Linux.
    /// </summary>
    public static string Extract(string archive, UpdatePlatform platform, string staging)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string root = Path.Combine(staging, platform.Folder);
        var budget = new Budget();
        if (platform.IsTarball) ExtractTar(archive, platform, root, budget);
        else ExtractZip(archive, platform, root, budget);
        if (!Directory.Exists(root)) throw new UpdateException("The package has no " + platform.Folder + " folder.");
        return root;
    }

    private sealed class Budget
    {
        public long Bytes;
        public int Entries;

        public void Take(long bytes)
        {
            Bytes += Math.Max(0, bytes);
            if (++Entries > EntriesMax || Bytes > UnpackedMax) throw new UpdateException("The package holds more than a release does; it was not unpacked.");
        }
    }

    private static void ExtractTar(string archive, UpdatePlatform platform, string root, Budget budget)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        try
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new UpdateException("The package holds " + entry.Name + ", which is not a plain file.");
                budget.Take(entry.Length);
                string target = Target(root, platform, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    entry.DataStream?.CopyTo(output);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, entry.Mode & (UnixFileMode)0b111_111_111);
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new UpdateException("The package is not a readable archive: " + e.Message);
        }
    }

    private static void ExtractZip(string archive, UpdatePlatform platform, string root, Budget budget)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') && entry.Length == 0) continue;
                budget.Take(entry.Length);
                string target = Target(root, platform, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch (InvalidDataException e)
        {
            throw new UpdateException("The package is not a readable archive: " + e.Message);
        }
    }

    /// <summary>
    /// Where an entry goes: its name must be the package folder, a slash and a relative path of plain names (no "..",
    /// no drive, no empty or "." part), and it must land inside <paramref name="root"/>.
    /// </summary>
    internal static string Target(string root, UpdatePlatform platform, string name)
    {
        string path = name.Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        string prefix = platform.Folder + "/";
        string rest = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : "";
        string[] parts = rest.Split('/');
        if (rest.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':', StringComparison.Ordinal)))
            throw new UpdateException("The package holds an entry outside its " + platform.Folder + " folder: " + name);
        string fullRoot = Path.GetFullPath(root);
        string target = Path.GetFullPath(Path.Combine([fullRoot, .. parts]));
        if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UpdateException("The package holds an entry outside its " + platform.Folder + " folder: " + name);
        return target;
    }

    /// <summary>
    /// The unpacked package is a server release of <paramref name="expected"/>: the server, the RO3Emu tool and the
    /// default settings are there, and the server's own version is the tag's. Returns that version text.
    /// </summary>
    public static string Check(string root, UpdatePlatform platform, ReleaseVersion expected)
    {
        foreach (string file in new[] { platform.ServerFile, platform.ToolFile, UpdatePlatform.Settings[0] })
            if (!File.Exists(Path.Combine(root, file))) throw new UpdateException("The package has no " + file + ".");
        string? version = BundleVersion.Read(Path.Combine(root, platform.ServerFile));
        if (!ReleaseVersion.TryParse(version, out var found))
            throw new UpdateException("The package's server does not say which version it is.");
        if (found != expected)
            throw new UpdateException("The package's server is version " + found + ", not the release's " + expected + ".");
        return version!;
    }

    internal static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
