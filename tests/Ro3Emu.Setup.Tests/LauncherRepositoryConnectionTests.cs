using Ro3Emu.Launcher;
using Ro3Emu.Patcher;

namespace Ro3Emu.Setup.Tests;

public sealed class LauncherRepositoryConnectionTests
{
    [Fact]
    public void TheConfiguredAddressIsReadWithoutChangingRepositoryOrTrust()
    {
        string directory = Directory.CreateTempSubdirectory("ro3-origin-config-").FullName;
        try
        {
            string file = Path.Combine(directory, "launcher.json");
            File.WriteAllText(file, """
                { "repository": "https://hiroshi-pc3.taila2669f.ts.net/ro3/",
                  "repositoryConnectionAddress": "100.126.246.106",
                  "requireHttpsRepository": true, "requireSignedChannel": true,
                  "trustedPublisherKeys": { "test": "unchanged-test-public-key" } }
                """);
            LauncherConfig config = LauncherConfig.Load(file);
            Assert.Equal("100.126.246.106", config.RepositoryConnectionAddress);
            Assert.Equal("https://hiroshi-pc3.taila2669f.ts.net/ro3/", config.Repository);
            Assert.True(config.RepositoryTrust.RequireHttps);
            Assert.True(config.RepositoryTrust.RequireSignedChannel);
            Assert.Equal("unchanged-test-public-key", config.RepositoryTrust.TrustedPublicKeys["test"]);
            using var repository = new RepositoryClient(new Uri(config.Repository), config.RepositoryTrust,
                connectionAddress: config.RepositoryConnectionAddress);
            Assert.Equal(config.Repository, repository.BaseUri.AbsoluteUri);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AConnectionAddressDoesNotAuthorizeAnOldHttpConfiguration()
    {
        var config = new LauncherConfig
        {
            Repository = "http://100.126.246.106:8080/ro3/",
            RepositoryConnectionAddress = "100.126.246.106",
        };
        Assert.Throws<PatcherException>(() => new RepositoryClient(new Uri(config.Repository), config.RepositoryTrust,
            connectionAddress: config.RepositoryConnectionAddress));
    }
}
