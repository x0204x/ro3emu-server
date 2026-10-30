using Ro3Emu.Patcher;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Publishes a client patched for one server into a launcher repository (src/Ro3Emu.Launcher installs from it).
/// The file set is exactly the Genesis install: the RO3 launcher's verified baseline
/// (<c>.ro3-update/verified-baseline.txt</c>, size + path per file). The DLLs this tool patches are rebuilt from the
/// originals in <c>ro3emu-backup</c> inside a staging copy of <c>ro3_Data/Managed</c>, so the source client may itself be
/// patched for another server. Logs, caches and our own folders are never published.
/// </summary>
public static class ReleaseBuilder
{
    private const string ManagedPrefix = "ro3_Data/Managed/";

    public static async Task<int> PublishAsync(string clientDir, string serverUrl, string pem, string repoDir, string version, string channel, int compression,
        string? localizationMap = null, bool skillTrace = false, RepositorySigningOptions? signing = null, string? traditionalChinesePack = null)
    {
        clientDir = Path.GetFullPath(clientDir);
        var baseline = VerifiedBaseline.Read(clientDir);
        var traditional = ReleaseFileSources.TraditionalChinesePack(clientDir, traditionalChinesePack);
        Console.WriteLine($"baseline: {baseline.Count} files");

        // Stage ro3.exe + Managed + the pristine originals, then patch the staging copy only.
        string staging = Path.Combine(Path.GetTempPath(), "ro3emu-release-" + version);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        string stagedManaged = Path.Combine(staging, "ro3_Data", "Managed");
        Directory.CreateDirectory(stagedManaged);
        File.Copy(Path.Combine(clientDir, "ro3.exe"), Path.Combine(staging, "ro3.exe"));
        foreach (var file in Directory.GetFiles(Path.Combine(clientDir, "ro3_Data", "Managed")))
            File.Copy(file, Path.Combine(stagedManaged, Path.GetFileName(file)));
        string backup = Path.Combine(clientDir, "ro3emu-backup");
        if (Directory.Exists(backup))
        {
            Directory.CreateDirectory(Path.Combine(staging, "ro3emu-backup"));
            foreach (var file in Directory.GetFiles(backup))
                File.Copy(file, Path.Combine(staging, "ro3emu-backup", Path.GetFileName(file)));
        }
        ClientPatcher.Patch(staging, serverUrl, pem);

        var files = new List<KeyValuePair<string, string>>();
        var mismatched = new List<string>();
        foreach (var entry in baseline)
        {
            string relative = entry.Path;
            if (relative.StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new(relative, Path.Combine(stagedManaged, relative[ManagedPrefix.Length..])));
                continue;
            }
            string source = Path.Combine(clientDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(source);
            if (!info.Exists || info.Length != entry.Size)
                mismatched.Add($"{relative} (baseline {entry.Size}, disk {(info.Exists ? info.Length : -1)})");
            files.Add(new(relative, source));
        }
        if (mismatched.Count > 0)
            throw new InvalidOperationException($"{mismatched.Count} files differ from the Genesis install, e.g. {string.Join("; ", mismatched.Take(5))}");
        foreach (var extra in Directory.GetFiles(stagedManaged).Select(Path.GetFileName))
        {
            string relative = ManagedPrefix + extra;
            if (!files.Any(f => string.Equals(f.Key, relative, StringComparison.OrdinalIgnoreCase)))
                files.Add(new(relative, Path.Combine(stagedManaged, extra!)));
        }
        if (ReleaseFileSources.LocalizationMap(clientDir, localizationMap) is { } localization) files.Add(localization);
        files.AddRange(traditional);
        if (ReleaseFileSources.SkillTraceMarker(staging, skillTrace) is { } marker) files.Add(marker);
        Console.WriteLine($"publishing {files.Count} files as {channel}/{version} into {repoDir}");

        var progress = new Progress<string>(Console.WriteLine);
        var result = await Publisher.PublishAsync(new PublishOptions
        {
            Files = files,
            RepoDirectory = repoDir,
            Version = version,
            Channel = channel,
            LaunchExe = "ro3.exe",
            CompressionLevel = compression,
            Signing = signing,
        }, progress);
        Console.WriteLine($"done: {result.NewChunks} new chunks ({result.NewStoredBytes / 1048576} MiB stored), {result.ReusedChunks} reused");
        Directory.Delete(staging, recursive: true);
        return 0;
    }
}
