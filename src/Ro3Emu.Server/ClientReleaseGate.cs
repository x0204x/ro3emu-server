using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.Server;

public sealed class ClientReleaseOptions
{
    public bool Required { get; set; }
    public string? RequirementPath { get; set; }
}

/// <summary>開始監聽之前的純檔案 gate；不建立資料庫、不執行遊戲迴圈。</summary>
public static class ClientReleaseGate
{
    public const string ExportIdentityFile = "client-source.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string ServerVersion => typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServerHost).Assembly.GetName().Version!.ToString();

    public static ClientReleaseRequirement? Read(ServerOptions options, string? serverVersion = null)
    {
        var policy = options.ClientRelease;
        if (!policy.Required && string.IsNullOrWhiteSpace(policy.RequirementPath)) return null;
        if (string.IsNullOrWhiteSpace(policy.RequirementPath)) throw new InvalidDataException("已要求玩家套件契約，但未設定 RequirementPath，拒絕開服。");
        var requirement = ReadJson<ClientReleaseRequirement>(policy.RequirementPath);
        requirement.Validate();
        if (!requirement.MatchesServerVersion(serverVersion ?? ServerVersion))
            throw new InvalidDataException("玩家套件對應的伺服器版本不符，拒絕開服。");
        var data = options.GameDataDir ?? options.DataDir;
        string curated = Path.Combine(data, "curated-import.json");
        GenesisIdentity identity;
        if (File.Exists(curated))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(curated));
            var root = document.RootElement;
            if (!root.TryGetProperty("Format", out var format) || format.GetInt32() != 1
                || !root.TryGetProperty("Client", out var client)) throw new InvalidDataException("匯入資料來源契約格式錯誤。");
            identity = client.Deserialize<GenesisIdentity>(Json) ?? throw new InvalidDataException("匯入資料沒有客戶端來源。");
            if (!root.TryGetProperty("Files", out var files)) throw new InvalidDataException("匯入資料缺少檔案清單。");
            GameDataIntegrity.Validate(data, files.Deserialize<SortedDictionary<string, DataFileRecord>>(Json));
        }
        else
        {
            var stamp = ReadJson<ExportIdentity>(Path.Combine(data, ExportIdentityFile));
            if (stamp.Format != 1 || !ClientReleaseRequirement.IsHash(stamp.ExportSha256)
                || Hash(Path.Combine(data, "export.json")) != stamp.ExportSha256)
                throw new InvalidDataException("匯出資料戳記與客戶端來源不一致。");
            identity = stamp.BaseClient;
            GameDataIntegrity.Validate(data, stamp.Files);
        }
        identity.Validate();
        if (identity != requirement.BaseClient) throw new InvalidDataException("伺服器資料與必要 Genesis 客戶端來源不同，拒絕開服。");
        return requirement;
    }

    private sealed record ExportIdentity(int Format, GenesisIdentity BaseClient, string ExportSha256,
        SortedDictionary<string, DataFileRecord>? Files = null);
    private static T ReadJson<T>(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 16 * 1024 * 1024) throw new InvalidDataException("找不到有效的客戶端／資料來源契約：" + path);
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("來源契約不可為 null。");
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
