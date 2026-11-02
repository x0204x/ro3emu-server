using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>launcher.json: built into the exe (LauncherConfigFile in the csproj); a file beside the exe wins.</summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("repository")] public string Repository { get; set; } = "http://localhost:8790/";
    /// <summary>選用的核准私有來源 TCP 位址；不變更 HTTPS 主機、TLS 驗證或其他來源的 DNS。</summary>
    [JsonPropertyName("repositoryConnectionAddress")] public string? RepositoryConnectionAddress { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "test";
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "Ragnarok 3";
    [JsonPropertyName("registerUrl")] public string? RegisterUrl { get; set; }
    [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
    [JsonPropertyName("discordUrl")] public string? DiscordUrl { get; set; }
    [JsonPropertyName("facebookUrl")] public string? FacebookUrl { get; set; }
    [JsonPropertyName("youtubeUrl")] public string? YoutubeUrl { get; set; }
    /// <summary>選用的網頁面板網址；僅接受 HTTP(S)，未設定時不顯示入口，也不自行啟用面板。</summary>
    [JsonPropertyName("panelUrl")] public string? PanelUrl { get; set; }

    [JsonIgnore]
    public string? ConfiguredPanelUrl => PanelUrl?.Trim() is { Length: > 0 } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
        ? url : null;
    [JsonPropertyName("allowedExternalHosts")] public string[] AllowedExternalHosts { get; set; } = [];
    [JsonPropertyName("uiFixture")] public string? UiFixture { get; set; }
    [JsonPropertyName("requireHttpsRepository")] public bool RequireHttpsRepository { get; set; }
    [JsonPropertyName("requireSignedChannel")] public bool RequireSignedChannel { get; set; }
    [JsonPropertyName("requireServerClientRelease")] public bool RequireServerClientRelease { get; set; } = true;
    [JsonPropertyName("trustedPublisherKeys")] public Dictionary<string, string> TrustedPublisherKeys { get; set; } = [];
    [JsonPropertyName("settingsFile")] public string? SettingsFile { get; set; }
    /// <summary>只選擇本機設定檔的固定身份，供更新來源遷移時保留目錄與序號；不是下載網址或信任來源。</summary>
    [JsonPropertyName("settingsIdentity")] public string? SettingsIdentity { get; set; }
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; } = ".";

    static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LauncherConfig Load(string besideExe)
    {
        try
        {
            if (File.Exists(besideExe)) return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllBytes(besideExe), Options) ?? new();
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json");
            return stream is null ? new() : JsonSerializer.Deserialize<LauncherConfig>(stream, Options) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public RepositoryTrustOptions RepositoryTrust => new()
    {
        RequireHttps = RequireHttpsRepository,
        RequireSignedChannel = RequireSignedChannel,
        TrustedPublicKeys = TrustedPublisherKeys,
    };

    public string ResolveSettingsFile(string executableDirectory)
        => ResolveSettingsFile(executableDirectory, UserSettings.SettingsDirectory);

    internal string ResolveSettingsFile(string executableDirectory, string settingsDirectory)
        => !string.IsNullOrWhiteSpace(SettingsFile) ? Path.GetFullPath(SettingsFile, executableDirectory)
            : SettingsIdentity is not null ? UserSettings.IdentityFilePath(SettingsIdentity, settingsDirectory)
            : UserSettings.RepositoryFilePath(Repository, settingsDirectory);

    public UserSettings LoadSettings(string executableDirectory)
        => LoadSettings(executableDirectory, UserSettings.SettingsDirectory);

    /// <summary>明確指定身份或檔案時不回落共用設定；資料夾參數只供隔離測試，不從 launcher.json 讀取。</summary>
    internal UserSettings LoadSettings(string executableDirectory, string settingsDirectory)
        => !string.IsNullOrWhiteSpace(SettingsFile) || SettingsIdentity is not null
            ? UserSettings.Load(ResolveSettingsFile(executableDirectory, settingsDirectory))
            : UserSettings.ForRepository(Repository, settingsDirectory);

    public string? ResolveClientDirectory(string executableDirectory)
        => string.IsNullOrWhiteSpace(ClientDirectory)
            ? null
            : Path.GetFullPath(ClientDirectory, executableDirectory);
}

/// <summary>各更新來源分開保存玩家設定；明確指定 settingsFile 的測試／發佈配置仍優先。</summary>
public sealed class UserSettings
{
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; }
    [JsonPropertyName("acceptedChannelSequences")] public Dictionary<string, long> AcceptedChannelSequences { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] string _filePath = DefaultFilePath;
    // 舊共用檔僅供未指定 settingsFile 的來源設定首次承接，不覆蓋各來源已建立的檔案。
    internal static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu");
    public static string DefaultFilePath => Path.Combine(SettingsDirectory, "launcher-settings.json");

    public static string RepositoryFilePath(string repositoryUrl) => RepositoryFilePath(repositoryUrl, SettingsDirectory);

    internal static string RepositoryFilePath(string repositoryUrl, string settingsDirectory) =>
        Path.Combine(settingsDirectory, "launcher-settings-" + FileKey(repositoryUrl) + ".json");

    public static UserSettings ForRepository(string repositoryUrl) => ForRepository(repositoryUrl, SettingsDirectory);

    internal static UserSettings ForRepository(string repositoryUrl, string settingsDirectory) =>
        LoadCore(RepositoryFilePath(repositoryUrl, settingsDirectory), Path.Combine(settingsDirectory, "launcher-settings.json"));

    internal static string IdentityFilePath(string identity, string settingsDirectory)
    {
        if (identity.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(identity[0]) || !char.IsAsciiLetterOrDigit(identity[^1])
            || identity.Any(c => !(char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c) || c is '.' or '-' or '_')))
            throw new PatcherException("settingsIdentity 必須是 1 至 128 字元的小寫英數本機身份，可含 .、-、_，首尾須為英數；不能是網址或路徑。");
        return Path.Combine(settingsDirectory, "launcher-settings-" + identity + ".json");
    }

    internal static string FileKey(string repositoryUrl)
    {
        string key = Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            ? uri.Host + (uri.IsDefaultPort ? "" : "_" + uri.Port) + uri.AbsolutePath : repositoryUrl;
        var chars = key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? char.ToLowerInvariant(c) : '_').ToArray();
        return new string(chars).Trim('_', '.');
    }

    public static UserSettings Load(string? filePath = null) => LoadCore(filePath ?? DefaultFilePath);

    private static UserSettings LoadCore(string filePath, string? legacyFile = null)
    {
        filePath = Path.GetFullPath(filePath);
        try
        {
            string? source = File.Exists(filePath) ? filePath : legacyFile is not null && File.Exists(legacyFile) ? legacyFile : null;
            var settings = source != null ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(source)) ?? new() : new();
            settings._filePath = filePath;
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UserSettings { _filePath = filePath };
        }
    }

    public bool Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(this));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void AcceptChannelSequence(string channel, long sequence)
    {
        long accepted = AcceptedChannelSequences.GetValueOrDefault(channel);
        if (sequence < accepted)
        {
            throw new PatcherException($"The update channel sequence went backwards ({sequence} < {accepted}); update refused.");
        }
        if (sequence > accepted)
        {
            AcceptedChannelSequences[channel] = sequence;
            if (!Save()) throw new PatcherException("The accepted update sequence could not be saved.");
        }
    }
}
