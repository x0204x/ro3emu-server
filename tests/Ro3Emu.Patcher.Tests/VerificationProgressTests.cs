namespace Ro3Emu.Patcher.Tests;

public sealed class VerificationProgressTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "verification-progress-" + Guid.NewGuid().ToString("N"));

    public VerificationProgressTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    ManifestFile WriteFile(string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_root, name), bytes);
        return new() { Path = name, Size = bytes.Length, Sha256 = Hashing.Sha256Hex(bytes) };
    }

    [Fact]
    public async Task LargeFileReportsIntermediateBytesBeforeTheFileCompletes()
    {
        ManifestFile file = WriteFile("large.bin", new byte[5 * 1024 * 1024]);
        using var repository = new RepositoryClient(new Uri("http://unused.invalid/"));
        var updates = new List<UpdateProgress>();
        var result = await new Updater(new InstallLayout(_root), repository).VerifyInstalledAsync(
            new Manifest { Files = [file] }, new InlineProgress(updates.Add), CancellationToken.None);
        Assert.True(result.Matches);
        Assert.Contains(updates, p => p.BytesDone > 0 && p.BytesDone < file.Size && p.FilesDone == 0 && p.FilesTotal == 1);
        Assert.Equal(file.Size, updates[^1].BytesDone);
        Assert.Equal(1, updates[^1].FilesDone);
        AssertMonotonic(updates);
    }

    [Fact]
    public async Task MissingWrongSizeAndEmptyFilesAdvanceTheScanButNeverPassVerification()
    {
        ManifestFile missing = WriteFile("missing.bin", new byte[13]);
        ManifestFile wrong = WriteFile("wrong.bin", new byte[23]);
        ManifestFile empty = WriteFile("empty.bin", []);
        ManifestFile intact = WriteFile("intact.bin", new byte[31]);
        File.Delete(Path.Combine(_root, missing.Path));
        File.WriteAllBytes(Path.Combine(_root, wrong.Path), [1]);
        using var repository = new RepositoryClient(new Uri("http://unused.invalid/"));
        var updates = new List<UpdateProgress>();
        var result = await new Updater(new InstallLayout(_root), repository).VerifyInstalledAsync(
            new Manifest { Files = [missing, wrong, empty, intact] }, new InlineProgress(updates.Add), CancellationToken.None);
        Assert.Equal(new[] { missing.Path, wrong.Path }, result.ChangedFiles);
        Assert.Equal(67, updates[^1].BytesDone);
        Assert.Equal(4, updates[^1].FilesDone);
        Assert.Equal(4, updates[^1].FilesTotal);
        Assert.Contains(updates, p => p.File == empty.Path && p.FilesDone == 3);
        AssertMonotonic(updates);
    }

    [Fact]
    public async Task CancellationInsideALargeFileStopsBeforeCompletion()
    {
        ManifestFile file = WriteFile("large.bin", new byte[5 * 1024 * 1024]);
        using var repository = new RepositoryClient(new Uri("http://unused.invalid/"));
        using var cancel = new CancellationTokenSource();
        var updates = new List<UpdateProgress>();
        var progress = new InlineProgress(p => { updates.Add(p); if (p.BytesDone > 0) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Updater(new InstallLayout(_root), repository)
            .VerifyInstalledAsync(new Manifest { Files = [file] }, progress, cancel.Token));
        Assert.DoesNotContain(updates, p => p.FilesDone == 1 || p.BytesDone == file.Size);
        Assert.Equal(file.Sha256, Hashing.Sha256File(Path.Combine(_root, file.Path)));
    }

    [Fact]
    public void ManySmallFilesReuseTheHashBuffer()
    {
        ManifestFile file = WriteFile("small.bin", [1, 2, 3]);
        string path = Path.Combine(_root, file.Path);
        Hashing.Sha256File(path);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Assert.Equal(file.Sha256, Hashing.Sha256File(path));
        // 只限純雜湊路徑配置，避免每個小檔案產生 1 MiB 以上的大型物件。
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 * 1024 * 1024);
    }

    static void AssertMonotonic(List<UpdateProgress> updates)
    {
        for (int i = 1; i < updates.Count; i++)
        {
            Assert.True(updates[i].BytesDone >= updates[i - 1].BytesDone);
            Assert.True(updates[i].FilesDone >= updates[i - 1].FilesDone);
            Assert.InRange(updates[i].BytesDone, 0, updates[i].BytesTotal);
        }
    }

    sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }
}
