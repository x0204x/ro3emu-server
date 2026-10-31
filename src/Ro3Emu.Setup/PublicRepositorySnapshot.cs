using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;
using Ro3Emu.Patcher;

namespace Ro3Emu.Setup;

/// <summary>保留整個既有公開更新站；不可變內容以 hardlink 重用，mutable metadata 各自複製。</summary>
internal static class PublicRepositorySnapshot
{
    public static void Clone(string source, string destination)
    {
        CheckSeparate(source, destination);
        EnsureNoLinks(source);
        EnsureNoLinks(destination);
        if (!Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新站根目錄不可為連結或重新解析點。");
        foreach (string path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新站含有連結或重新解析點，拒絕複製候選。");
            if ((attributes & FileAttributes.Directory) != 0) continue;
            string relative = Path.GetRelativePath(source, path);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string first = relative.Split(Path.DirectorySeparatorChar)[0];
            bool immutable = first is "chunks" or "manifests" or "launchers";
            if (!immutable || !TryLink(target, path)) File.Copy(path, target);
        }
    }

    public static ClientReleaseRequirement Verify(string repo, string channelName, RepositoryTrustOptions trust, CancellationToken ct = default)
    {
        EnsureNoLinks(repo);
        EnsureNoLinks(Path.Combine(repo, "channels"));
        var requirement = Publisher.ReadClientRequirement(repo, channelName, trust);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(repo, "channels"), "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var channel = JsonSerializer.Deserialize<ChannelInfo>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("空白更新通道。");
            EnsureNoLinks(path);
            string manifestPath = Local(repo, channel.Manifest);
            if (Hashing.Sha256File(manifestPath, ct: ct) != channel.ManifestSha256) throw new InvalidDataException("更新站 manifest SHA 不符。");
            var manifest = Manifest.FromCompressedBytes(File.ReadAllBytes(manifestPath));
            if (channel.Launcher is { } launcher) CheckAsset(repo, launcher.Url, launcher.Sha256, ct);
            if (channel.News is { } news) CheckAsset(repo, news.Url, news.Sha256, ct);
            bool selected = channel.Channel == channelName;
            foreach (var file in manifest.Files)
            {
                using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytes = 0;
                foreach (var chunk in file.Chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    string storedPath = Local(repo, PathRules.ChunkRelative(chunk.Hash));
                    if (!File.Exists(storedPath) || new FileInfo(storedPath).Length != chunk.StoredSize)
                        throw new InvalidDataException("更新站缺少 manifest 所需的 chunk，保留原通道。");
                    if (!selected) continue; // 其它通道保留完整 closure；選中通道逐塊解壓核 raw SHA 及整檔 SHA。
                    using var decompressor = new ZstdSharp.Decompressor();
                    byte[] raw = decompressor.Unwrap(File.ReadAllBytes(storedPath)).ToArray();
                    if (raw.Length != chunk.Size || Hashing.Sha256Hex(raw) != chunk.Hash)
                        throw new InvalidDataException("更新站 chunk 的原始大小或 SHA 不符。");
                    whole.AppendData(raw); bytes += raw.Length;
                }
                if (selected && (bytes != file.Size || Convert.ToHexStringLower(whole.GetHashAndReset()) != file.Sha256))
                    throw new InvalidDataException("更新站組合後的整檔 SHA 不符。");
            }
        }
        return requirement;
    }

    private static void CheckAsset(string repo, string path, string hash, CancellationToken ct)
    {
        if (Hashing.Sha256File(Local(repo, path), ct: ct) != hash) throw new InvalidDataException("更新站公告或登入器 SHA 不符。");
    }

    private static string Local(string repo, string relative)
    {
        string path = PathRules.ToLocal(repo, relative);
        EnsureNoLinks(path);
        return path;
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新站路徑或祖先含有連結／重新解析點，拒絕複製或切換。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void CheckSeparate(string source, string destination)
    {
        string a = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string b = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("來源與候選不可相同或互相包含。");
    }

    internal static void ValidateActivation(string repo, string candidate)
    {
        CheckSeparate(repo, candidate);
        EnsureNoLinks(repo); EnsureNoLinks(candidate);
        string root = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stage = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = "." + Path.GetFileName(root) + ".candidate-";
        string name = Path.GetFileName(stage);
        if (!string.Equals(Path.GetDirectoryName(root), Path.GetDirectoryName(stage), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException("候選必須是同目錄下由本次建立的獨立 snapshot 名稱。");
    }

    private static bool TryLink(string target, string source)
    {
        try { return OperatingSystem.IsWindows() ? CreateHardLink(target, source, IntPtr.Zero) : Link(source, target) == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string target, string source, IntPtr reserved);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string target);
}
