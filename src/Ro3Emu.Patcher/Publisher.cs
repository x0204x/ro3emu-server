using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Ro3Emu.Patcher;

public sealed class PublishOptions
{
    /// <summary>Destination path (forward slashes, relative to the install's game directory) → source file on disk.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Files { get; init; }
    public required string RepoDirectory { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public required string LaunchExe { get; init; }
    public string LaunchArgs { get; init; } = "";
    public ChunkingParameters Chunking { get; init; } = ChunkingParameters.Default;
    public int CompressionLevel { get; init; } = 19;
    public int Workers { get; init; } = Environment.ProcessorCount;
    /// <summary>Write the manifest but leave the channel pointing at its previous release.</summary>
    public bool SkipChannel { get; init; }
    public LauncherRelease? Launcher { get; init; }
    public RepositorySigningOptions? Signing { get; init; }
}

public sealed record PublishResult(Manifest Manifest, ChannelInfo Channel, int NewChunks, long NewStoredBytes, int ReusedChunks);

/// <summary>Turns a set of files into a release: new chunks into chunks/, the manifest, then the channel pointer.</summary>
public static class Publisher
{
    /// <summary>Every file under <paramref name="directory"/>, minus the excluded relative paths/prefixes.</summary>
    public static List<KeyValuePair<string, string>> CollectDirectory(string directory, string destinationPrefix, Func<string, bool> exclude)
    {
        var result = new List<KeyValuePair<string, string>>();
        directory = Path.GetFullPath(directory);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(directory, "*", options))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (exclude(relative))
            {
                continue;
            }
            string destination = string.IsNullOrEmpty(destinationPrefix) ? relative : destinationPrefix.TrimEnd('/') + "/" + relative;
            result.Add(new(destination, file));
        }
        return result;
    }

    public static async Task<PublishResult> PublishAsync(PublishOptions options, IProgress<string>? log = null, CancellationToken ct = default)
    {
        options.Chunking.Validate();
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Version, @"^[0-9A-Za-z][0-9A-Za-z._+-]*$"))
        {
            throw new ArgumentException($"Invalid version '{options.Version}'.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Channel, @"^[a-z0-9-]+$"))
        {
            throw new ArgumentException($"Invalid channel '{options.Channel}'.");
        }

        string repo = Path.GetFullPath(options.RepoDirectory);
        Directory.CreateDirectory(repo);
        // One publisher at a time: two writing the same chunks or channel race each other.
        using var repoLock = AcquireLock(repo);
        string manifestRelative = $"manifests/{options.Version}.json.zst";
        string manifestPath = Path.Combine(repo, manifestRelative);
        if (File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Release {options.Version} already exists; releases are immutable, pick a new version.");
        }

        var duplicates = options.Files.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException("Duplicate destination paths: " + string.Join(", ", duplicates));
        }
        if (!options.Files.Any(f => string.Equals(f.Key, options.LaunchExe, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The launch exe '{options.LaunchExe}' is not part of the release.");
        }

        var manifest = new Manifest
        {
            Version = options.Version,
            Created = DateTimeOffset.UtcNow,
            Launch = new LaunchInfo { Exe = options.LaunchExe, Args = options.LaunchArgs },
            Chunking = ChunkingInfo.From(options.Chunking),
        };

        // Chunks are compressed and written by workers; the reader thread only cuts and hashes.
        var queue = Channel.CreateBounded<(string Hash, byte[] Buffer, int Length, ChunkRef Ref)>(options.Workers * 2);
        int newChunks = 0, reused = 0;
        long newBytes = 0;

        var workers = Enumerable.Range(0, Math.Max(1, options.Workers)).Select(_ => Task.Run(async () =>
        {
            using var compressor = new ZstdSharp.Compressor(options.CompressionLevel);
            await foreach (var item in queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    byte[] compressed = compressor.Wrap(item.Buffer.AsSpan(0, item.Length)).ToArray();
                    string target = Path.Combine(repo, PathRules.ChunkRelative(item.Hash));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(temp, compressed, ct);
                    MoveIntoPlace(temp, target);
                    item.Ref.StoredSize = compressed.Length;
                    Interlocked.Add(ref newBytes, compressed.Length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.Buffer);
                }
            }
        }, ct)).ToArray();

        // Stored sizes of chunks that already exist are read from disk; chunks queued in this run are filled in by the workers.
        var pendingRefs = new Dictionary<string, List<ChunkRef>>();
        try
        {
            foreach (var (destination, source) in options.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                PathRules.ValidateRelative(destination);
                log?.Report($"  {destination}");
                var file = new ManifestFile { Path = destination };
                using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var reader = new ChunkReader(stream, options.Chunking);
                while (reader.Next(out _, out var chunk))
                {
                    whole.AppendData(chunk.Span);
                    string hash = Hashing.Sha256Hex(chunk.Span);
                    var chunkRef = new ChunkRef { Hash = hash, Size = chunk.Length };
                    file.Chunks.Add(chunkRef);
                    file.Size += chunk.Length;

                    if (pendingRefs.TryGetValue(hash, out var waiting))
                    {
                        waiting.Add(chunkRef);
                        continue;
                    }
                    string stored = Path.Combine(repo, PathRules.ChunkRelative(hash));
                    var info = new FileInfo(stored);
                    if (info.Exists)
                    {
                        chunkRef.StoredSize = checked((int)info.Length);
                        reused++;
                        continue;
                    }
                    pendingRefs[hash] = new List<ChunkRef> { chunkRef };
                    newChunks++;
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
                    chunk.Span.CopyTo(buffer);
                    await queue.Writer.WriteAsync((hash, buffer, chunk.Length, chunkRef), ct);
                }
                file.Sha256 = Convert.ToHexStringLower(whole.GetHashAndReset());
                manifest.Files.Add(file);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
        }
        await Task.WhenAll(workers);

        foreach (var refs in pendingRefs.Values)
        {
            int stored = refs[0].StoredSize;
            foreach (var r in refs)
            {
                r.StoredSize = stored;
            }
        }

        byte[] manifestBytes = manifest.ToCompressedBytes();
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, ct);

        var channel = new ChannelInfo
        {
            Channel = options.Channel,
            Sequence = await NextSequenceAsync(repo, options.Channel, ct),
            Version = options.Version,
            Manifest = manifestRelative,
            ManifestSha256 = Hashing.Sha256Hex(manifestBytes),
            Published = DateTimeOffset.UtcNow,
            Expires = options.Signing is null ? null : DateTimeOffset.UtcNow.AddDays(14),
            Launcher = options.Launcher,
        };
        if (!options.SkipChannel)
        {
            await WriteChannelAsync(repo, channel, options.Signing, ct);
        }
        return new PublishResult(manifest, channel, newChunks, newBytes, reused);
    }

    static FileStream AcquireLock(string repo)
    {
        try
        {
            return new FileStream(Path.Combine(repo, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException e)
        {
            throw new InvalidOperationException("Another publish is running on this repository.", e);
        }
    }

    /// <summary>Chunks are content-addressed: if the file appeared meanwhile it has the same bytes.</summary>
    static void MoveIntoPlace(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(target))
        {
            File.Delete(temp);
        }
    }

    /// <summary>Points a channel at an existing release (promote beta → stable, roll back).</summary>
    public static async Task<ChannelInfo> SetChannelAsync(string repoDirectory, string channelName, string version, LauncherRelease? launcher,
        RepositorySigningOptions? signing = null, CancellationToken ct = default)
    {
        string repo = Path.GetFullPath(repoDirectory);
        using var repoLock = AcquireLock(repo);
        string manifestRelative = $"manifests/{version}.json.zst";
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(repo, manifestRelative), ct);
        Manifest.FromCompressedBytes(bytes);
        var channel = new ChannelInfo
        {
            Channel = channelName,
            Sequence = await NextSequenceAsync(repo, channelName, ct),
            Version = version,
            Manifest = manifestRelative,
            ManifestSha256 = Hashing.Sha256Hex(bytes),
            Published = DateTimeOffset.UtcNow,
            Expires = signing is null ? null : DateTimeOffset.UtcNow.AddDays(14),
            Launcher = launcher,
        };
        await WriteChannelAsync(repo, channel, signing, ct);
        return channel;
    }

    /// <summary>Publishes a launcher exe (launcher/&lt;version&gt;/) and points the channel's launcher field at it.</summary>
    public static async Task<ChannelInfo> PublishLauncherAsync(string repoDirectory, string channelName, string exePath, string version,
        RepositorySigningOptions? signing = null, CancellationToken ct = default)
    {
        if (!Version.TryParse(version, out _))
        {
            throw new ArgumentException($"Launcher version must look like 1.2.3, got '{version}'.");
        }
        string repo = Path.GetFullPath(repoDirectory);
        using var repoLock = AcquireLock(repo);
        string channelPath = Path.Combine(repo, "channels", channelName + ".json");
        if (!File.Exists(channelPath))
        {
            throw new InvalidOperationException($"Channel '{channelName}' has no release yet; publish the game first.");
        }
        var channel = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(channelPath, ct), Json.Compact)!;
        byte[] exe = await File.ReadAllBytesAsync(exePath, ct);
        string relative = $"launcher/{version}/Ro3EmuLauncher.exe";
        string target = Path.Combine(repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, exe, ct);
        channel.Launcher = new LauncherRelease { Version = version, Url = relative, Sha256 = Hashing.Sha256Hex(exe) };
        channel.Sequence = Math.Max(channel.Sequence + 1, await NextSequenceAsync(repo, channelName, ct));
        channel.Published = DateTimeOffset.UtcNow;
        channel.Expires = signing is null ? null : DateTimeOffset.UtcNow.AddDays(14);
        await WriteChannelAsync(repo, channel, signing, ct);
        return channel;
    }

    /// <summary>Reissues an existing channel with a new sequence, expiry and detached publisher signature.</summary>
    public static async Task<ChannelInfo> SignExistingChannelAsync(string repoDirectory, string channelName,
        RepositorySigningOptions signing, CancellationToken ct = default)
    {
        string repo = Path.GetFullPath(repoDirectory);
        using var repoLock = AcquireLock(repo);
        string path = Path.Combine(repo, "channels", channelName + ".json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Channel '{channelName}' does not exist.");
        }
        var channel = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(path, ct), Json.Compact)
            ?? throw new InvalidDataException($"Channel '{channelName}' is empty.");
        if (!string.Equals(channel.Channel, channelName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Channel identity '{channel.Channel}' does not match '{channelName}'.");
        }
        channel.Sequence = Math.Max(1, checked(channel.Sequence + 1));
        channel.Published = DateTimeOffset.UtcNow;
        channel.Expires = DateTimeOffset.UtcNow.AddDays(14);
        await WriteChannelAsync(repo, channel, signing, ct);
        return channel;
    }

    static async Task<long> NextSequenceAsync(string repo, string channelName, CancellationToken ct)
    {
        string path = Path.Combine(repo, "channels", channelName + ".json");
        if (!File.Exists(path)) return 1;
        try
        {
            var current = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(path, ct), Json.Compact);
            return Math.Max(1, checked((current?.Sequence ?? 0) + 1));
        }
        catch (Exception e) when (e is JsonException or OverflowException)
        {
            throw new InvalidDataException($"Existing channel '{channelName}' is invalid.", e);
        }
    }

    static async Task WriteChannelAsync(string repo, ChannelInfo channel, RepositorySigningOptions? signing, CancellationToken ct)
    {
        string path = Path.Combine(repo, "channels", channel.Channel + ".json");
        if (channel.Launcher is null && File.Exists(path))
        {
            // A game release keeps the launcher release the channel already points at.
            var existing = JsonSerializer.Deserialize<ChannelInfo>(await File.ReadAllBytesAsync(path, ct), Json.Compact);
            channel.Launcher = existing?.Launcher;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(channel, Json.Indented);
        await File.WriteAllBytesAsync(temp, raw, ct);
        File.Move(temp, path, overwrite: true);
        string signaturePath = path + ".sig";
        if (signing is not null)
        {
            byte[] signature = RepositorySignatures.Sign(raw, signing);
            string signatureTemp = signaturePath + ".tmp";
            await File.WriteAllBytesAsync(signatureTemp, signature, ct);
            File.Move(signatureTemp, signaturePath, overwrite: true);
        }
        else if (File.Exists(signaturePath))
        {
            File.Delete(signaturePath);
        }
    }
}
