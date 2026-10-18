using System.Net;
using Ro3Emu.Patcher;

namespace Ro3Emu.Patcher.Tests;

/// <summary>Serves a repository directory to RepositoryClient without a socket; counts what was fetched.</summary>
sealed class FileRepoHandler(string repo) : HttpMessageHandler
{
    public long ChunkBytesServed;
    public int ChunkRequests;
    public Func<string, bool>? FailWhen;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string relative = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
        if (FailWhen?.Invoke(relative) == true)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
        string path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        byte[] data = File.ReadAllBytes(path);
        if (relative.StartsWith("chunks/"))
        {
            Interlocked.Add(ref ChunkBytesServed, data.Length);
            Interlocked.Increment(ref ChunkRequests);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }
}

public sealed class PatcherTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "fspatch-" + Guid.NewGuid().ToString("N"));
    string Build => Path.Combine(_root, "build");
    string Repo => Path.Combine(_root, "repo");
    string Game => Path.Combine(_root, "install", "game");
    string Meta => Path.Combine(Game, ".ro3launcher");
    string? _dataDir;
    InstallLayout Layout => new(Game, _dataDir);

    // Small chunks keep the test data small while exercising multi-chunk files.
    static readonly ChunkingParameters Small = new(1024, 4096, 16384);

    public PatcherTests() => Directory.CreateDirectory(Build);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    void WriteBuild(string relative, byte[] data)
    {
        string path = Path.Combine(Build, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    Task<PublishResult> Publish(string version) => Publisher.PublishAsync(new PublishOptions
    {
        Files = Publisher.CollectDirectory(Build, "", _ => false),
        RepoDirectory = Repo,
        Version = version,
        Channel = "stable",
        LaunchExe = "Game.exe",
        Chunking = Small,
        CompressionLevel = 3,
        Workers = 4,
    });

    async Task<UpdateSummary> Update(FileRepoHandler handler, bool verifyAll = false, CancellationToken ct = default)
    {
        using var client = new RepositoryClient(new Uri("http://repo.test/"), handler);
        var updater = new Updater(Layout, client);
        var check = await updater.CheckAsync("stable", ct);
        return await updater.UpdateAsync(check.Channel, verifyAll, null, ct);
    }

    void AssertGameMatchesBuild()
    {
        foreach (string file in Directory.EnumerateFiles(Build, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Build, file).Replace(Path.DirectorySeparatorChar, '/');
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Layout.Resolve(relative)));
        }
    }

    [Fact]
    public async Task FreshInstall_LeavesNoChunksInTheCache()
    {
        // Chunk shared by two files and within one file: deleted only after its last use.
        byte[] shared = Random(50_000, 11);
        WriteBuild("Game.exe", Random(30_000, 10));
        WriteBuild("data/a.bin", shared.Concat(shared).ToArray());
        WriteBuild("data/b.bin", shared.Concat(Random(20_000, 12)).ToArray());
        await Publish("1.0.0");

        await Update(new FileRepoHandler(Repo));

        AssertGameMatchesBuild();
        string cache = Path.Combine(Meta, "cache");
        Assert.True(!Directory.Exists(cache) || !Directory.EnumerateFiles(cache).Any(), "chunks left in the cache after the install");
    }

    [Fact]
    public void Chunking_IsDeterministic_AndRespectsBounds()
    {
        byte[] data = Random(400_000, 1);
        var a = Cut(data, Small);
        var b = Cut(data, Small);
        Assert.Equal(a, b);
        Assert.Equal(data.Length, a.Sum());
        Assert.All(a.Take(a.Count - 1), n => Assert.InRange(n, Small.Min, Small.Max));
    }

    [Fact]
    public void Chunking_InsertionOnlyChangesNearbyChunks()
    {
        byte[] data = Random(2_000_000, 2);
        byte[] edited = data[..1_000_000].Concat(Random(777, 3)).Concat(data[1_000_000..]).ToArray();
        var before = Hashes(data, Small);
        var after = Hashes(edited, Small);
        int shared = after.Count(before.Contains);
        Assert.True(shared >= after.Count - 3, $"only {shared} of {after.Count} chunks survived an insertion");
    }

    static List<int> Cut(byte[] data, ChunkingParameters p)
    {
        var sizes = new List<int>();
        var reader = new ChunkReader(new MemoryStream(data), p);
        while (reader.Next(out _, out var chunk)) sizes.Add(chunk.Length);
        return sizes;
    }

    static HashSet<string> Hashes(byte[] data, ChunkingParameters p)
    {
        var set = new HashSet<string>();
        var reader = new ChunkReader(new MemoryStream(data), p);
        while (reader.Next(out _, out var chunk)) set.Add(Hashing.Sha256Hex(chunk.Span));
        return set;
    }

    [Fact]
    public void Glob_ExcludesUnityBuildJunk()
    {
        var globs = new GlobSet(new[] { "*_BackUpThisFolder_ButDontShipItWithYourGame/", "*.log", "Temp/", "Data/**/*.pdb" });
        Assert.True(globs.IsMatch("RagnarokUnity_BackUpThisFolder_ButDontShipItWithYourGame/x.cpp"));
        Assert.True(globs.IsMatch("build.log"));
        Assert.True(globs.IsMatch("sub/build.log"));
        Assert.True(globs.IsMatch("Temp/ro-editor-command.txt"));
        Assert.True(globs.IsMatch("Data/a/b/c.pdb"));
        Assert.False(globs.IsMatch("RagnarokUnity.exe"));
        Assert.False(globs.IsMatch("NotTemp/x"));
    }

    [Fact]
    public void Paths_OutsideTheInstallAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => PathRules.ValidateRelative("../evil.exe"));
        Assert.Throws<InvalidDataException>(() => PathRules.ValidateRelative("C:/Windows/x"));
        Assert.Throws<InvalidDataException>(() => PathRules.ValidateRelative("/abs"));
        Assert.Throws<InvalidDataException>(() => PathRules.ValidateRelative("a\\b"));
        PathRules.ValidateRelative("Game_Data/level0");
    }

    [Theory]
    [InlineData("../../sentinel.txt")]
    [InlineData("..\\..\\sentinel.txt")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("abcdef")]
    [InlineData("")]
    public void Manifest_ChunkHashesThatAreNotSha256AreRejected(string hash)
    {
        // A chunk hash names a cache file; anything but 64 lower-case hex digits could point outside the cache.
        var manifest = new Manifest { Files = [new ManifestFile { Path = "Game.exe", Chunks = [new ChunkRef { Hash = hash, Size = 1, StoredSize = 1 }] }] };
        Assert.Throws<InvalidDataException>(() => Manifest.FromCompressedBytes(manifest.ToCompressedBytes()));
        manifest.Files[0].Chunks[0].Hash = new string('a', 64);
        Assert.Single(Manifest.FromCompressedBytes(manifest.ToCompressedBytes()).Files);
    }

    [Fact]
    public async Task FreshInstall_ThenPatch_DownloadsOnlyChanges_AndKeepsUserFiles()
    {
        byte[] grf = Random(600_000, 10);
        WriteBuild("Game.exe", Random(50_000, 11));
        WriteBuild("ROData/data.grf", grf);
        WriteBuild("Game_Data/old.assets", Random(20_000, 12));
        await Publish("1.0.0");

        var handler = new FileRepoHandler(Repo);
        var first = await Update(handler);
        Assert.Equal("1.0.0", first.Version);
        AssertGameMatchesBuild();
        long fullDownload = handler.ChunkBytesServed;

        // Things the player and the game create must survive updates.
        File.WriteAllText(Path.Combine(Game, "settings.json"), "{}");

        // 1.0.1: an insertion in the big file, one file removed, one added.
        byte[] patchedGrf = grf[..300_000].Concat(Random(5000, 13)).Concat(grf[300_000..]).ToArray();
        WriteBuild("ROData/data.grf", patchedGrf);
        File.Delete(Path.Combine(Build, "Game_Data", "old.assets"));
        WriteBuild("Game_Data/new.assets", Random(3000, 14));
        await Publish("1.0.1");

        handler.ChunkBytesServed = 0;
        var second = await Update(handler);
        Assert.Equal("1.0.1", second.Version);
        AssertGameMatchesBuild();
        Assert.False(File.Exists(Path.Combine(Game, "Game_Data", "old.assets")));
        Assert.True(File.Exists(Path.Combine(Game, "settings.json")));
        Assert.Equal(1, second.DeletedFiles);
        Assert.True(handler.ChunkBytesServed < fullDownload / 5, $"patch downloaded {handler.ChunkBytesServed} of {fullDownload}");
        Assert.True(second.ReusedBytes > 500_000);
        Assert.False(Directory.Exists(Path.Combine(Meta, "staging")));
        Assert.False(Directory.Exists(Path.Combine(Meta, "cache")));

        // Already up to date: nothing downloaded, nothing moved.
        handler.ChunkBytesServed = 0;
        var third = await Update(handler);
        Assert.Equal(0, third.ChangedFiles);
        Assert.Equal(0, handler.ChunkBytesServed);
    }

    [Fact]
    public async Task InterruptedDownload_LeavesOldVersion_AndResumesFromCache()
    {
        WriteBuild("Game.exe", Random(10_000, 20));
        WriteBuild("ROData/data.grf", Random(300_000, 21));
        await Publish("1.0.0");
        var handler = new FileRepoHandler(Repo);
        await Update(handler);

        WriteBuild("ROData/data.grf", Random(300_000, 22));
        await Publish("1.0.1");

        // The server dies after a few chunks: the update fails, the installed 1.0.0 stays intact.
        int served = 0;
        handler.FailWhen = relative => relative.StartsWith("chunks/") && Interlocked.Increment(ref served) > 10;
        await Assert.ThrowsAsync<PatcherException>(() => Update(handler));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Repo, "..", "build", "Game.exe")), File.ReadAllBytes(Path.Combine(Game, "Game.exe")));
        using (var client = new RepositoryClient(new Uri("http://repo.test/"), handler))
        {
            var updater = new Updater(Layout, client);
            Assert.Equal("1.0.0", updater.LoadState()!.Version);
            Assert.Empty(updater.QuickVerify(updater.LoadState()!));
        }

        handler.FailWhen = null;
        handler.ChunkRequests = 0;
        var resumed = await Update(handler);
        Assert.Equal("1.0.1", resumed.Version);
        AssertGameMatchesBuild();
        Assert.True(handler.ChunkRequests < 70, $"resume refetched {handler.ChunkRequests} chunks");
    }

    [Fact]
    public async Task Repair_RestoresDamagedFiles()
    {
        WriteBuild("Game.exe", Random(10_000, 30));
        WriteBuild("ROData/data.grf", Random(200_000, 31));
        await Publish("1.0.0");
        var handler = new FileRepoHandler(Repo);
        await Update(handler);

        // Same size, different bytes: only a hash notices.
        string grf = Path.Combine(Game, "ROData", "data.grf");
        byte[] damaged = File.ReadAllBytes(grf);
        damaged[123_456] ^= 0xFF;
        File.WriteAllBytes(grf, damaged);
        // Damage comes later than the install; a coarse file clock (Linux containers) could otherwise keep the old mtime.
        File.SetLastWriteTimeUtc(grf, File.GetLastWriteTimeUtc(grf).AddSeconds(1));
        File.Delete(Path.Combine(Game, "Game.exe"));

        using (var client = new RepositoryClient(new Uri("http://repo.test/"), handler))
        {
            var updater = new Updater(Layout, client);
            Assert.Equal(2, updater.QuickVerify(updater.LoadState()!).Count);
        }
        handler.ChunkBytesServed = 0;
        var repaired = await Update(handler, verifyAll: true);
        Assert.Equal(2, repaired.ChangedFiles);
        AssertGameMatchesBuild();
        Assert.True(handler.ChunkBytesServed < 60_000, "repair should fetch only the damaged chunk and the missing exe");
    }

    [WindowsFact]
    public async Task LockedFile_FailsCommit_AndJournalFinishesItLater()
    {
        WriteBuild("Game.exe", Random(10_000, 40));
        WriteBuild("Game_Data/a.assets", Random(10_000, 41));
        await Publish("1.0.0");
        var handler = new FileRepoHandler(Repo);
        await Update(handler);

        WriteBuild("Game.exe", Random(10_000, 42));
        WriteBuild("Game_Data/a.assets", Random(10_000, 43));
        await Publish("1.0.1");

        // The game is running: its exe cannot be replaced.
        using (new FileStream(Path.Combine(Game, "Game.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<PatcherException>(() => Update(handler));
        }
        Assert.True(File.Exists(Path.Combine(Meta, "journal.json")));

        // Next start: the journal is replayed before anything else.
        using var client = new RepositoryClient(new Uri("http://repo.test/"), handler);
        var updater = new Updater(Layout, client);
        var state = updater.LoadState()!;
        Assert.Equal("1.0.1", state.Version);
        Assert.False(File.Exists(Path.Combine(Meta, "journal.json")));
        AssertGameMatchesBuild();
        Assert.Empty(updater.QuickVerify(state));
    }

    [Fact]
    public async Task Publish_RefusesToOverwriteARelease_AndChannelCanRollBack()
    {
        WriteBuild("Game.exe", Random(10_000, 50));
        await Publish("1.0.0");
        WriteBuild("Game.exe", Random(10_000, 51));
        await Publish("1.0.1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish("1.0.1"));

        await Publisher.SetChannelAsync(Repo, "stable", "1.0.0", null);
        var handler = new FileRepoHandler(Repo);
        var result = await Update(handler);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public async Task GameRelease_KeepsTheChannelsLauncherRelease()
    {
        WriteBuild("Game.exe", Random(10_000, 60));
        await Publish("1.0.0");
        string exe = Path.Combine(_root, "launcher.exe");
        File.WriteAllBytes(exe, Random(5000, 61));
        await Publisher.PublishLauncherAsync(Repo, "stable", exe, "1.0.1");

        WriteBuild("Game.exe", Random(10_000, 62));
        await Publish("1.0.1");
        using var client = new RepositoryClient(new Uri("http://repo.test/"), new FileRepoHandler(Repo));
        var channel = await client.GetChannelAsync("stable", CancellationToken.None);
        Assert.Equal("1.0.1", channel.Version);
        Assert.Equal("1.0.1", channel.Launcher?.Version);
        Assert.Equal(File.ReadAllBytes(exe), await client.TryGetBytesAsync(channel.Launcher!.Url, CancellationToken.None));
    }

    [Fact]
    public async Task DataFolderElsewhere_InstallsAndPatchesThere()
    {
        _dataDir = Path.Combine(_root, "otherdrive", "GameData");
        WriteBuild("Game.exe", Random(10_000, 70));
        WriteBuild("ROData/data.grf", Random(100_000, 71));
        WriteBuild("ROData/BGM/01.mp3", Random(5000, 72));
        await Publish("1.0.0");
        var handler = new FileRepoHandler(Repo);
        await Update(handler);
        Assert.True(File.Exists(Path.Combine(_dataDir, "data.grf")));
        Assert.True(File.Exists(Path.Combine(_dataDir, "BGM", "01.mp3")));
        Assert.False(Directory.Exists(Path.Combine(Game, "ROData")));
        AssertGameMatchesBuild();

        WriteBuild("ROData/data.grf", Random(100_000, 73));
        await Publish("1.0.1");
        await Update(handler);
        AssertGameMatchesBuild();
        Assert.False(Directory.Exists(Path.Combine(_dataDir, ".ro3launcher-staging")));
    }

    [Fact]
    public async Task Move_DataOutAndBack_AndGameFolder_KeepsTheInstallValid()
    {
        WriteBuild("Game.exe", Random(10_000, 80));
        WriteBuild("ROData/data.grf", Random(100_000, 81));
        await Publish("1.0.0");
        var handler = new FileRepoHandler(Repo);
        await Update(handler);
        File.WriteAllText(Path.Combine(Game, "player.log"), "game-written");

        using var client = new RepositoryClient(new Uri("http://repo.test/"), handler);
        var state = new Updater(Layout, client).LoadState()!;

        // Data to another folder.
        var before = Layout;
        _dataDir = Path.Combine(_root, "data2");
        InstallMover.Move(before, Layout, state, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_dataDir, "data.grf")));
        Assert.False(Directory.Exists(Path.Combine(Game, "ROData")));
        Assert.Empty(new Updater(Layout, client).QuickVerify(state));

        // Back to the default place.
        before = Layout;
        _dataDir = null;
        InstallMover.Move(before, Layout, state, null, CancellationToken.None);
        Assert.Empty(new Updater(Layout, client).QuickVerify(state));
        AssertGameMatchesBuild();

        // The whole game folder, with what the game wrote and the launcher state.
        string oldGame = Game;
        var from = Layout;
        var to = new InstallLayout(Path.Combine(_root, "moved", "game"));
        InstallMover.Move(from, to, state, null, CancellationToken.None);
        Assert.False(Directory.Exists(oldGame));
        Assert.Equal("game-written", File.ReadAllText(Path.Combine(to.GameDirectory, "player.log")));
        var moved = new Updater(to, client);
        Assert.Equal("1.0.0", moved.LoadState()!.Version);
        Assert.Empty(moved.QuickVerify(moved.LoadState()!));
    }

    [Fact]
    public void Layout_RejectsDataInsideTheGameFolder()
    {
        Assert.Throws<ArgumentException>(() => new InstallLayout(Game, Path.Combine(Game, "sub")));
        Assert.Null(new InstallLayout(Game, Path.Combine(Game, "ROData")).DataDirectory);
    }
}

/// <summary>Needs Windows file locking (an open FileShare.None handle blocks replacing the file).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only: relies on Windows file locking.";
    }
}
