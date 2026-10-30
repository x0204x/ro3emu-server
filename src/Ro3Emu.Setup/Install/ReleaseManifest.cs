using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Setup.Install;

/// <summary>
/// The publisher's signed release manifest (protocol "ro3-recovery-launcher-update", schema 2): the release, the zip
/// packages, the operations that take files out of them and the complete list of target files. manifest.sig is an
/// RSA-3072 SHA-256 PKCS#1 v1.5 signature over the exact manifest bytes.
/// </summary>
public sealed class ReleaseManifest
{
    public const string Protocol = "ro3-recovery-launcher-update";
    public const string Algorithm = "RSA-3072-SHA256-PKCS1v15";

    [JsonPropertyName("protocol")] public string ProtocolName { get; init; } = "";
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("release")] public ReleaseInfo Release { get; init; } = new();
    [JsonPropertyName("signing")] public SigningInfo Signing { get; init; } = new();
    [JsonPropertyName("packages")] public List<Package> Packages { get; init; } = [];
    [JsonPropertyName("operations")] public List<Operation> Operations { get; init; } = [];
    [JsonPropertyName("target_files")] public List<TargetFile> TargetFiles { get; init; } = [];
    [JsonPropertyName("totals")] public Totals Sizes { get; init; } = new();

    public sealed class ReleaseInfo
    {
        [JsonPropertyName("release_id")] public string Id { get; init; } = "";
        [JsonPropertyName("release_sequence")] public long Sequence { get; init; }
        [JsonPropertyName("target_build")] public Build TargetBuild { get; init; } = new();
        [JsonPropertyName("required_base_build")] public Build? RequiredBaseBuild { get; init; }
        [JsonPropertyName("expires_utc")] public DateTime? ExpiresUtc { get; init; }
        [JsonPropertyName("entry_point")] public string EntryPoint { get; init; } = "";
        [JsonPropertyName("platform")] public string Platform { get; init; } = "";
    }

    public sealed class Build
    {
        [JsonPropertyName("build_id")] public string Id { get; init; } = "";
        [JsonPropertyName("content_sha256")] public string ContentSha256 { get; init; } = "";
    }

    public sealed class SigningInfo
    {
        [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = "";
        [JsonPropertyName("key_id")] public string KeyId { get; init; } = "";
    }

    public sealed class Package
    {
        [JsonPropertyName("package_id")] public string Id { get; init; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
        [JsonPropertyName("byte_length")] public long Length { get; init; }
        [JsonPropertyName("immutable_url")] public string Url { get; init; } = "";
    }

    public sealed class Operation
    {
        [JsonPropertyName("action")] public string Action { get; init; } = "";
        [JsonPropertyName("logical_path")] public string Path { get; init; } = "";
        [JsonPropertyName("package_id")] public string PackageId { get; init; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
        [JsonPropertyName("byte_length")] public long Length { get; init; }
    }

    public sealed class TargetFile
    {
        [JsonPropertyName("logical_path")] public string Path { get; init; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
        [JsonPropertyName("byte_length")] public long Length { get; init; }
    }

    public sealed class Totals
    {
        [JsonPropertyName("download_bytes")] public long Download { get; init; }
        [JsonPropertyName("expanded_bytes")] public long Expanded { get; init; }
        [JsonPropertyName("required_free_bytes")] public long RequiredFree { get; init; }
    }

    /// <summary>
    /// Checks the signature over the exact bytes with the publisher's key, then parses and sanity-checks the manifest.
    /// Anything wrong throws: nothing from an unverified manifest is ever used.
    /// </summary>
    public static ReleaseManifest Verify(byte[] manifest, byte[] signature, PublisherConfig config, DateTime? nowUtc = null)
    {
        using (var key = config.PublicKey())
        {
            if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InstallException("The game download information is not signed by the publisher.",
                    "Try again later. If it keeps failing, the download servers moved or were tampered with: check the RO3Emu releases page.");
        }
        ReleaseManifest parsed;
        try { parsed = JsonSerializer.Deserialize<ReleaseManifest>(manifest) ?? throw new JsonException("empty"); }
        catch (JsonException e) { throw new InstallException("Genesis' release manifest could not be read: " + e.Message, "Update RO3Emu."); }
        if (parsed.ProtocolName != Protocol || parsed.SchemaVersion != 2)
            throw new InstallException($"The publisher changed its release format ({parsed.ProtocolName} {parsed.SchemaVersion}).", "Update RO3Emu.");
        if (parsed.Signing.KeyId != config.KeyId || parsed.Signing.Algorithm != Algorithm)
            throw new InstallException($"The manifest names another signing key ({parsed.Signing.KeyId}).", "Update RO3Emu.");
        if (parsed.Release.ExpiresUtc is { } expires && expires < (nowUtc ?? DateTime.UtcNow))
            throw new InstallException($"Genesis' release information expired on {expires:yyyy-MM-dd}.", "Try again later; the publisher renews it with each release.");
        foreach (var path in parsed.TargetFiles.Select(t => t.Path).Concat(parsed.Operations.Select(o => o.Path)))
            if (!SafePath(path)) throw new InstallException($"The manifest names an unsafe path ({path}).", "Do not use this download source.");
        return parsed;
    }

    /// <summary>A relative path inside the game folder: no root, no "..", no drive or stream names.</summary>
    public static bool SafePath(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.StartsWith('\\') && !path.Contains(':')
        && path.Split('/', '\\').All(part => part.Length > 0 && part != "." && part != "..");
}
