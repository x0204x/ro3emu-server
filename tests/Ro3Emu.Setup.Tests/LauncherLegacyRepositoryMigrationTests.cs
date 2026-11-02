using System.Text.Json;
using Ro3Emu.Launcher;
using Ro3Emu.Patcher;

namespace Ro3Emu.Setup.Tests;

public sealed class LauncherLegacyRepositoryMigrationTests : IDisposable
{
    private const string OldRepository = "http://legacy.example.test:8080/ro3/";
    private const string NewRepository = "https://updates.example.test/ro3/";
    private readonly string _root = Directory.CreateTempSubdirectory("ro3-legacy-origin-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static LauncherConfig Legacy() => new()
    {
        Repository = OldRepository, Channel = "live", Server = "http://game.example.test:5380",
        RequireSignedChannel = true, TrustedPublisherKeys = new() { ["publisher"] = "fixture-public-key" },
        ClientDirectory = "custom-client", ServerName = "玩家自訂名稱", PanelUrl = "https://panel.example.test/",
    };

    private static LauncherConfig Embedded()
    {
        var config = Legacy();
        config.Repository = NewRepository;
        config.RepositoryConnectionAddress = "100.64.0.1";
        config.RequireHttpsRepository = true;
        config.LegacyHttpRepositories = [OldRepository];
        return config;
    }

    private LauncherConfig Load(LauncherConfig external, LauncherConfig embedded)
    {
        string path = Path.Combine(_root, "launcher.json");
        string original = JsonSerializer.Serialize(external);
        File.WriteAllText(path, original);
        LauncherConfig result = LauncherConfig.Load(path, embedded);
        Assert.Equal(original, File.ReadAllText(path));
        return result;
    }

    private void SaveSettings(string path, string directory, long sequence)
        => File.WriteAllText(path, JsonSerializer.Serialize(new UserSettings
        {
            ClientDirectory = directory, AcceptedChannelSequences = new() { ["live"] = sequence },
        }));

    [Fact]
    public void LegacySidecarMigratesBeforeRepositoryValidationAndKeepsCustomValuesAndSequence()
    {
        string oldSettings = UserSettings.RepositoryFilePath(OldRepository, _root);
        SaveSettings(oldSettings, "previously-selected-client", 29);
        SaveSettings(UserSettings.RepositoryFilePath(NewRepository, _root), "unrelated-client", 1);
        LauncherConfig config = Load(Legacy(), Embedded());

        Assert.Equal(NewRepository, config.Repository);
        Assert.Equal("100.64.0.1", config.RepositoryConnectionAddress);
        Assert.True(config.RequireHttpsRepository);
        Assert.True(config.RequireSignedChannel);
        Assert.Equal("fixture-public-key", config.TrustedPublisherKeys["publisher"]);
        Assert.Equal("custom-client", config.ClientDirectory);
        Assert.Equal("玩家自訂名稱", config.ServerName);
        Assert.Equal("https://panel.example.test/", config.PanelUrl);
        Assert.Equal(oldSettings, config.ResolveSettingsFile(_root, _root));
        UserSettings settings = config.LoadSettings(_root, _root);
        Assert.Equal("previously-selected-client", settings.ClientDirectory);
        Assert.Throws<PatcherException>(() => settings.AcceptChannelSequence("live", 28));
        settings.AcceptChannelSequence("live", 30);
        Assert.Equal(30, Load(Legacy(), Embedded()).LoadSettings(_root, _root).AcceptedChannelSequences["live"]);
        using var repository = new RepositoryClient(new Uri(config.Repository), config.RepositoryTrust);
        Assert.Equal(NewRepository, repository.BaseUri.AbsoluteUri);
    }

    [Fact]
    public void ExistingSharedSettingsFallbackIsPreservedOnlyForPreviouslyImplicitIdentity()
    {
        SaveSettings(Path.Combine(_root, "launcher-settings.json"), "legacy-shared-client", 29);
        LauncherConfig config = Load(Legacy(), Embedded());
        UserSettings settings = config.LoadSettings(_root, _root);
        Assert.Equal("legacy-shared-client", settings.ClientDirectory);
        settings.AcceptChannelSequence("live", 30);
        Assert.Equal(30, UserSettings.Load(UserSettings.RepositoryFilePath(OldRepository, _root)).AcceptedChannelSequences["live"]);

        var explicitIdentity = Legacy();
        explicitIdentity.SettingsIdentity = "custom-identity";
        Assert.Null(Load(explicitIdentity, Embedded()).LoadSettings(_root, _root).ClientDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitStorageLocationIsNeverReplaced(bool useFile)
    {
        var external = Legacy();
        external.SettingsIdentity = "custom-identity";
        if (useFile) external.SettingsFile = "custom-settings.json";
        string path = external.ResolveSettingsFile(_root, _root);
        SaveSettings(path, "custom-storage-client", 35);
        LauncherConfig config = Load(external, Embedded());
        Assert.Equal(NewRepository, config.Repository);
        Assert.Equal(path, config.ResolveSettingsFile(_root, _root));
        Assert.Equal("custom-storage-client", config.LoadSettings(_root, _root).ClientDirectory);
        Assert.Equal(35, config.LoadSettings(_root, _root).AcceptedChannelSequences["live"]);
    }

    [Theory]
    [InlineData("other-origin")]
    [InlineData("other-path")]
    [InlineData("other-channel")]
    [InlineData("other-server")]
    [InlineData("other-key")]
    [InlineData("extra-key")]
    [InlineData("missing-key")]
    [InlineData("unsigned-external")]
    [InlineData("unsigned-embedded")]
    [InlineData("insecure-target")]
    [InlineData("https-not-required")]
    [InlineData("external-allowlist-only")]
    [InlineData("already-https")]
    public void UnrelatedOrUntrustedConfigurationsAreNeverMigrated(string change)
    {
        var external = Legacy();
        var embedded = Embedded();
        switch (change)
        {
            case "other-origin": external.Repository = "http://custom.example.test/ro3/"; break;
            case "other-path": external.Repository = OldRepository + "another/"; break;
            case "other-channel": external.Channel = "test"; break;
            case "other-server": external.Server = "http://custom.example.test:5380"; break;
            case "other-key": external.TrustedPublisherKeys["publisher"] = "other-public-key"; break;
            case "extra-key": external.TrustedPublisherKeys["another"] = "another-key"; break;
            case "missing-key": external.TrustedPublisherKeys.Clear(); break;
            case "unsigned-external": external.RequireSignedChannel = false; break;
            case "unsigned-embedded": embedded.RequireSignedChannel = false; break;
            case "insecure-target": embedded.Repository = "http://updates.example.test/ro3/"; break;
            case "https-not-required": embedded.RequireHttpsRepository = false; break;
            case "external-allowlist-only": embedded.LegacyHttpRepositories = []; external.LegacyHttpRepositories = [OldRepository]; break;
            case "already-https": external.Repository = NewRepository; break;
        }
        LauncherConfig result = Load(external, embedded);
        Assert.Equal(JsonSerializer.Serialize(external), JsonSerializer.Serialize(result));
    }

    [Fact]
    public void EmbeddedReleaseRequirementCanOnlyStrengthenExistingConfiguration()
    {
        var external = Legacy();
        external.RequireServerClientRelease = false;
        Assert.True(Load(external, Embedded()).RequireServerClientRelease);
        var embedded = Embedded();
        embedded.RequireServerClientRelease = false;
        Assert.True(Load(Legacy(), embedded).RequireServerClientRelease);
    }
}
