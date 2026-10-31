using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher.Tests;

public sealed class ClientReleaseGateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "client-contract-" + Guid.NewGuid().ToString("N"));
    readonly RSA _signer = RSA.Create(3072);
    const string KeyId = "synthetic-client-contract";
    string Repo => Path.Combine(_root, "repo");
    string Build => Path.Combine(_root, "build");
    string Game => Path.Combine(_root, "game");
    string StatePath => Path.Combine(Game, ".ro3launcher", "state.json");
    static GenesisIdentity BaseClient => new(42, "invented-release", "invented-build", new string('a', 64), new string('b', 64));
    static string PatchSha256 => new('c', 64);
    RepositorySigningOptions Signing => new(KeyId, _signer.ExportRSAPrivateKeyPem());
    RepositoryTrustOptions Trust => new()
    {
        RequireSignedChannel = true,
        TrustedPublicKeys = new Dictionary<string, string> { [KeyId] = _signer.ExportSubjectPublicKeyInfoPem() },
    };

    public ClientReleaseGateTests()
    {
        Directory.CreateDirectory(Build);
        File.WriteAllText(Path.Combine(Build, "ro3.exe"), "synthetic launch target");
        byte[] assets = new byte[32 * 1024];
        new Random(41).NextBytes(assets);
        File.WriteAllBytes(Path.Combine(Build, "scene.assets"), assets);
    }

    public void Dispose()
    {
        _signer.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    Task<PublishResult> PublishAsync(string version = "test-1") => Publisher.PublishAsync(new PublishOptions
    {
        Files = Publisher.CollectDirectory(Build, "", _ => false), RepoDirectory = Repo, Version = version,
        Channel = "stable", LaunchExe = "ro3.exe", Signing = Signing,
        BaseClient = BaseClient, ServerVersion = "9.9.9", PatchSha256 = PatchSha256,
        Chunking = new ChunkingParameters(1024, 4096, 16384), CompressionLevel = 3, Workers = 1,
    });

    static ClientReleaseRequirement Requirement(ChannelInfo channel) => new()
    {
        BaseClient = channel.BaseClient, ServerVersion = channel.ServerVersion ?? "", Channel = channel.Channel,
        Sequence = channel.Sequence, Version = channel.Version, ManifestSha256 = channel.ManifestSha256,
        PatchSha256 = channel.PatchSha256 ?? "",
    };

    static Task<ClientReleaseReady> EnsureAsync(Updater updater, RepositoryClient repository,
        ClientReleaseRequirement required, bool running = false, IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default) => new ClientReleaseSynchronizer(updater, repository, _ => Task.FromResult(required))
            .EnsureReadyAsync("stable", running, () => !running, progress, ct);

    static LocalState ReadState(Updater updater) => updater.ReadStateOnly() ?? throw new InvalidOperationException("Missing test state");

    [Theory]
    [InlineData("base")]
    [InlineData("patch")]
    [InlineData("server")]
    [InlineData("channel")]
    [InlineData("sequence")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("manifest-base")]
    [InlineData("manifest-patch")]
    [InlineData("manifest-server")]
    [InlineData("manifest-version")]
    public async Task GateRejectsAnUnsynchronizedIdentity(string mismatch)
    {
        var release = await PublishAsync();
        var required = Requirement(release.Channel);
        required = mismatch switch
        {
            "base" => required with { BaseClient = BaseClient with { Sequence = 43 } },
            "patch" => required with { PatchSha256 = new string('d', 64) },
            "server" => required with { ServerVersion = "9.9.8" },
            "channel" => required with { Channel = "preview" },
            "sequence" => required with { Sequence = required.Sequence + 1 },
            "version" => required with { Version = "other" },
            "hash" => required with { ManifestSha256 = new string('e', 64) },
            _ => required,
        };
        if (mismatch == "manifest-base") release.Manifest.BaseClient = BaseClient with { Sequence = 43 };
        if (mismatch == "manifest-patch") release.Manifest.PatchSha256 = new string('d', 64);
        if (mismatch == "manifest-server") release.Manifest.ServerVersion = "9.9.8";
        if (mismatch == "manifest-version") release.Manifest.Version = "other";
        Assert.Throws<PatcherException>(() => ClientReleaseGate.Validate(required, release.Channel, release.Manifest));
    }

    [Fact]
    public async Task MetadataOnlyEpochIncreaseKeepsTheSameVerifiedClientPlayable()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        await EnsureAsync(updater, repository, required);
        string news = Path.Combine(_root, "news.json");
        File.WriteAllText(news, "{\"items\":[]}");
        var next = await Publisher.PublishNewsAsync(Repo, "stable", news, Signing);
        Assert.True(next.Sequence > required.Sequence);
        Assert.Equal(required.ManifestSha256, next.ManifestSha256);
        var accepted = new List<long>();
        var sync = new ClientReleaseSynchronizer(updater, repository, _ => Task.FromResult(required),
            channel => accepted.Add(channel.Sequence));
        var ready = await sync.EnsureReadyAsync("stable", true, () => false, null, CancellationToken.None);
        Assert.False(ready.Updated);
        Assert.Equal(next.Sequence, ready.Channel.Sequence);
        Assert.All(accepted, sequence => Assert.Equal(next.Sequence, sequence));
    }

    [Fact]
    public async Task AChannelBelowTheRequiredMinimumEpochIsRejected()
    {
        var release = await PublishAsync();
        var required = Requirement(release.Channel) with { Sequence = release.Channel.Sequence + 1 };
        Assert.Throws<PatcherException>(() => ClientReleaseGate.Validate(required, release.Channel, release.Manifest));
    }

    [Fact]
    public async Task ServerMinimumEpochCanAdvanceDuringASameClientVerification()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var first = Requirement(release.Channel);
        await EnsureAsync(updater, repository, first);
        string news = Path.Combine(_root, "news.json");
        File.WriteAllText(news, "{\"items\":[]}");
        var next = await Publisher.PublishNewsAsync(Repo, "stable", news, Signing);
        int reads = 0;
        var sync = new ClientReleaseSynchronizer(updater, repository,
            _ => Task.FromResult(++reads == 1 ? first : Requirement(next)));
        var ready = await sync.EnsureReadyAsync("stable", true, () => false, null, CancellationToken.None);
        Assert.False(ready.Updated);
        Assert.Equal(next.Sequence, ready.Required.Sequence);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task AHigherEpochForAnotherManifestDoesNotSatisfyTheRequiredClient()
    {
        var first = await PublishAsync();
        var required = Requirement(first.Channel);
        File.WriteAllText(Path.Combine(Build, "scene.assets"), "another synthetic client");
        var second = await PublishAsync("test-2");
        Assert.True(second.Channel.Sequence > required.Sequence);
        Assert.NotEqual(required.ManifestSha256, second.Channel.ManifestSha256);
        Assert.Throws<PatcherException>(() => ClientReleaseGate.Validate(required, second.Channel, second.Manifest));
    }

    [Fact]
    public async Task SameSizeAndTimestampDamageIsFoundAndAutomaticallyRepaired()
    {
        var release = await PublishAsync();
        var handler = new FileRepoHandler(Repo);
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, handler);
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        Assert.True((await EnsureAsync(updater, repository, required)).Updated);
        string path = Path.Combine(Game, "scene.assets");
        DateTime originalTime = File.GetLastWriteTimeUtc(path);
        byte[] damaged = File.ReadAllBytes(path);
        damaged[12345] ^= 0xff;
        File.WriteAllBytes(path, damaged);
        File.SetLastWriteTimeUtc(path, originalTime);
        Assert.Empty(updater.QuickVerify(ReadState(updater)));
        Assert.Contains("scene.assets", (await updater.VerifyInstalledAsync(release.Manifest, null, CancellationToken.None)).ChangedFiles);
        handler.ChunkBytesServed = 0;
        Assert.True((await EnsureAsync(updater, repository, required)).Updated);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Build, "scene.assets")), File.ReadAllBytes(path));
        Assert.True(handler.ChunkBytesServed < damaged.Length);
    }

    [Fact]
    public async Task ManifestVerificationFindsMissingFilesOmittedFromLocalState()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        await EnsureAsync(updater, repository, required);
        var state = ReadState(updater);
        state.Files.Remove("scene.assets");
        state.Save(StatePath);
        File.Delete(Path.Combine(Game, "scene.assets"));
        Assert.Empty(updater.QuickVerify(state));
        Assert.Contains("scene.assets", (await updater.VerifyInstalledAsync(release.Manifest, null, CancellationToken.None)).ChangedFiles);
        var repaired = await EnsureAsync(updater, repository, required);
        Assert.True(repaired.Updated);
        Assert.Contains("scene.assets", repaired.Installed.Files.Keys);
        Assert.True((await updater.VerifyInstalledAsync(release.Manifest, null, CancellationToken.None)).Matches);
    }

    [Fact]
    public async Task ServerFailureDoesNotFallBackToAnOldManagedInstall()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        await EnsureAsync(updater, repository, Requirement(release.Channel));
        string stateHash = Hashing.Sha256File(StatePath);
        var sync = new ClientReleaseSynchronizer(updater, repository, _ => throw new HttpRequestException("offline"));
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.EnsureReadyAsync("stable", false, () => true, null, CancellationToken.None));
        Assert.Equal(stateHash, Hashing.Sha256File(StatePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSignedMetadataNeverFallsBackToTheManagedInstall(bool expired)
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        await EnsureAsync(updater, repository, required);
        string stateHash = Hashing.Sha256File(StatePath);
        release.Channel.Expires = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddDays(1);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(release.Channel, Json.Indented);
        string channelPath = Path.Combine(Repo, "channels", "stable.json");
        File.WriteAllBytes(channelPath, bytes);
        if (expired) File.WriteAllBytes(channelPath + ".sig", RepositorySignatures.Sign(bytes, Signing));
        await Assert.ThrowsAsync<PatcherException>(() => EnsureAsync(updater, repository, required));
        Assert.Equal(stateHash, Hashing.Sha256File(StatePath));
    }

    [Fact]
    public async Task AVerifiedCurrentReleaseCanStartAnotherGameWithoutWrites()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        await EnsureAsync(updater, repository, required);
        string stateHash = Hashing.Sha256File(StatePath);
        var ready = await EnsureAsync(updater, repository, required, running: true);
        Assert.False(ready.Updated);
        Assert.Equal(required, ready.Required);
        Assert.Equal(stateHash, Hashing.Sha256File(StatePath));
    }

    [Fact]
    public async Task RunningGamesBlockANewReleaseWithoutChangingTheOldInstall()
    {
        var first = await PublishAsync();
        var handler = new FileRepoHandler(Repo);
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, handler);
        var updater = new Updater(new InstallLayout(Game), repository);
        await EnsureAsync(updater, repository, Requirement(first.Channel));
        string stateHash = Hashing.Sha256File(StatePath), assetHash = Hashing.Sha256File(Path.Combine(Game, "scene.assets"));
        File.WriteAllText(Path.Combine(Build, "scene.assets"), "synthetic next release");
        var second = await PublishAsync("test-2");
        handler.ChunkRequests = 0;
        var error = await Assert.ThrowsAsync<PatcherException>(() => EnsureAsync(updater, repository, Requirement(second.Channel), running: true));
        Assert.Contains("關閉所有遊戲", error.Message);
        Assert.Equal(0, handler.ChunkRequests);
        Assert.Equal(stateHash, Hashing.Sha256File(StatePath));
        Assert.Equal(assetHash, Hashing.Sha256File(Path.Combine(Game, "scene.assets")));
    }

    [Fact]
    public async Task DamagedRunningInstallIsReadOnlyAndCannotStartAnotherGame()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        await EnsureAsync(updater, repository, required);
        string path = Path.Combine(Game, "scene.assets");
        File.WriteAllBytes(path, new byte[32 * 1024]);
        string damaged = Hashing.Sha256File(path), stateHash = Hashing.Sha256File(StatePath);
        await Assert.ThrowsAsync<PatcherException>(() => EnsureAsync(updater, repository, required, running: true));
        Assert.Equal(damaged, Hashing.Sha256File(path));
        Assert.Equal(stateHash, Hashing.Sha256File(StatePath));
    }

    [Fact]
    public async Task CancelledVerificationNeverCommitsOrBecomesReady()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Stage is UpdateStage.Verifying or UpdateStage.Scanning) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EnsureAsync(updater, repository,
            Requirement(release.Channel), progress: progress, ct: cancel.Token));
        Assert.False(File.Exists(StatePath));
        Assert.False(File.Exists(Path.Combine(Game, "ro3.exe")));
    }

    [Fact]
    public async Task AGameAppearingDuringDownloadPreventsTheCommit()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var sync = new ClientReleaseSynchronizer(updater, repository, _ => Task.FromResult(Requirement(release.Channel)));
        int checks = 0;
        await Assert.ThrowsAsync<PatcherException>(() => sync.EnsureReadyAsync("stable", false,
            () => ++checks <= 2, null, CancellationToken.None));
        Assert.Equal(3, checks);
        Assert.False(File.Exists(StatePath));
        Assert.False(File.Exists(Path.Combine(Game, "ro3.exe")));
    }

    [Fact]
    public async Task ContractIsFetchedAgainAfterVerificationAndCannotSilentlySwitch()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        var required = Requirement(release.Channel);
        await EnsureAsync(updater, repository, required);
        int reads = 0;
        var sync = new ClientReleaseSynchronizer(updater, repository, _ => Task.FromResult(
            ++reads == 1 ? required : required with { Sequence = required.Sequence + 1 }));
        await Assert.ThrowsAsync<PatcherException>(() => sync.EnsureReadyAsync("stable", false, () => true, null, CancellationToken.None));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task UnsignedTrustIsNeverAcceptedByTheStrictSynchronizer()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        await Assert.ThrowsAsync<PatcherException>(() => EnsureAsync(updater, repository, Requirement(release.Channel)));
        Assert.False(File.Exists(StatePath));
    }

    sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    [Fact]
    public async Task SynchronousContractWorkNeverBlocksTheCallingThread()
    {
        var release = await PublishAsync();
        using var repository = new RepositoryClient(new Uri("http://repo.test/"), Trust, new FileRepoHandler(Repo));
        var updater = new Updater(new InstallLayout(Game), repository);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        var sync = new ClientReleaseSynchronizer(updater, repository, _ =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10), CancellationToken.None)) throw new TimeoutException("Test did not release contract work");
            return Task.FromResult(Requirement(release.Channel));
        });
        var operation = Task.Run(() =>
        {
            Task<ClientReleaseReady> pending = sync.EnsureReadyAsync("stable", false, () => true, null, CancellationToken.None);
            returned.Set();
            return pending;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(returned.Wait(TimeSpan.FromSeconds(2)), "The caller must regain control while verification is still running");
        }
        finally { resume.Set(); }
        Assert.True((await operation).Updated);
    }

    [Fact]
    public async Task RequirementReaderUsesOnlyTheConfiguredServerAndNoCache()
    {
        var release = await PublishAsync();
        var required = Requirement(release.Channel);
        using var handler = new ContractHandler(request =>
        {
            Assert.Equal("http://game.test/api/ro3emu/client-release", request.RequestUri?.AbsoluteUri);
            Assert.True(request.Headers.CacheControl?.NoCache);
            Assert.True(request.Headers.CacheControl?.NoStore);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(required)), RequestMessage = request,
            };
        });
        using var client = new ServerClientReleaseClient(new Uri("http://game.test/api"), handler);
        Assert.Equal(required, await client.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequirementReaderRejectsOversizedContentAndForeignRedirect(bool redirect)
    {
        using var handler = new ContractHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[64 * 1024 + 1]),
            RequestMessage = redirect ? new HttpRequestMessage(HttpMethod.Get, "http://other.test/ro3emu/client-release") : request,
        });
        using var client = new ServerClientReleaseClient(new Uri("http://game.test"), handler);
        await Assert.ThrowsAsync<PatcherException>(() => client.GetAsync(CancellationToken.None));
    }

    sealed class ContractHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
}
