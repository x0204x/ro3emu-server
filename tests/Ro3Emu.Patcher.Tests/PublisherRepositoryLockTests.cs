using System.Security.Cryptography;

namespace Ro3Emu.Patcher.Tests;

/// <summary>只操作自建小型更新站，驗證長時間持鎖與目錄切換的競爭邊界。</summary>
public sealed class PublisherRepositoryLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-publisher-lock-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");
    private string Source => Path.Combine(_root, "Game.exe");

    public PublisherRepositoryLockTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Source, "合成鎖測試，不可執行");
    }

    private Task<PublishResult> Publish(string repo, string version) => Publisher.PublishAsync(new PublishOptions
    {
        Files = [new("Game.exe", Source)], RepoDirectory = repo, Channel = "stable", Version = version,
        LaunchExe = "Game.exe", Workers = 1, CompressionLevel = 1,
    });

    [Fact]
    public async Task HeldLockBlocksEveryPublishingEntryBeforeRepositoryCreation()
    {
        using var signer = RSA.Create(3072);
        var signing = new RepositorySigningOptions("synthetic-lock-test", signer.ExportRSAPrivateKeyPem());
        string news = Path.Combine(_root, "news.json");
        File.WriteAllText(news, "{\"items\":[]}");
        using (Publisher.AcquireRepositoryLock(Repo))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(Repo, "first"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.SetChannelAsync(Repo, "stable", "first", null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.PublishLauncherAsync(Repo, "stable", Source, "2.0.13"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.PublishNewsAsync(Repo, "stable", news));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher.SignExistingChannelAsync(Repo, "stable", signing));
            Assert.False(Directory.Exists(Repo));
        }
        await Publish(Repo, "first");
        Assert.True(File.Exists(Path.Combine(Repo, "channels", "stable.json")));
    }

    [Fact]
    public async Task LockSurvivesRenameAndBlocksRecreationWhileSeparateCandidateCanPublish()
    {
        await Publish(Repo, "first");
        string rollback = Path.Combine(_root, "rollback");
        string candidate = Path.Combine(_root, "candidate");
        using (Publisher.AcquireRepositoryLock(Repo))
        {
            Directory.Move(Repo, rollback);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(Repo, "lost-update"));
            Assert.False(Directory.Exists(Repo));
            await Publish(candidate, "second");
            Directory.Move(candidate, Repo);
            Assert.Throws<InvalidOperationException>(() => Publisher.AcquireRepositoryLock(Repo + Path.DirectorySeparatorChar));
        }
        using var next = Publisher.AcquireRepositoryLock(Repo);
        Assert.True(File.Exists(Path.Combine(rollback, "channels", "stable.json")));
        Assert.True(File.Exists(Path.Combine(Repo, "manifests", "second.json.zst")));
    }

    [Fact]
    public void LockIsOutsideTheRepositoryAndDisposalReleasesWithoutDeletingIt()
    {
        string expected = Repo + ".publisher.lock";
        using (var held = Publisher.AcquireRepositoryLock(Repo))
        {
            Assert.Equal(expected, held.Name);
            Assert.True(File.Exists(expected));
            Assert.False(Directory.Exists(Repo));
            Assert.Throws<InvalidOperationException>(() => Publisher.AcquireRepositoryLock(Path.Combine(_root, ".", "repo")));
        }
        Assert.True(File.Exists(expected));
        using var reacquired = Publisher.AcquireRepositoryLock(Repo);
    }

    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("ro3-publisher-lock-", StringComparison.Ordinal))
            throw new InvalidOperationException("合成測試清理目錄超出預期範圍。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
