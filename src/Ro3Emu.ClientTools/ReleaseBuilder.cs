using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Ro3Emu.Patcher;

namespace Ro3Emu.ClientTools;

/// <summary>只以完整已驗Genesis來源建立玩家套件；原件不在修補範圍內。</summary>
public static class ReleaseBuilder
{
    private const string ManagedPrefix = "ro3_Data/Managed/";
    private const string HooksFile = "Ro3Emu.ClientHooks.dll", LocFile = "Ro3Emu.Loc.dll";

    public static async Task<int> PublishAsync(string clientDir, string serverUrl, string pem, string repoDir, string version, string channel, int compression,
        string? localizationMap = null, bool skillTrace = false, RepositorySigningOptions? signing = null, string? traditionalChinesePack = null,
        string? serverVersion = null)
    {
        clientDir = Path.GetFullPath(clientDir);
        var baseline = VerifiedBaseline.ValidateSources(clientDir);
        var identity = VerifiedBaseline.ReadIdentity(clientDir);
        string fingerprint = GetPatchFingerprint(clientDir, serverUrl, pem, localizationMap, traditionalChinesePack);
        var extras = ExtraFiles(clientDir, localizationMap, traditionalChinesePack);
        string staging = Directory.CreateTempSubdirectory("ro3emu-release-").FullName;
        try
        {
            StageOriginals(clientDir, baseline, staging);
            var (hooks, loc) = StagePayloads(staging);
            ClientPatcher.Patch(staging, NormalizeUrl(serverUrl), NormalizeKey(pem), hooks, loc);
            var files = SelectFiles(clientDir, staging, baseline);
            files.AddRange(extras);
            if (ReleaseFileSources.SkillTraceMarker(staging, skillTrace) is { } marker) files.Add(marker);
            var hashes = ExpectedHashes(files, baseline);
            if (VerifiedBaseline.ReadIdentity(clientDir) != identity
                || GetPatchFingerprint(clientDir, serverUrl, pem, localizationMap, traditionalChinesePack) != fingerprint)
                throw new InvalidDataException("封裝期間來源身分或修補輸入已變動。");
            string patch = skillTrace ? Hash(Encoding.UTF8.GetBytes(fingerprint + "\nskillTrace=true")) : fingerprint;
            var result = await Publisher.PublishAsync(new PublishOptions
            {
                Files = files, ExpectedSourceHashes = hashes, RepoDirectory = repoDir, Version = version, Channel = channel,
                LaunchExe = "ro3.exe", CompressionLevel = compression, Signing = signing,
                VerifyExistingChannel = serverVersion is not null,
                BaseClient = serverVersion is null ? null : identity, ServerVersion = serverVersion,
                PatchSha256 = serverVersion is null ? null : patch,
            }, new Progress<string>(Console.WriteLine));
            Console.WriteLine($"done: {result.NewChunks} new chunks, {result.ReusedChunks} reused");
            return 0;
        }
        finally { RemoveStaging(staging); }
    }

    /// <summary>工具、實際hook／loc位元組、公開端點／RSA公鑰與語系內容的穩定指紋。</summary>
    public static string GetPatchFingerprint(string clientDir, string serverUrl, string pem,
        string? localizationMap = null, string? traditionalChinesePack = null)
    {
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = Hashing.Sha256File(AssemblyFile(typeof(ReleaseBuilder).Assembly)),
            ["patcher"] = Hashing.Sha256File(AssemblyFile(typeof(Publisher).Assembly)),
            ["hooks"] = Hash(Payload(HooksFile, "Ro3Emu.ClientHooks")), ["loc"] = Hash(Payload(LocFile, "Ro3Emu.Loc")),
            ["serverUrl"] = NormalizeUrl(serverUrl), ["publicKey"] = NormalizeKey(pem),
            ["verbose"] = ClientPatcher.VerboseLogs ? "true" : "false",
        };
        foreach (var (destination, source) in ExtraFiles(clientDir, localizationMap, traditionalChinesePack))
        {
            GuardSource(source);
            parts["extra:" + destination] = Hashing.Sha256File(source);
        }
        return Hash(Encoding.UTF8.GetBytes(string.Concat(parts.Select(p => p.Key + "\0" + p.Value + "\n"))));
    }

    private static List<KeyValuePair<string, string>> ExtraFiles(string clientDir, string? map, string? pack)
    {
        var files = ReleaseFileSources.TraditionalChinesePack(clientDir, pack).ToList();
        if (ReleaseFileSources.LocalizationMap(clientDir, map) is { } localization) files.Add(localization);
        return files;
    }

    private static void StageOriginals(string clientDir, IReadOnlyList<VerifiedBaseline.Entry> baseline, string staging)
    {
        foreach (var entry in baseline.Where(e => e.Path.StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase) || e.Path == "ro3.exe"))
        {
            string source = VerifiedBaseline.ResolveOriginal(clientDir, entry);
            string target = PathRules.ToLocal(staging, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidDataException("缺少暫存上層路徑。"));
            File.Copy(source, target);
            if (Hashing.Sha256File(target) != entry.Sha256) throw new InvalidDataException("暫存原件SHA不符：" + entry.Path);
        }
    }

    private static (string Hooks, string Loc) StagePayloads(string staging)
    {
        string folder = Path.Combine(staging, "payloads");
        Directory.CreateDirectory(folder);
        string hooks = Path.Combine(folder, HooksFile), loc = Path.Combine(folder, LocFile);
        File.WriteAllBytes(hooks, Payload(HooksFile, "Ro3Emu.ClientHooks"));
        File.WriteAllBytes(loc, Payload(LocFile, "Ro3Emu.Loc"));
        return (hooks, loc);
    }

    private static List<KeyValuePair<string, string>> SelectFiles(string clientDir, string staging, IReadOnlyList<VerifiedBaseline.Entry> baseline)
    {
        var allowed = ClientPatcher.PatchedFiles.Append("RO3.AsiaProfile.dll").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<KeyValuePair<string, string>>();
        foreach (var entry in baseline)
        {
            bool managed = entry.Path.StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase);
            string source = managed ? PathRules.ToLocal(staging, entry.Path) : VerifiedBaseline.SafeFile(clientDir, entry.Path);
            if (managed && !allowed.Contains(Path.GetFileName(entry.Path)) && Hashing.Sha256File(source) != entry.Sha256)
                throw new InvalidDataException("未列為允許修補的Managed檔案已變動：" + entry.Path);
            files.Add(new(entry.Path, source));
        }
        foreach (string file in new[] { HooksFile, LocFile })
        {
            string relative = ManagedPrefix + file;
            if (!files.Any(f => f.Key.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                files.Add(new(relative, PathRules.ToLocal(staging, relative)));
        }
        return files;
    }

    private static Dictionary<string, string> ExpectedHashes(List<KeyValuePair<string, string>> files, IReadOnlyList<VerifiedBaseline.Entry> baseline)
    {
        var originals = baseline.ToDictionary(e => e.Path, e => e.Sha256, StringComparer.Ordinal);
        return files.ToDictionary(f => f.Key, f => !f.Key.StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase)
            && originals.TryGetValue(f.Key, out string? original) ? original : Hashing.Sha256File(f.Value), StringComparer.Ordinal);
    }

    private static byte[] Payload(string name, string project)
    {
        using var embedded = typeof(ReleaseBuilder).Assembly.GetManifestResourceStream(name);
        if (embedded != null) { using var buffer = new MemoryStream(); embedded.CopyTo(buffer); return buffer.ToArray(); }
        foreach (string path in PayloadCandidates(name, project))
            if (File.Exists(path)) { GuardSource(path); return File.ReadAllBytes(path); }
        throw new FileNotFoundException("缺少已建置的修補元件：" + name);
    }

    private static IEnumerable<string> PayloadCandidates(string name, string project)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "client", name);
        yield return Path.Combine(AppContext.BaseDirectory, name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            foreach (string configuration in new[] { "Release", "Debug" })
                yield return Path.Combine(dir.FullName, "src", project, "bin", configuration, "netstandard2.1", name);
    }

    private static string AssemblyFile(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? throw new InvalidDataException("修補工具組件沒有名稱。");
        if (Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process).Equals(name, StringComparison.OrdinalIgnoreCase))
            return process;
        foreach (string path in new[] { Path.Combine(AppContext.BaseDirectory, name + ".dll"), Path.Combine(AppContext.BaseDirectory, name + ".exe") })
            if (File.Exists(path)) return path;
        return Environment.ProcessPath ?? throw new InvalidDataException("無法驗證目前修補工具位元組。");
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("伺服器端點必須是不含帳密、query或fragment的HTTP(S) URL。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string NormalizeKey(string pem)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new ArgumentException("修補指紋只接受RSA公鑰。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.ExportRSAPublicKeyPem().Replace("\r\n", "\n").Trim();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void GuardSource(string path)
    {
        for (string? at = Path.GetFullPath(path); at != null; at = Path.GetDirectoryName(at))
            if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("修補輸入不允許reparse point：" + at);
    }

    private static void RemoveStaging(string staging)
    {
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(staging);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("ro3emu-release-", StringComparison.Ordinal))
            throw new InvalidDataException("拒絕刪除非本次封裝的暫存目錄。");
        GuardSource(full);
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.TryPop(out string? directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("暫存目錄含reparse point，已保留供檢查。");
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        Directory.Delete(full, recursive: true);
    }
}
