using System.Text.Json;
using Ro3Emu.Launcher;
using Ro3Emu.Patcher;

namespace Ro3Emu.Setup.Tests;

/// <summary>更新來源改為 HTTPS 時只承接本機設定身份，不承接舊的網路來源或放寬驗證。</summary>
public sealed class LauncherSettingsIdentityTests : IDisposable
{
    private const string OldRepository = "http://100.126.246.106:8080/ro3/";
    private const string OldIdentity = "100.126.246.106_8080_ro3";
    private const string NewRepository = "https://server.example.test/ro3/";
    private readonly string _root = Directory.CreateTempSubdirectory("ro3-settings-identity-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Save(string path, string directory, long sequence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllText(path, JsonSerializer.Serialize(new UserSettings
        {
            ClientDirectory = directory,
            AcceptedChannelSequences = new() { ["live"] = sequence },
        }));
    }

    [Fact]
    public void HttpsMigrationKeepsTheExistingDirectoryAndMonotonicSequenceWithoutChangingTrust()
    {
        string oldPath = UserSettings.RepositoryFilePath(OldRepository, _root);
        string directory = Path.Combine(_root, "chosen-client");
        Save(oldPath, directory, 27);
        var config = new LauncherConfig
        {
            Repository = NewRepository, SettingsIdentity = OldIdentity,
            RequireHttpsRepository = true, RequireSignedChannel = true,
            TrustedPublisherKeys = new() { ["same-publisher"] = "unchanged-test-public-key" },
        };
        Assert.Equal(oldPath, config.ResolveSettingsFile(_root, _root));
        UserSettings settings = config.LoadSettings(_root, _root);
        Assert.Equal(directory, settings.ClientDirectory);
        Assert.Equal(27, settings.AcceptedChannelSequences["live"]);
        Assert.Throws<PatcherException>(() => settings.AcceptChannelSequence("live", 26));
        settings.AcceptChannelSequence("live", 28);
        Assert.Equal(28, config.LoadSettings(_root, _root).AcceptedChannelSequences["live"]);
        Assert.False(File.Exists(UserSettings.RepositoryFilePath(NewRepository, _root)));
        Assert.Equal(NewRepository, config.Repository);
        Assert.True(config.RepositoryTrust.RequireHttps);
        Assert.True(config.RepositoryTrust.RequireSignedChannel);
        Assert.Equal("unchanged-test-public-key", config.RepositoryTrust.TrustedPublicKeys["same-publisher"]);
    }

    [Fact]
    public void ExplicitSettingsFileHasPriorityOverIdentity()
    {
        string path = Path.Combine(_root, "explicit.json");
        Save(path, "explicit-client", 31);
        var config = new LauncherConfig { Repository = NewRepository, SettingsFile = "explicit.json", SettingsIdentity = "../ignored" };
        Assert.Equal(path, config.ResolveSettingsFile(_root, _root));
        Assert.Equal("explicit-client", config.LoadSettings(_root, _root).ClientDirectory);
        Assert.Equal(31, config.LoadSettings(_root, _root).AcceptedChannelSequences["live"]);
    }

    [Fact]
    public void AnExplicitIdentityNeverFallsBackToAnotherSharedSettingsFile()
    {
        Save(Path.Combine(_root, "launcher-settings.json"), "unrelated-client", 70);
        var config = new LauncherConfig { Repository = NewRepository, SettingsIdentity = OldIdentity };
        UserSettings settings = config.LoadSettings(_root, _root);
        Assert.Null(settings.ClientDirectory);
        Assert.Empty(settings.AcceptedChannelSequences);
    }

    [Fact]
    public void WithoutIdentityEachRepositoryKeepsItsOwnSettingsAndTheExistingLegacyFallback()
    {
        Save(UserSettings.RepositoryFilePath(OldRepository, _root), "old-client", 27);
        Save(UserSettings.RepositoryFilePath(NewRepository, _root), "new-client", 30);
        var config = new LauncherConfig { Repository = NewRepository };
        Assert.Equal("new-client", config.LoadSettings(_root, _root).ClientDirectory);
        Assert.Equal(30, config.LoadSettings(_root, _root).AcceptedChannelSequences["live"]);
        var another = new LauncherConfig { Repository = "https://other.example.test/" };
        Assert.Null(another.LoadSettings(_root, _root).ClientDirectory);
        Save(Path.Combine(_root, "launcher-settings.json"), "legacy-client", 20);
        Assert.Equal("legacy-client", another.LoadSettings(_root, _root).ClientDirectory);
        Assert.Equal("new-client", config.LoadSettings(_root, _root).ClientDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://other.example.test/")]
    [InlineData("../other")]
    [InlineData("C:\\other")]
    [InlineData(".hidden")]
    [InlineData("name.")]
    [InlineData("a/b")]
    [InlineData("UPPER")]
    public void IdentityCannotSelectAUrlOrEscapeTheSettingsDirectory(string identity)
    {
        var config = new LauncherConfig { Repository = NewRepository, SettingsIdentity = identity };
        Assert.Throws<PatcherException>(() => config.LoadSettings(_root, _root));
    }

    [Fact]
    public async Task AnExternalJsonStillWinsButStorageIdentityCannotAuthorizeItsOldHttpRepository()
    {
        string path = Path.Combine(_root, "launcher.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new LauncherConfig
        {
            Repository = OldRepository, SettingsIdentity = OldIdentity,
            RequireHttpsRepository = false, RequireSignedChannel = false,
        }));
        LauncherConfig config = LauncherConfig.Load(path);
        Assert.Equal(OldRepository, config.Repository);
        Assert.Equal(OldIdentity, config.SettingsIdentity);
        using var handler = new NoRequestsHandler();
        using var repository = new RepositoryClient(new Uri(config.Repository), config.RepositoryTrust, handler);
        var refused = await Assert.ThrowsAsync<PatcherException>(() => repository.GetChannelAsync("live", CancellationToken.None));
        Assert.Contains("https", refused.Message);
        Assert.Equal(0, handler.Requests);
    }

    private sealed class NoRequestsHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("This storage-only test must refuse HTTP before sending a request.");
        }
    }
}
