using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Patcher;

public sealed class ChunkRef
{
    /// <summary>SHA-256 (lower-case hex) of the raw chunk bytes.</summary>
    [JsonPropertyName("h")] public string Hash { get; set; } = "";
    /// <summary>Raw size.</summary>
    [JsonPropertyName("s")] public int Size { get; set; }
    /// <summary>Stored (compressed) size in the repository.</summary>
    [JsonPropertyName("c")] public int StoredSize { get; set; }
}

public sealed class ManifestFile
{
    /// <summary>Relative path with forward slashes.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("chunks")] public List<ChunkRef> Chunks { get; set; } = new();
}

public sealed class LaunchInfo
{
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
    [JsonPropertyName("args")] public string Args { get; set; } = "";
}

public sealed class ChunkingInfo
{
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("avg")] public int Avg { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }

    public ChunkingParameters ToParameters() => new(Min, Avg, Max);
    public static ChunkingInfo From(ChunkingParameters p) => new() { Min = p.Min, Avg = p.Avg, Max = p.Max };
}

public sealed class Manifest
{
    public const int CurrentFormat = 1;

    [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
    [JsonPropertyName("launch")] public LaunchInfo Launch { get; set; } = new();
    [JsonPropertyName("chunking")] public ChunkingInfo Chunking { get; set; } = ChunkingInfo.From(ChunkingParameters.Default);
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();

    public byte[] ToCompressedBytes()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, Json.Compact);
        using var compressor = new ZstdSharp.Compressor(19);
        return compressor.Wrap(json).ToArray();
    }

    internal static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static Manifest FromCompressedBytes(byte[] data)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        byte[] json = decompressor.Unwrap(data).ToArray();
        var manifest = JsonSerializer.Deserialize<Manifest>(json, Json.Compact)
            ?? throw new InvalidDataException("Empty manifest.");
        if (manifest.Format != CurrentFormat)
        {
            throw new InvalidDataException($"Unsupported manifest format {manifest.Format}; update the launcher.");
        }
        foreach (var file in manifest.Files)
        {
            PathRules.ValidateRelative(file.Path);
            // Chunk hashes name cache files: only a SHA-256 in lower-case hex may become a path.
            if (file.Sha256.Length != 0 && !IsSha256Hex(file.Sha256)) throw new InvalidDataException($"Bad file hash in the manifest: {file.Path}");
            foreach (var chunk in file.Chunks)
            {
                if (!IsSha256Hex(chunk.Hash) || chunk.Size < 0 || chunk.StoredSize < 0)
                    throw new InvalidDataException($"Bad chunk in the manifest: {file.Path}");
            }
        }
        return manifest;
    }
}

public sealed class LauncherRelease
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>channels/&lt;name&gt;.json — the only mutable file the launcher polls.</summary>
public sealed class ChannelInfo
{
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("manifest")] public string Manifest { get; set; } = "";
    [JsonPropertyName("manifestSha256")] public string ManifestSha256 { get; set; } = "";
    [JsonPropertyName("published")] public DateTimeOffset Published { get; set; }
    [JsonPropertyName("launcher")] public LauncherRelease? Launcher { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256File(string path, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            onBytes?.Invoke(read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public static class PathRules
{
    /// <summary>Rejects absolute paths and "..": a manifest must never write outside the install directory.</summary>
    public static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Invalid path in manifest: '{path}'.");
        }
    }

    public static string ToLocal(string root, string relative)
    {
        ValidateRelative(relative);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        string rootFull = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path escapes the install directory: '{relative}'.");
        }
        return full;
    }

    public static string ChunkRelative(string hash) => $"chunks/{hash[..2]}/{hash}.zst";
}
