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
            ? UserSettings.DefaultFilePath
            : Path.GetFullPath(SettingsFile, executableDirectory);
}

/// <summary>Per-user choices (%APPDATA%\RO3Emu\launcher-settings.json): only the client folder so far.</summary>
public sealed class UserSettings
{
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; }
    [JsonPropertyName("acceptedChannelSequences")] public Dictionary<string, long> AcceptedChannelSequences { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore] string _filePath = DefaultFilePath;
    public static string DefaultFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu", "launcher-settings.json");

    public static UserSettings Load(string? filePath = null)
    {
        filePath = Path.GetFullPath(filePath ?? DefaultFilePath);
        try
        {
            var settings = File.Exists(filePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(filePath)) ?? new() : new();
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
