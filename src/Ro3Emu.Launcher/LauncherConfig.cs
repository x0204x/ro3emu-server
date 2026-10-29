using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>launcher.json: built into the exe (LauncherConfigFile in the csproj); a file beside the exe wins.</summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("repository")] public string Repository { get; set; } = "http://localhost:8790/";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "test";
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "Ragnarok 3";
    [JsonPropertyName("registerUrl")] public string? RegisterUrl { get; set; }
    [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
    [JsonPropertyName("discordUrl")] public string? DiscordUrl { get; set; }
    [JsonPropertyName("facebookUrl")] public string? FacebookUrl { get; set; }
    [JsonPropertyName("youtubeUrl")] public string? YoutubeUrl { get; set; }
    [JsonPropertyName("allowedExternalHosts")] public string[] AllowedExternalHosts { get; set; } = [];
    [JsonPropertyName("uiFixture")] public string? UiFixture { get; set; }
    [JsonPropertyName("requireHttpsRepository")] public bool RequireHttpsRepository { get; set; }
    [JsonPropertyName("requireSignedChannel")] public bool RequireSignedChannel { get; set; }
    [JsonPropertyName("trustedPublisherKeys")] public Dictionary<string, string> TrustedPublisherKeys { get; set; } = new();
    [JsonPropertyName("settingsFile")] public string? SettingsFile { get; set; }
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
        => string.IsNullOrWhiteSpace(SettingsFile)
            ? UserSettings.RepositoryFilePath(Repository)
            : Path.GetFullPath(SettingsFile, executableDirectory);

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
    public static string DefaultFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu", "launcher-settings.json");

    public static string RepositoryFilePath(string repositoryUrl) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu", "launcher-settings-" + FileKey(repositoryUrl) + ".json");

    public static UserSettings ForRepository(string repositoryUrl) => LoadCore(RepositoryFilePath(repositoryUrl), legacy: true);

    internal static string FileKey(string repositoryUrl)
    {
        string key = Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            ? uri.Host + (uri.IsDefaultPort ? "" : "_" + uri.Port) + uri.AbsolutePath : repositoryUrl;
        var chars = key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? char.ToLowerInvariant(c) : '_').ToArray();
        return new string(chars).Trim('_', '.');
    }

    public static UserSettings Load(string? filePath = null) => LoadCore(filePath ?? DefaultFilePath, legacy: false);

    private static UserSettings LoadCore(string filePath, bool legacy)
    {
        filePath = Path.GetFullPath(filePath);
        try
        {
            string? source = File.Exists(filePath) ? filePath : legacy && File.Exists(DefaultFilePath) ? DefaultFilePath : null;
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
