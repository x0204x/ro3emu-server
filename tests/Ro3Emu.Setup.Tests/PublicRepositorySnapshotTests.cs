using System.Security.Cryptography;
using Ro3Emu.ClientRelease;
using Ro3Emu.Patcher;
using Ro3Emu.Setup;

namespace Ro3Emu.Setup.Tests;

public sealed class PublicRepositorySnapshotTests : IDisposable
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(3072);
    public PublicRepositorySnapshotTests() => Directory.CreateDirectory(_root);
    private RepositorySigningOptions Signing => new("lab", _key.ExportRSAPrivateKeyPem());
    private RepositoryTrustOptions Trust => new() { RequireSignedChannel = true, TrustedPublicKeys = new Dictionary<string, string> { { "lab", _key.ExportSubjectPublicKeyInfoPem() } } };
    private Task<PublishResult> Publish(string repo, string version, string payload, string channel = "stable")
    {
        string source = Path.Combine(_root, version + ".bin"); File.WriteAllText(source, payload);
        return Publisher.PublishAsync(new PublishOptions
        {
            Files = [new("ro3.exe", source)], RepoDirectory = repo, Version = version, Channel = channel,
            LaunchExe = "ro3.exe", Signing = Signing, BaseClient = new GenesisIdentity(140083, "release", "build", Hash, Hash), ServerVersion = "1.0.24", PatchSha256 = Hash
        });
    }
    [Fact]
    public async Task CandidateKeepsOtherChannelChunksAndPublicFilesAndReusesUnchangedChunks()
    {
        string old = Path.Combine(_root, "old"), candidate = Path.Combine(_root, "candidate"); Directory.CreateDirectory(candidate);
        await Publish(old, "legacy", "older-exclusive", "legacy");
        var prior = await Publish(old, "first", "current-bytes");
        File.WriteAllText(Path.Combine(old, "RO3Launcher.exe"), "launcher");
        Directory.CreateDirectory(Path.Combine(old, "content", "news"));
        File.WriteAllText(Path.Combine(old, "content", "news", "existing.json"), "news");
        PublicRepositorySnapshot.Clone(old, candidate);
        var next = await Publish(candidate, "next", "current-bytes");
        Assert.True(next.ReusedChunks > 0);
        Assert.True(next.Channel.Sequence > prior.Channel.Sequence);
        Assert.Equal("launcher", File.ReadAllText(Path.Combine(candidate, "RO3Launcher.exe")));
        Assert.Equal("news", File.ReadAllText(Path.Combine(candidate, "content", "news", "existing.json")));
        Assert.Equal(next.Channel.Sequence, PublicRepositorySnapshot.Verify(candidate, "stable", Trust).Sequence);
        Assert.True(File.Exists(Path.Combine(candidate, "channels", "legacy.json")));
    }
    [Fact]
    public async Task CorruptRawChunkFailsFullClosureVerification()
    {
        string repo = Path.Combine(_root, "repo"); var publication = await Publish(repo, "version", "contents");
        var chunk = publication.Manifest.Files[0].Chunks[0]; string path = PathRules.ToLocal(repo, PathRules.ChunkRelative(chunk.Hash));
        byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 0xff; File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<Exception>(() => PublicRepositorySnapshot.Verify(repo, "stable", Trust));
    }
    [Fact]
    public async Task MissingOldChannelChunkRefusesCandidateEvenWhenCurrentReleaseIsIntact()
    {
        string repo = Path.Combine(_root, "repo"); var old = await Publish(repo, "old-version", "exclusive", "old");
        await Publish(repo, "new-version", "different");
        var chunk = old.Manifest.Files[0].Chunks[0]; File.Delete(PathRules.ToLocal(repo, PathRules.ChunkRelative(chunk.Hash)));
        Assert.Throws<InvalidDataException>(() => PublicRepositorySnapshot.Verify(repo, "stable", Trust));
    }
    [Fact]
    public async Task SharedSourceLockRejectsConcurrentPublisherBeforeItCreatesTheRepo()
    {
        string repo = Path.Combine(_root, "locked-repo");
        using var held = Publisher.AcquireRepositoryLock(repo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(repo, "blocked", "bytes"));
        Assert.False(Directory.Exists(repo));
    }
    [Fact]
    public void NestedCandidateCannotCopyOrMoveTheSource()
    {
        string repo = Path.Combine(_root, "repo"); Directory.CreateDirectory(repo); File.WriteAllText(Path.Combine(repo, "keep"), "original");
        Assert.Throws<InvalidDataException>(() => PublicRepositorySnapshot.Clone(repo, Path.Combine(repo, "nested")));
        Assert.Throws<InvalidDataException>(() => ClientSync.Activate(repo, Path.Combine(repo, "nested")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(repo, "keep")));
    }
    [Fact]
    public void ActivationRejectsOutsideSiblingAndUnexpectedCandidateName()
    {
        string repo = Path.Combine(_root, "repo"); Directory.CreateDirectory(repo);
        Assert.Throws<InvalidDataException>(() => ClientSync.Activate(repo, Path.Combine(_root, "arbitrary")));
        Assert.Throws<InvalidDataException>(() => ClientSync.Activate(repo, Path.Combine(_root, "other", ".repo.candidate-" + Guid.NewGuid().ToString("N"))));
        Assert.True(Directory.Exists(repo));
    }
    [Fact]
    public void ReparseAncestorRejectsSourceAndDestinationBeforeCopying()
    {
        string real = Path.Combine(_root, "real"); Directory.CreateDirectory(real);
        string link = Path.Combine(_root, "linked");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Windows 未允許建立 symlink 時，使用作業系統既有 junction 作唯讀祖先測試。
            link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Application Data");
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        string source = Path.Combine(_root, "source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "keep"), "original");
        Assert.Throws<InvalidDataException>(() => PublicRepositorySnapshot.Clone(Path.Combine(link, "source"), Path.Combine(_root, "target")));
        Assert.Throws<InvalidDataException>(() => PublicRepositorySnapshot.Clone(source, Path.Combine(link, "candidate")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(source, "keep")));
    }
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, true); }
}
