using System.Security.Cryptography;
using System.Text.Json;

namespace Ro3Emu.Patcher;

public enum UpdateStage { Scanning, Downloading, Assembling, Verifying, Committing }

public readonly record struct UpdateProgress(UpdateStage Stage, long BytesDone, long BytesTotal, string? File = null);

public sealed record UpdateCheck(ChannelInfo Channel, LocalState? Installed, bool GamePresent)
{
    public bool UpdateRequired => Installed is null
        || !string.Equals(Installed.ManifestSha256, Channel.ManifestSha256, StringComparison.OrdinalIgnoreCase);
}

public sealed record UpdateSummary(string Version, int ChangedFiles, int DeletedFiles, long DownloadedBytes, long ReusedBytes);

/// <summary>
/// Brings a game directory to a release: scan → plan → download missing chunks → assemble changed files into
/// staging → verify → commit through a journal. Originals are untouched until every new file is complete and
/// verified, so cancelling or crashing before the commit leaves the old version playable.
/// </summary>
public sealed class Updater(InstallLayout layout, RepositoryClient repository)
{
    readonly InstallLayout _layout = layout;
    readonly string _metaDir = layout.MetaDirectory;
    readonly RepositoryClient _repo = repository;

    public InstallLayout Layout => _layout;

    public int DownloadConcurrency { get; set; } = 8;

    string StatePath => Path.Combine(_metaDir, "state.json");
    string NextStatePath => Path.Combine(_metaDir, "state.next.json");
    string ManifestPath => Path.Combine(_metaDir, "installed.json.zst");
    string NextManifestPath => Path.Combine(_metaDir, "installed.next.json.zst");
    string JournalPath => Path.Combine(_metaDir, "journal.json");
    string CacheDir => Path.Combine(_metaDir, "cache");

    public LocalState? LoadState()
    {
        RecoverInterruptedCommit();
        return LocalState.Load(StatePath);
    }

    public async Task<UpdateCheck> CheckAsync(string channel, CancellationToken ct)
    {
        var state = LoadState();
        var info = await _repo.GetChannelAsync(channel, ct);
        bool present = state is not null && File.Exists(_layout.Resolve(state.Launch.Exe));
        return new UpdateCheck(info, present ? state : null, present);
    }

    /// <summary>Paths whose size or mtime differ from what was installed (cheap: no reads). Empty = looks intact.</summary>
    public List<string> QuickVerify(LocalState state)
    {
        var changed = new List<string>();
        foreach (var (path, entry) in state.Files)
        {
            var info = new FileInfo(_layout.Resolve(path));
            if (!info.Exists || info.Length != entry.Size || info.LastWriteTimeUtc.Ticks != entry.MtimeTicks)
            {
                changed.Add(path);
            }
        }
        return changed;
    }

    sealed record ChunkSource(string Path, long Offset, int Size);

    sealed class FilePlan(ManifestFile file, string localPath)
    {
        public ManifestFile File { get; } = file;
        public string LocalPath { get; } = localPath;
        public bool Changed;
        /// <summary>Chunk layout of the bytes on disk now, when known without reading (previous manifest).</summary>
        public List<ChunkRef>? OldLayout;
        public bool OldLayoutUnknown;
    }

    public async Task<UpdateSummary> UpdateAsync(ChannelInfo channel, bool verifyAll, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        RecoverInterruptedCommit();
        Directory.CreateDirectory(_metaDir);
        var (manifest, manifestRaw) = await _repo.GetManifestAsync(channel, ct);
        var parameters = manifest.Chunking.ToParameters();
        parameters.Validate();

        var state = LocalState.Load(StatePath);
        Manifest? previous = null;
        if (state is not null && File.Exists(ManifestPath))
        {
            try { previous = Manifest.FromCompressedBytes(await File.ReadAllBytesAsync(ManifestPath, ct)); }
            catch (Exception e) when (e is InvalidDataException or IOException or JsonException) { previous = null; }
        }
        var previousFiles = previous?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        // 1. Scan: which files differ from the release.
        var plans = manifest.Files.Select(f => new FilePlan(f, _layout.Resolve(f.Path))).ToList();
        var verified = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        long scanTotal = 0;
        var toHash = new List<FilePlan>();
        foreach (var plan in plans)
        {
            var info = new FileInfo(plan.LocalPath);
            if (!info.Exists)
            {
                plan.Changed = true;
                continue;
            }
            InstalledFile? entry = null;
            state?.Files.TryGetValue(plan.File.Path, out entry);
            bool statMatches = entry is not null && info.Length == entry.Size && info.LastWriteTimeUtc.Ticks == entry.MtimeTicks;
            if (statMatches && !verifyAll)
            {
                if (string.Equals(entry!.Sha256, plan.File.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    verified[plan.File.Path] = entry;
                    continue;
                }
                plan.Changed = true;
                if (previousFiles is not null && previousFiles.TryGetValue(plan.File.Path, out var old)
                    && string.Equals(old.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    plan.OldLayout = old.Chunks;
                }
                else
                {
                    plan.OldLayoutUnknown = true;
                }
                continue;
            }
            if (info.Length == plan.File.Size)
            {
                toHash.Add(plan);
                scanTotal += info.Length;
            }
            else
            {
                plan.Changed = true;
                plan.OldLayoutUnknown = true;
            }
        }

        long scanned = 0;
        progress?.Report(new UpdateProgress(UpdateStage.Scanning, 0, scanTotal));
        foreach (var plan in toHash)
        {
            var info = new FileInfo(plan.LocalPath);
            string hash = Hashing.Sha256File(plan.LocalPath, n =>
            {
                scanned += n;
                progress?.Report(new UpdateProgress(UpdateStage.Scanning, scanned, scanTotal, plan.File.Path));
            }, ct);
            if (string.Equals(hash, plan.File.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                verified[plan.File.Path] = new InstalledFile { Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks, Sha256 = hash };
            }
            else
            {
                plan.Changed = true;
                plan.OldLayoutUnknown = true;
            }
        }

        var changed = plans.Where(p => p.Changed).ToList();

        // 2. Index the bytes already on disk that a changed file can reuse.
        var local = new Dictionary<string, ChunkSource>(StringComparer.OrdinalIgnoreCase);
        void AddLayout(string path, IEnumerable<ChunkRef> chunks)
        {
            long offset = 0;
            foreach (var chunk in chunks)
            {
                local.TryAdd(chunk.Hash, new ChunkSource(path, offset, chunk.Size));
                offset += chunk.Size;
            }
        }
        var neededHashes = new HashSet<string>(changed.SelectMany(p => p.File.Chunks).Select(c => c.Hash), StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans.Where(p => !p.Changed))
        {
            AddLayout(plan.LocalPath, plan.File.Chunks);
        }
        foreach (var plan in changed.Where(p => p.OldLayout is not null))
        {
            AddLayout(plan.LocalPath, plan.OldLayout!);
        }
        var unknown = changed.Where(p => p.OldLayoutUnknown && File.Exists(p.LocalPath)).ToList();
        long chunkTotal = unknown.Sum(p => new FileInfo(p.LocalPath).Length), chunked = 0;
        foreach (var plan in unknown)
        {
            using var stream = new FileStream(plan.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var reader = new ChunkReader(stream, parameters);
            while (reader.Next(out long offset, out var chunk))
            {
                ct.ThrowIfCancellationRequested();
                string hash = Hashing.Sha256Hex(chunk.Span);
                if (neededHashes.Contains(hash))
                {
                    local.TryAdd(hash, new ChunkSource(plan.LocalPath, offset, chunk.Length));
                }
                chunked += chunk.Length;
                progress?.Report(new UpdateProgress(UpdateStage.Scanning, chunked, chunkTotal, plan.File.Path));
            }
        }

        // 3. Download what is neither on disk nor in the cache of an earlier, interrupted attempt.
        Directory.CreateDirectory(CacheDir);
        var missing = new Dictionary<string, ChunkRef>(StringComparer.OrdinalIgnoreCase);
        long reusedBytes = 0;
        foreach (var chunk in changed.SelectMany(p => p.File.Chunks))
        {
            if (local.ContainsKey(chunk.Hash))
            {
                reusedBytes += chunk.Size;
                continue;
            }
            var cached = new FileInfo(CachePath(chunk.Hash));
            if (cached.Exists && cached.Length == chunk.StoredSize)
            {
                continue;
            }
            missing.TryAdd(chunk.Hash, chunk);
        }

        long stagingBytes = changed.Sum(p => p.File.Size);
        // The cache holds stored (compressed) chunks and each one is deleted after its last use while files are
        // assembled, so on one drive the peak is about the larger of the two plus one file, not their sum.
        long cacheBytes = missing.Values.Sum(c => (long)c.StoredSize);
        var cacheNeeds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var stagingNeeds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Need(Dictionary<string, long> needs, string path, long bytes) { string root = Path.GetPathRoot(path) ?? ""; needs[root] = needs.GetValueOrDefault(root) + bytes; }
        Need(cacheNeeds, CacheDir, cacheBytes);
        foreach (var plan in changed) Need(stagingNeeds, _layout.RootOf(plan.File.Path), plan.File.Size);
        long largestFile = changed.Count == 0 ? 0 : changed.Max(p => p.File.Size);
        foreach (string root in cacheNeeds.Keys.Union(stagingNeeds.Keys, StringComparer.OrdinalIgnoreCase))
        {
            long cache = cacheNeeds.GetValueOrDefault(root), staging = stagingNeeds.GetValueOrDefault(root);
            EnsureFreeSpace(root, cache > 0 && staging > 0 ? Math.Max(cache, staging) + largestFile : cache + staging);
        }

        long downloadTotal = missing.Values.Sum(c => (long)c.StoredSize), downloaded = 0;
        progress?.Report(new UpdateProgress(UpdateStage.Downloading, 0, downloadTotal));
        await Parallel.ForEachAsync(missing.Values, new ParallelOptions { MaxDegreeOfParallelism = DownloadConcurrency, CancellationToken = ct }, async (chunk, token) =>
        {
            await DownloadToCacheAsync(chunk, n =>
            {
                long done = Interlocked.Add(ref downloaded, n);
                progress?.Report(new UpdateProgress(UpdateStage.Downloading, done, downloadTotal));
            }, token);
        });

        // 4. Assemble each changed file into staging; every chunk is checked, the whole file too.
        var cacheUses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in changed.SelectMany(p => p.File.Chunks).Where(c => !local.ContainsKey(c.Hash)))
        {
            cacheUses[chunk.Hash] = cacheUses.GetValueOrDefault(chunk.Hash) + 1;
        }
        long assembleTotal = stagingBytes, assembled = 0;
        progress?.Report(new UpdateProgress(UpdateStage.Assembling, 0, assembleTotal));
        var sources = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var plan in changed)
            {
                string stagePath = _layout.StagingPath(plan.File.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
                using (var output = new FileStream(stagePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    output.SetLength(plan.File.Size);
                    foreach (var chunk in plan.File.Chunks)
                    {
                        ct.ThrowIfCancellationRequested();
                        byte[] data = await ReadChunkAsync(chunk, local, sources, ct);
                        whole.AppendData(data);
                        await output.WriteAsync(data, ct);
                        if (cacheUses.TryGetValue(chunk.Hash, out int uses))
                        {
                            if (uses > 1) cacheUses[chunk.Hash] = uses - 1;
                            else
                            {
                                cacheUses.Remove(chunk.Hash);
                                File.Delete(CachePath(chunk.Hash)); // an interrupted run downloads it again
                            }
                        }
                        assembled += data.Length;
                        progress?.Report(new UpdateProgress(UpdateStage.Assembling, assembled, assembleTotal, plan.File.Path));
                    }
                    string hash = Convert.ToHexStringLower(whole.GetHashAndReset());
                    if (!string.Equals(hash, plan.File.Sha256, StringComparison.OrdinalIgnoreCase) || output.Length != plan.File.Size)
                    {
                        throw new PatcherException($"{plan.File.Path} did not verify after the update. Use Repair to try again.");
                    }
                }
                var info = new FileInfo(stagePath);
                staged[plan.File.Path] = new InstalledFile { Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks, Sha256 = plan.File.Sha256 };
            }
        }
        finally
        {
            foreach (var stream in sources.Values)
            {
                stream.Dispose();
            }
        }

        // 5. Commit.
        progress?.Report(new UpdateProgress(UpdateStage.Committing, 0, 1));
        var newState = new LocalState
        {
            Version = manifest.Version,
            Channel = channel.Channel,
            ManifestSha256 = channel.ManifestSha256,
            Launch = manifest.Launch,
        };
        foreach (var plan in plans)
        {
            newState.Files[plan.File.Path] = staged.TryGetValue(plan.File.Path, out var s) ? s : verified[plan.File.Path];
        }
        var releasePaths = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state is not null) owned.UnionWith(state.Files.Keys);
        if (previous is not null) owned.UnionWith(previous.Files.Select(f => f.Path));
        var journal = new CommitJournal
        {
            Moves = changed.Select(p => p.File.Path).ToList(),
            Deletes = owned.Where(p => !releasePaths.Contains(p)).ToList(),
        };

        newState.Save(NextStatePath);
        await File.WriteAllBytesAsync(NextManifestPath, manifestRaw, ct);
        string journalTemp = JournalPath + ".tmp";
        await File.WriteAllBytesAsync(journalTemp, JsonSerializer.SerializeToUtf8Bytes(journal, Json.Compact), CancellationToken.None);
        File.Move(journalTemp, JournalPath, overwrite: true);
        ApplyJournal(journal);

        progress?.Report(new UpdateProgress(UpdateStage.Committing, 1, 1));
        return new UpdateSummary(manifest.Version, journal.Moves.Count, journal.Deletes.Count, downloaded, reusedBytes);
    }

    string CachePath(string hash) => Path.Combine(CacheDir, hash);

    async Task DownloadToCacheAsync(ChunkRef chunk, Action<int> onBytes, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            long reported = 0;
            try
            {
                byte[] stored = await _repo.GetChunkAsync(chunk.Hash, chunk.StoredSize, n => { reported += n; onBytes(n); }, ct);
                byte[] raw = Decompress(stored, chunk.Size);
                if (!string.Equals(Hashing.Sha256Hex(raw), chunk.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"Chunk {chunk.Hash[..12]} is corrupt.");
                }
                string path = CachePath(chunk.Hash);
                string temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
                await File.WriteAllBytesAsync(temp, stored, ct);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                onBytes(checked((int)-reported));
                if (attempt >= 5)
                {
                    throw new PatcherException("Downloading the update failed. Check your connection and try again.", e);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    static byte[] Decompress(byte[] stored, int expectedSize)
    {
        try
        {
            using var decompressor = new ZstdSharp.Decompressor();
            byte[] raw = decompressor.Unwrap(stored).ToArray();
            if (raw.Length != expectedSize)
            {
                throw new InvalidDataException("Chunk size mismatch after decompression.");
            }
            return raw;
        }
        catch (ZstdSharp.ZstdException e)
        {
            throw new InvalidDataException("Chunk does not decompress.", e);
        }
    }

    async Task<byte[]> ReadChunkAsync(ChunkRef chunk, Dictionary<string, ChunkSource> local, Dictionary<string, FileStream> sources, CancellationToken ct)
    {
        if (local.TryGetValue(chunk.Hash, out var source))
        {
            if (!sources.TryGetValue(source.Path, out var stream))
            {
                stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
                sources[source.Path] = stream;
            }
            byte[] data = new byte[chunk.Size];
            stream.Position = source.Offset;
            int total = 0;
            while (total < data.Length)
            {
                int read = await stream.ReadAsync(data.AsMemory(total), ct);
                if (read == 0) break;
                total += read;
            }
            if (total == data.Length && string.Equals(Hashing.Sha256Hex(data), chunk.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return data;
            }
            // The local copy changed under us (edited file, disk error): fetch this chunk instead.
            local.Remove(chunk.Hash);
        }

        string cachePath = CachePath(chunk.Hash);
        if (File.Exists(cachePath))
        {
            if (TryReadCached(await File.ReadAllBytesAsync(cachePath, ct), chunk) is { } cached)
            {
                return cached;
            }
            File.Delete(cachePath);
        }
        await DownloadToCacheAsync(chunk, _ => { }, ct);
        return TryReadCached(await File.ReadAllBytesAsync(cachePath, ct), chunk)
            ?? throw new PatcherException($"Chunk {chunk.Hash[..12]} is corrupt after download.");
    }

    /// <summary>A cache file holds the chunk's stored (zstd) bytes; null when it does not decompress to the chunk.</summary>
    static byte[]? TryReadCached(byte[] stored, ChunkRef chunk)
    {
        try
        {
            byte[] raw = Decompress(stored, chunk.Size);
            return string.Equals(Hashing.Sha256Hex(raw), chunk.Hash, StringComparison.OrdinalIgnoreCase) ? raw : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    static void EnsureFreeSpace(string root, long bytes)
    {
        if (string.IsNullOrEmpty(root))
        {
            return;
        }
        long margin = 200L * 1024 * 1024;
        try
        {
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < bytes + margin)
            {
                throw new PatcherException($"Not enough disk space: the update needs {Format(bytes + margin)} free on {root}, there is {Format(free)}.");
            }
        }
        catch (ArgumentException)
        {
            // Network path or unknown drive: let the write fail if it must.
        }
    }

    static string Format(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>Finishes a commit that a crash or power loss interrupted.</summary>
    public void RecoverInterruptedCommit()
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }
        var journal = JsonSerializer.Deserialize<CommitJournal>(File.ReadAllBytes(JournalPath), Json.Compact) ?? new CommitJournal();
        ApplyJournal(journal);
    }

    void ApplyJournal(CommitJournal journal)
    {
        foreach (string path in journal.Moves)
        {
            string from = _layout.StagingPath(path);
            string to = _layout.Resolve(path);
            if (!File.Exists(from))
            {
                continue; // moved before the interruption
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try
            {
                File.Move(from, to, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new PatcherException($"Could not replace {path}. Close the game and try again.", e);
            }
        }
        foreach (string path in journal.Deletes)
        {
            string target = _layout.Resolve(path);
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                DeleteEmptyParents(Path.GetDirectoryName(target), _layout.RootOf(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover file of an old release is harmless.
            }
        }
        if (File.Exists(NextManifestPath)) File.Move(NextManifestPath, ManifestPath, overwrite: true);
        if (File.Exists(NextStatePath)) File.Move(NextStatePath, StatePath, overwrite: true);
        File.Delete(JournalPath);
        foreach (string staging in _layout.StagingDirectories()) TryDeleteDirectory(staging);
        TryDeleteDirectory(CacheDir);
    }

    static void DeleteEmptyParents(string? directory, string rootDirectory)
    {
        string root = rootDirectory.TrimEnd(Path.DirectorySeparatorChar);
        while (directory is not null && directory.Length > root.Length
               && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
