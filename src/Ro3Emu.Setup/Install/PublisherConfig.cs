using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Setup.Install;

/// <summary>
/// Where the official client comes from: the publisher's own update service, exactly as its launcher uses it
/// (Genesis Launcher\config.ini: RecoveryFullManifestUrl / RecoveryDeltaManifestUrl, RecoveryAllowedOrigins,
/// RecoveryPreferredDownloadOrigin, ManifestApprovedKeyId, ManifestPublicKeySpkiBase64). We never host game files.
/// The built-in values can be replaced by RO3Emu.download.json next to RO3Emu.exe, so a move of the publisher only
/// needs a new list.
/// </summary>
public sealed record PublisherConfig
{
    /// <summary>Origins tried in order for the manifests and every package (each ends with '/').</summary>
    [JsonPropertyName("origins")]
    public string[] Origins { get; init; } = [];

    /// <summary>Origins a manifest's immutable_url may point into (rewritten onto <see cref="Origins"/>).</summary>
    [JsonPropertyName("allowedOrigins")]
    public string[] AllowedOrigins { get; init; } = [];

    [JsonPropertyName("fullManifest")]
    public string FullManifest { get; init; } = "";

    [JsonPropertyName("fullSignature")]
    public string FullSignature { get; init; } = "";

    [JsonPropertyName("deltaManifest")]
    public string? DeltaManifest { get; init; }

    [JsonPropertyName("deltaSignature")]
    public string? DeltaSignature { get; init; }

    [JsonPropertyName("keyId")]
    public string KeyId { get; init; } = "";

    /// <summary>The publisher's RSA public key, SubjectPublicKeyInfo DER in base64.</summary>
    [JsonPropertyName("publicKeySpki")]
    public string PublicKeySpki { get; init; } = "";

    /// <summary>SHA-256 of that DER, checked so a typo in an override file cannot slip in another key unnoticed.</summary>
    [JsonPropertyName("publicKeySpkiSha256")]
    public string PublicKeySpkiSha256 { get; init; } = "";

    /// <summary>The Genesis update service, 16 Oct 2026 (Genesis Launcher\config.ini).</summary>
    public static PublisherConfig Genesis { get; } = new()
    {
        Origins = ["https://update.genesis3.is/", "https://br-gru-1.linodeobjects.com/update.genesis3.is/"],
        AllowedOrigins =
        [
            "https://update.genesis3.is/", "https://br-gru-1.linodeobjects.com/update.genesis3.is/", "https://update.playgenesis3.com/",
            "https://update.playragnarok3.com/", "https://d26b54t50zhv93.cloudfront.net/",
        ],
        FullManifest = "windows/launcher/v1/channels/ro3_recovery/full/manifest.json",
        FullSignature = "windows/launcher/v1/channels/ro3_recovery/full/manifest.sig",
        DeltaManifest = "windows/launcher/v1/channels/ro3_recovery/delta/manifest.json",
        DeltaSignature = "windows/launcher/v1/channels/ro3_recovery/delta/manifest.sig",
        KeyId = "ro3-recovery-production-2026-01",
        PublicKeySpki = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAhX7N8JFJqSBhkYVc4I0uT3WRSIKn09JX77jvQ4vOpBHYqjBIBZKa5eyDBacDB+oZwsGH5s8nPTSRLet0RcchuJAKf+w/J+60IAd1LDNDjzgbNOBP26OFb67MrXWTkJPnjdJv1+kABFInrinGn3bg2N7/P0ngASdDsiUkZW8Qp2+aZ0KqHNltEBBnsPLFdRL3RcBmbTbvHTjwiTSu9CunvniFJJUt6E78TB4MJj+b1pfPVawwXeIBBEUCGsyWh8pyUQJ4aG3wPbUr2hOPtguKwKrTLvN/QnWsn8xEP3WZZuIgNbGsxHtVjuRVM9k/2olgjgqlIDRISx0RhRbKqwufYR/mgNvaB7AaDyUAFPtunehYHujVUg/2hxS/4xlalGnZkXOgt3Xl4EAqX0lrwnqR1xhxz/TLiKwssRYBxpZ3zj0Y9fryX22ZrHKVbqJZX1Qwr7HeYrFvfyNR18hNBdiAPNPJeu2fnHLoedlD7Om2YLqDUSz9DtVE0mNesJ4gk4sjAgMBAAE=",
        PublicKeySpkiSha256 = "2d156a1366dd8e31abc96bd68e1119da239b7591731f1d90cc74fbdffc1f5261",
    };

    public const string OverrideFile = "RO3Emu.download.json";

    /// <summary>RO3Emu.download.json next to RO3Emu.exe when present (fields left out keep the built-in values).</summary>
    public static PublisherConfig Load(string appDir)
    {
        var path = Path.Combine(appDir, OverrideFile);
        if (!File.Exists(path)) return Genesis;
        var custom = JsonSerializer.Deserialize<PublisherConfig>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{OverrideFile} is empty");
        return new PublisherConfig
        {
            Origins = custom.Origins.Length > 0 ? custom.Origins : Genesis.Origins,
            AllowedOrigins = custom.AllowedOrigins.Length > 0 ? custom.AllowedOrigins : Genesis.AllowedOrigins.Concat(custom.Origins).ToArray(),
            FullManifest = custom.FullManifest.Length > 0 ? custom.FullManifest : Genesis.FullManifest,
            FullSignature = custom.FullSignature.Length > 0 ? custom.FullSignature : Genesis.FullSignature,
            DeltaManifest = custom.DeltaManifest ?? Genesis.DeltaManifest,
            DeltaSignature = custom.DeltaSignature ?? Genesis.DeltaSignature,
            KeyId = custom.KeyId.Length > 0 ? custom.KeyId : Genesis.KeyId,
            PublicKeySpki = custom.PublicKeySpki.Length > 0 ? custom.PublicKeySpki : Genesis.PublicKeySpki,
            PublicKeySpkiSha256 = custom.PublicKeySpki.Length > 0 ? custom.PublicKeySpkiSha256 : Genesis.PublicKeySpkiSha256,
        };
    }

    /// <summary>The key, after checking its pinned SHA-256.</summary>
    public RSA PublicKey()
    {
        var der = Convert.FromBase64String(PublicKeySpki);
        var pin = Convert.ToHexStringLower(SHA256.HashData(der));
        if (!string.Equals(pin, PublicKeySpkiSha256, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("The publisher key in the download settings does not match its fingerprint.",
                $"Delete {OverrideFile} next to RO3Emu.exe or get a current one from the RO3Emu releases page.");
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(der, out _);
        return rsa;
    }

    /// <summary>
    /// The URLs to try for a package, best first: its immutable_url moved onto each configured origin (when it lies in
    /// an allowed origin), then the URL itself if its origin is allowed. A URL outside every allowed origin is refused.
    /// </summary>
    public List<string> PackageUrls(string immutableUrl)
    {
        var allowed = AllowedOrigins.Concat(Origins).FirstOrDefault(o => immutableUrl.StartsWith(o, StringComparison.OrdinalIgnoreCase))
            ?? throw new InstallException($"Genesis' manifest points outside its own servers ({immutableUrl}).",
                "Update RO3Emu, or add the new address to RO3Emu.download.json.");
        var rest = immutableUrl[allowed.Length..];
        var urls = Origins.Select(o => o + rest).ToList();
        if (!urls.Contains(immutableUrl, StringComparer.OrdinalIgnoreCase)) urls.Add(immutableUrl);
        return urls;
    }
}
