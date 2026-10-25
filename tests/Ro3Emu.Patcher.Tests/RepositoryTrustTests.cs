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

    async Task PublishAsync() => await Publisher.PublishAsync(new PublishOptions
    {
        Files = Publisher.CollectDirectory(Build, "", _ => false),
        RepoDirectory = Repo,
        Version = "1.0.0",
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
}
