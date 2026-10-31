using System.Text.Json;
using System.Text.Json.Serialization;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher;

public sealed class InstalledFile
{
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>LastWriteTimeUtc ticks when the launcher last verified the file.</summary>
    [JsonPropertyName("mtime")] public long MtimeTicks { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>What the launcher installed: lets a check skip hashing files whose size and mtime did not change.</summary>
public sealed class LocalState
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("baseClient")] public GenesisIdentity? BaseClient { get; set; }
    [JsonPropertyName("serverVersion")] public string? ServerVersion { get; set; }
    [JsonPropertyName("patchSha256")] public string? PatchSha256 { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("manifestSha256")] public string ManifestSha256 { get; set; } = "";
    [JsonPropertyName("launch")] public LaunchInfo Launch { get; set; } = new();
    [JsonPropertyName("files")] public Dictionary<string, InstalledFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static LocalState? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var state = JsonSerializer.Deserialize<LocalState>(File.ReadAllBytes(path), Json.Compact);
            if (state is not null)
            {
                state.Files = new Dictionary<string, InstalledFile>(state.Files, StringComparer.OrdinalIgnoreCase);
            }
            return state;
        }
        catch (JsonException)
        {
            // A damaged state only costs a full re-hash.
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, Json.Compact));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The steps of a commit, written before the first move so a crash mid-commit is finished on the next start.</summary>
public sealed class CommitJournal
{
    [JsonPropertyName("moves")] public List<string> Moves { get; set; } = new();
    [JsonPropertyName("deletes")] public List<string> Deletes { get; set; } = new();
}
