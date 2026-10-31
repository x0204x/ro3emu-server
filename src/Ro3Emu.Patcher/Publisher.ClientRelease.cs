using System.Text.Json;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher;

public static partial class Publisher
{
    /// <summary>核對本機已發布通道；有公鑰 trust 時重新驗簽，絕不讀取私鑰。</summary>
    public static ClientReleaseRequirement ReadClientRequirement(string repoDirectory, string channelName,
        RepositoryTrustOptions? trust = null)
    {
        if (string.IsNullOrWhiteSpace(channelName) || channelName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new InvalidDataException("更新通道名稱不合法。");
        string repo = Path.GetFullPath(repoDirectory);
        string path = ContractPath(repo, $"channels/{channelName}.json");
        byte[] bytes = File.ReadAllBytes(path);
        if (trust?.RequireSignedChannel == true)
            RepositorySignatures.Verify(bytes, File.ReadAllBytes(ContractPath(repo, $"channels/{channelName}.json.sig")), trust);
        var channel = JsonSerializer.Deserialize<ChannelInfo>(bytes, Json.Compact)
            ?? throw new InvalidDataException("通道不可為空。");
        if (channel.Channel != channelName || channel.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("通道身分不符或已過期。");
        string manifestPath = ContractPath(repo, channel.Manifest);
        if (Hashing.Sha256File(manifestPath) != channel.ManifestSha256) throw new InvalidDataException("Manifest SHA 不符。");
        ClientMetadata.EnsureMatches(Manifest.FromCompressedBytes(File.ReadAllBytes(manifestPath)), channel);
        var expected = ClientMetadata.ToRequirement(channel);
        string sidecar = ContractPath(repo, ClientReleaseRequirement.FileName);
        if (File.Exists(sidecar))
        {
            var actual = JsonSerializer.Deserialize<ClientReleaseRequirement>(File.ReadAllBytes(sidecar), Json.Compact)
                ?? throw new InvalidDataException("客戶端契約不可為空。");
            actual.Validate();
            if (actual != expected) throw new InvalidDataException("客戶端契約與已發布通道不符。");
        }
        return expected;
    }

    /// <summary>不可變檔只保存manifest身分；通道序號會因公告或登入器更新而增加。</summary>
    private static byte[] RequirementIdentity(ChannelInfo channel) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = 1, version = channel.Version, manifestSha256 = channel.ManifestSha256,
        serverVersion = channel.ServerVersion, baseClient = channel.BaseClient, patchSha256 = channel.PatchSha256,
    }, Json.Indented);

    private static void PrepareRequirementIdentity(string repo, ChannelInfo channel)
    {
        if (channel.BaseClient is null)
        {
            if (File.Exists(ContractPath(repo, ClientReleaseRequirement.FileName)))
                throw new InvalidDataException("已有來源契約的更新庫不可發布缺少身分的舊格式。");
            return;
        }
        ClientMetadata.ToRequirement(channel);
        string path = ContractPath(repo, $"requirements/{channel.ManifestSha256}.json");
        byte[] expected = RequirementIdentity(channel);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("不可變的manifest身分契約已有不同內容。");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException("契約缺少上層目錄。"));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(expected);
        stream.Flush(true);
    }

    private static void WriteClientRequirement(string repo, ChannelInfo channel)
    {
        if (channel.BaseClient is null) return;
        string path = ContractPath(repo, ClientReleaseRequirement.FileName), temporary = path + ".new";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ClientMetadata.ToRequirement(channel), Json.Indented);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string ContractPath(string root, string relative)
    {
        string path = PathRules.ToLocal(root, relative);
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("來源契約不允許reparse point：" + current);
        return path;
    }
}
