using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher.Tests;

/// <summary>自造檔案與記憶體測試簽署金鑰；不讀原版玩家檔案或正式私鑰。</summary>
public sealed class PublisherClientContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-publisher-contract-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _signer = RSA.Create(3072);
    private const string KeyId = "synthetic-publisher";
    private string Repo => Path.Combine(_root, "repo");
    private string Source => Path.Combine(_root, "Game.exe");
    private RepositorySigningOptions Signing => new(KeyId, _signer.ExportRSAPrivateKeyPem());
    private RepositoryTrustOptions Trust => new()
    {
        RequireSignedChannel = true,
        TrustedPublicKeys = new Dictionary<string, string> { [KeyId] = _signer.ExportSubjectPublicKeyInfoPem() }
    };
    private static GenesisIdentity Identity(long n) => new(n, "release-" + n, "build-" + n, new string('a', 64), new string('b', 64));

    public PublisherClientContractTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Source, "合成遊戲檔，不可執行");
    }

    private Task<PublishResult> Publish(string version, long official = 7, string server = "1.0.24", bool verify = false,
        LauncherRelease? launcher = null) => Publisher.PublishAsync(new PublishOptions
        {
            Files = [new("Game.exe", Source)], RepoDirectory = Repo, Channel = "stable", Version = version, LaunchExe = "Game.exe",
            BaseClient = Identity(official), ServerVersion = server, PatchSha256 = new string('c', 64), Signing = Signing,
            Workers = 1, CompressionLevel = 1,
            VerifyExistingChannel = verify, Launcher = launcher,
        });

    [Fact]
    public async Task PublishesACompleteRequirementAndRejectsATamperedSidecar()
    {
        var published = await Publish("first");
        var requirement = Publisher.ReadClientRequirement(Repo, "stable", Trust);
        Assert.Equal(Identity(7), requirement.BaseClient);
        Assert.Equal(published.Channel.Sequence, requirement.Sequence);
        Assert.Equal(published.Channel.ManifestSha256, requirement.ManifestSha256);
        string sidecar = Path.Combine(Repo, ClientReleaseRequirement.FileName);
        File.WriteAllText(sidecar, JsonSerializer.Serialize(requirement with { ServerVersion = "different" }));
        Assert.Throws<InvalidDataException>(() => Publisher.ReadClientRequirement(Repo, "stable", Trust));
    }

    [Fact]
    public async Task ValidSignatureDoesNotPermitDifferentChannelAndManifestIdentity()
    {
        var published = await Publish("first");
        published.Channel.BaseClient = Identity(8);
        string path = Path.Combine(Repo, "channels", "stable.json");
        byte[] changed = JsonSerializer.SerializeToUtf8Bytes(published.Channel, Json.Indented);
        File.WriteAllBytes(path, changed);
        File.WriteAllBytes(path + ".sig", RepositorySignatures.Sign(changed, Signing));
        Assert.Throws<InvalidDataException>(() => Publisher.ReadClientRequirement(Repo, "stable", Trust));
        await Assert.ThrowsAsync<InvalidDataException>(() => Publisher.SignExistingChannelAsync(Repo, "stable", Signing));
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AuxiliaryReleasesAndRollbackPreserveTheTargetManifestIdentity()
    {
        var first = await Publish("first");
        await Publish("second", 8, "1.0.25");
        await Publisher.PublishLauncherAsync(Repo, "stable", Source, "2.0.99", Signing);
        string news = Path.Combine(_root, "news.json");
        File.WriteAllText(news, "{\"items\":[]}");
        await Publisher.PublishNewsAsync(Repo, "stable", news, Signing);
        var renewed = await Publisher.SignExistingChannelAsync(Repo, "stable", Signing);
        Assert.Equal(Identity(8), Publisher.ReadClientRequirement(Repo, "stable", Trust).BaseClient);
        var rollback = await Publisher.SetChannelAsync(Repo, "stable", "first", null, Signing);
        var contract = Publisher.ReadClientRequirement(Repo, "stable", Trust);
        Assert.True(rollback.Sequence > renewed.Sequence);
        Assert.Equal(Identity(7), contract.BaseClient);
        Assert.Equal("1.0.24", contract.ServerVersion);
        Assert.Equal(first.Channel.ManifestSha256, contract.ManifestSha256);
        Assert.NotNull(rollback.Launcher);
        Assert.NotNull(rollback.News);
    }

    [Fact]
    public async Task NewGamePackagePreservesVerifiedAuxiliaryPointersAndUsesNewContract()
    {
        await Publish("first");
        var launcher = await Publisher.PublishLauncherAsync(Repo, "stable", Source, "2.0.13", Signing);
        string news = Path.Combine(_root, "news.json");
        File.WriteAllText(news, "{\"items\":[{\"title\":\"合成公告\"}]}");
        var previous = await Publisher.PublishNewsAsync(Repo, "stable", news, Signing);
        var second = await Publish("second", 8, "1.0.25", verify: true);
        var requirement = Publisher.ReadClientRequirement(Repo, "stable", Trust);
        Assert.Equal(JsonSerializer.Serialize(launcher.Launcher), JsonSerializer.Serialize(second.Channel.Launcher));
        Assert.Equal(JsonSerializer.Serialize(previous.News), JsonSerializer.Serialize(second.Channel.News));
        Assert.True(second.Channel.Sequence > previous.Sequence);
        Assert.Equal(Identity(8), requirement.BaseClient);
        Assert.Equal("1.0.25", requirement.ServerVersion);
        Assert.Equal(second.Manifest.Version, requirement.Version);
        Assert.Equal(second.Channel.ManifestSha256, requirement.ManifestSha256);
        Assert.Equal(second.Channel.PatchSha256, requirement.PatchSha256);
        string channel = Path.Combine(Repo, "channels", "stable.json");
        RepositorySignatures.Verify(File.ReadAllBytes(channel), File.ReadAllBytes(channel + ".sig"), Trust);
    }

    [Fact]
    public async Task StrictPublishRefusesTamperedSignedChannelBeforeWritingNewRelease()
    {
        var previous = await Publish("first");
        string channel = Path.Combine(Repo, "channels", "stable.json");
        previous.Channel.Published = previous.Channel.Published.AddSeconds(-1);
        byte[] changed = JsonSerializer.SerializeToUtf8Bytes(previous.Channel, Json.Indented);
        File.WriteAllBytes(channel, changed);
        await Assert.ThrowsAsync<PatcherException>(() => Publish("second", verify: true));
        Assert.Equal(changed, File.ReadAllBytes(channel));
        Assert.False(File.Exists(Path.Combine(Repo, "manifests", "second.json.zst")));
    }

    [Fact]
    public async Task StrictPublishRefusesMissingSignatureWithoutChangingExistingPublication()
    {
        await Publish("first");
        string channel = Path.Combine(Repo, "channels", "stable.json");
        byte[] before = File.ReadAllBytes(channel);
        string contract = Path.Combine(Repo, ClientReleaseRequirement.FileName);
        byte[] requirement = File.ReadAllBytes(contract);
        File.Delete(channel + ".sig");
        await Assert.ThrowsAsync<PatcherException>(() => Publish("second", verify: true));
        Assert.Equal(before, File.ReadAllBytes(channel));
        Assert.Equal(requirement, File.ReadAllBytes(contract));
        Assert.False(File.Exists(Path.Combine(Repo, "manifests", "second.json.zst")));
    }

    [Fact]
    public async Task ExplicitLauncherOverridesPreservedLauncherWithoutDroppingNews()
    {
        await Publish("first");
        await Publisher.PublishLauncherAsync(Repo, "stable", Source, "2.0.12", Signing);
        string news = Path.Combine(_root, "news.json");
        File.WriteAllText(news, "{\"items\":[]}");
        var previous = await Publisher.PublishNewsAsync(Repo, "stable", news, Signing);
        string replacementPath = "launcher/2.0.13/RO3Launcher.exe";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Repo, replacementPath))!);
        File.Copy(Source, Path.Combine(Repo, replacementPath));
        var replacement = new LauncherRelease { Version = "2.0.13", Url = replacementPath, Sha256 = Hashing.Sha256File(Source) };
        var result = await Publish("second", verify: true, launcher: replacement);
        Assert.Same(replacement, result.Channel.Launcher);
        Assert.Equal(JsonSerializer.Serialize(previous.News), JsonSerializer.Serialize(result.Channel.News));
        Assert.Equal(result.Channel.Sequence, Publisher.ReadClientRequirement(Repo, "stable", Trust).Sequence);
    }

    [Fact]
    public async Task ASourceHashMismatchNeverCreatesAManifestOrChannel()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Publisher.PublishAsync(new PublishOptions
        {
            Files = [new("Game.exe", Source)], ExpectedSourceHashes = new Dictionary<string, string> { ["Game.exe"] = new string('0', 64) },
            RepoDirectory = Repo, Channel = "stable", Version = "bad", LaunchExe = "Game.exe", Workers = 1,
        }));
        Assert.False(File.Exists(Path.Combine(Repo, "manifests", "bad.json.zst")));
        Assert.False(File.Exists(Path.Combine(Repo, "channels", "stable.json")));
    }

    [Fact]
    public async Task ImmutableIdentityConflictIsRefusedBeforeChangingTheChannel()
    {
        var published = await Publish("first");
        string path = Path.Combine(Repo, "channels", "stable.json");
        byte[] before = File.ReadAllBytes(path);
        File.WriteAllText(Path.Combine(Repo, "requirements", published.Channel.ManifestSha256 + ".json"), "wrong immutable identity");
        await Assert.ThrowsAsync<InvalidDataException>(() => Publisher.SignExistingChannelAsync(Repo, "stable", Signing));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
