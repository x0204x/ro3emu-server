using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class RepositoryTrustTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-trust-" + Guid.NewGuid().ToString("N"));
    readonly RSA _signer = RSA.Create(3072);
    const string KeyId = "ro3emu-test-2026";

    string Repo => Path.Combine(_root, "repo");
    string Build => Path.Combine(_root, "build");
    string PrivatePem => _signer.ExportRSAPrivateKeyPem();
    string PublicPem => _signer.ExportSubjectPublicKeyInfoPem();
    RepositorySigningOptions Signing => new(KeyId, PrivatePem);
    RepositoryTrustOptions Trust => new()
    {
        RequireSignedChannel = true,
        TrustedPublicKeys = new Dictionary<string, string> { [KeyId] = PublicPem },
    };

    public RepositoryTrustTests()
    {
        Directory.CreateDirectory(Build);
        File.WriteAllText(Path.Combine(Build, "Game.exe"), "test game");
    }

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    async Task PublishAsync(string version = "1.0.0") => await Publisher.PublishAsync(new PublishOptions
    {
        Files = Publisher.CollectDirectory(Build, "", _ => false),
        RepoDirectory = Repo,
        Version = version,
        Channel = "stable",
        LaunchExe = "Game.exe",
        Signing = Signing,
        CompressionLevel = 3,
        Workers = 1,
    });

    [Fact]
    public async Task SignedChannel_WithPinnedKey_IsAccepted()
    {
        await PublishAsync();
        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        ChannelInfo channel = await client.GetChannelAsync("stable", CancellationToken.None);
        Assert.Equal("1.0.0", channel.Version);
        Assert.Equal(1, channel.Sequence);
        Assert.True(channel.Expires > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SignedChannel_Tampering_IsRejected()
    {
        await PublishAsync();
        string path = Path.Combine(Repo, "channels", "stable.json");
        byte[] raw = await File.ReadAllBytesAsync(path);
        raw[raw.Length / 2] ^= 1;
        await File.WriteAllBytesAsync(path, raw);

        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var error = await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));
        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedChannel_UnknownKey_IsRejected()
    {
        await PublishAsync();
        using var other = RSA.Create(3072);
        var trust = new RepositoryTrustOptions
        {
            RequireSignedChannel = true,
            TrustedPublicKeys = new Dictionary<string, string> { ["another-key"] = other.ExportSubjectPublicKeyInfoPem() },
        };
        using var client = new RepositoryClient(new Uri("http://repo.test/"), trust, new FileRepoHandler(Repo));
        var error = await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));
        Assert.Contains("unknown key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedChannel_ExpiredMetadata_IsRejected()
    {
        await PublishAsync();
        string path = Path.Combine(Repo, "channels", "stable.json");
        ChannelInfo channel = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(path), Json.Compact)!;
        channel.Expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(channel, Json.Indented);
        await File.WriteAllBytesAsync(path, raw);
        await File.WriteAllBytesAsync(path + ".sig", RepositorySignatures.Sign(raw, Signing));

        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var error = await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));
        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionPolicy_RejectsHttpRepository()
    {
        var trust = new RepositoryTrustOptions { RequireHttps = true };
        var error = Assert.Throws<PatcherException>(() => new RepositoryClient(new Uri("http://repo.test/"), trust));
        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionPolicy_RejectsHttpsDowngradeResponse()
    {
        var trust = new RepositoryTrustOptions { RequireHttps = true };
        using var client = new RepositoryClient(new Uri("https://repo.test/"), trust, new DowngradeResponseHandler());
        var error = await Assert.ThrowsAsync<PatcherException>(() => client.TryGetBytesAsync("content/news/test.json", CancellationToken.None));
        Assert.Contains("redirected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedChannel_MissingMalformedAndOversizedSignature_IsRejected()
    {
        await PublishAsync();
        string signaturePath = Path.Combine(Repo, "channels", "stable.json.sig");
        byte[] signature = await File.ReadAllBytesAsync(signaturePath);
        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));

        File.Delete(signaturePath);
        await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));

        await File.WriteAllTextAsync(signaturePath, "{}");
        await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));

        await File.WriteAllBytesAsync(signaturePath, new byte[1024 * 1024 + 1]);
        var oversized = await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));
        Assert.Contains("too large", oversized.Message, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllBytesAsync(signaturePath, signature);
        Assert.Equal("1.0.0", (await client.GetChannelAsync("stable", CancellationToken.None)).Version);
    }

    [Fact]
    public async Task SignedRepository_InstallsUpdatesAndRepairs_EndToEnd()
    {
        await File.WriteAllTextAsync(Path.Combine(Build, "data.bin"), "version one");
        await PublishAsync();
        string game = Path.Combine(_root, "install");
        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(game), client);

        var first = await updater.CheckAsync("stable", CancellationToken.None);
        Assert.Equal(1, first.Channel.Sequence);
        Assert.Equal("1.0.0", (await updater.UpdateAsync(first.Channel, false, null, CancellationToken.None)).Version);
        Assert.Equal("version one", await File.ReadAllTextAsync(Path.Combine(game, "data.bin")));

        await File.WriteAllTextAsync(Path.Combine(Build, "data.bin"), "version two");
        await PublishAsync("1.0.1");
        var second = await updater.CheckAsync("stable", CancellationToken.None);
        Assert.Equal(2, second.Channel.Sequence);
        await updater.UpdateAsync(second.Channel, false, null, CancellationToken.None);
        Assert.Equal("version two", await File.ReadAllTextAsync(Path.Combine(game, "data.bin")));

        await File.WriteAllTextAsync(Path.Combine(game, "data.bin"), "damaged");
        await updater.UpdateAsync(second.Channel, true, null, CancellationToken.None);
        Assert.Equal("version two", await File.ReadAllTextAsync(Path.Combine(game, "data.bin")));
        Assert.Empty(updater.QuickVerify(updater.LoadState()!));
    }

    [Fact]
    public async Task SignedNews_TamperIsRejectedBeforeRendering()
    {
        await PublishAsync();
        string newsPath = Path.Combine(_root, "news.json");
        byte[] news = """{"items":[{"title":"簽章公告"}],"banners":[]}"""u8.ToArray();
        await File.WriteAllBytesAsync(newsPath, news);
        await Publisher.PublishNewsAsync(Repo, "stable", newsPath, Signing);
        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        ChannelInfo channel = await client.GetChannelAsync("stable", CancellationToken.None);
        Assert.Equal(news, await client.GetVerifiedBytesAsync(channel.News!, CancellationToken.None));

        string stored = Path.Combine(Repo, channel.News!.Url.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(stored, " ");
        await Assert.ThrowsAsync<PatcherException>(() => client.GetVerifiedBytesAsync(channel.News!, CancellationToken.None));
    }

    [Fact]
    public async Task SignedSelfUpdate_ValidBytesSwapAndTamperLeavesCurrentUntouched()
    {
        await PublishAsync();
        string published = Path.Combine(_root, "published-launcher.exe");
        byte[] next = "next launcher"u8.ToArray();
        await File.WriteAllBytesAsync(published, next);
        ChannelInfo channel = await Publisher.PublishLauncherAsync(Repo, "stable", published, "2.0.0", Signing);
        string current = Path.Combine(_root, "current-launcher.exe");
        byte[] previous = "previous launcher"u8.ToArray();
        await File.WriteAllBytesAsync(current, previous);
        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));

        Assert.True(await SelfUpdate.ApplyAsync(client, channel.Launcher!, "1.0.0", current, CancellationToken.None));
        Assert.Equal(next, await File.ReadAllBytesAsync(current));
        Assert.Equal(previous, await File.ReadAllBytesAsync(current + ".old"));

        File.Move(current + ".old", current, overwrite: true);
        string stored = Path.Combine(Repo, channel.Launcher!.Url.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(stored, "tampered");
        await Assert.ThrowsAsync<PatcherException>(() => SelfUpdate.ApplyAsync(client, channel.Launcher!, "1.0.0", current, CancellationToken.None));
        Assert.Equal(previous, await File.ReadAllBytesAsync(current));
        Assert.False(File.Exists(current + ".new"));
    }

    [Fact]
    public async Task Publisher_RefusesDifferentBytesForExistingLauncherVersion()
    {
        await PublishAsync();
        string launcher = Path.Combine(_root, "launcher.exe");
        await File.WriteAllTextAsync(launcher, "first");
        await Publisher.PublishLauncherAsync(Repo, "stable", launcher, "2.0.0", Signing);
        await File.WriteAllTextAsync(launcher, "different");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Publisher.PublishLauncherAsync(Repo, "stable", launcher, "2.0.0", Signing));
        Assert.Contains("immutable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publisher_RefusesToSignBrokenManifestReference()
    {
        await PublishAsync();
        string channelPath = Path.Combine(Repo, "channels", "stable.json");
        ChannelInfo channel = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(channelPath), Json.Compact)!;
        await File.AppendAllTextAsync(Path.Combine(Repo, channel.Manifest.Replace('/', Path.DirectorySeparatorChar)), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => Publisher.SignExistingChannelAsync(Repo, "stable", Signing));
    }

    [Fact]
    public async Task SignedChannel_RequiresPositiveSequenceAndExpiry()
    {
        await PublishAsync();
        string path = Path.Combine(Repo, "channels", "stable.json");
        ChannelInfo channel = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(path), Json.Compact)!;
        channel.Sequence = 0;
        await RewriteChannelAsync(path, channel);
        using var client = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));

        channel.Sequence = 1;
        channel.Expires = null;
        await RewriteChannelAsync(path, channel);
        await Assert.ThrowsAsync<PatcherException>(() => client.GetChannelAsync("stable", CancellationToken.None));
    }

    async Task RewriteChannelAsync(string path, ChannelInfo channel)
    {
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(channel, Json.Indented);
        await File.WriteAllBytesAsync(path, raw);
        await File.WriteAllBytesAsync(path + ".sig", RepositorySignatures.Sign(raw, Signing));
    }

    sealed class DowngradeResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://repo.test/content/news/test.json"),
                Content = new ByteArrayContent([]),
            });
    }
}
