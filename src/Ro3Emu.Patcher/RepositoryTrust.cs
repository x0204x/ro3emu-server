using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Patcher;

/// <summary>
/// Pins the publisher identities accepted by a launcher. Production launchers should require HTTPS and a signed
/// channel. An explicit unsigned policy remains available for isolated LAN/Tailscale labs and old repositories.
/// </summary>
public sealed class RepositoryTrustOptions
{
    public bool RequireHttps { get; init; }
    public bool RequireSignedChannel { get; init; }
    public int MaxMetadataBytes { get; init; } = 1024 * 1024;
    public IReadOnlyDictionary<string, string> TrustedPublicKeys { get; init; } = new Dictionary<string, string>();

    public static RepositoryTrustOptions UnsignedLab { get; } = new();
}

/// <summary>Detached signature stored beside a channel as <c>channels/&lt;name&gt;.json.sig</c>.</summary>
public sealed class DetachedSignature
{
    public const int CurrentFormat = 1;
    public const string CurrentAlgorithm = "rsa-pss-sha256";

    [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = CurrentAlgorithm;
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

/// <summary>Private publishing identity. The private PEM must never be committed or embedded in the launcher.</summary>
public sealed record RepositorySigningOptions(string KeyId, string PrivateKeyPem);

public static class RepositorySignatures
{
    public static byte[] Sign(ReadOnlySpan<byte> payload, RepositorySigningOptions signing)
    {
        ValidateKeyId(signing.KeyId);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(signing.PrivateKeyPem);
        if (rsa.KeySize < 3072)
        {
            throw new CryptographicException("Repository signing keys must be RSA-3072 or stronger.");
        }
        byte[] signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = new DetachedSignature { KeyId = signing.KeyId, Signature = Convert.ToBase64String(signature) };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Json.Indented);
    }

    public static void Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signatureJson, RepositoryTrustOptions trust)
    {
        DetachedSignature envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DetachedSignature>(signatureJson, Json.Compact)
                ?? throw new PatcherException("The update channel signature is empty.");
        }
        catch (JsonException e)
        {
            throw new PatcherException("The update channel signature is invalid JSON.", e);
        }
        if (envelope.Format != DetachedSignature.CurrentFormat || envelope.Algorithm != DetachedSignature.CurrentAlgorithm)
        {
            throw new PatcherException("The update channel uses an unsupported signature format.");
        }
        ValidateKeyId(envelope.KeyId);
        if (!trust.TrustedPublicKeys.TryGetValue(envelope.KeyId, out string? pem))
        {
            throw new PatcherException($"The update channel was signed by an unknown key '{envelope.KeyId}'.");
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException e)
        {
            throw new PatcherException("The update channel signature is not valid base64.", e);
        }
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 3072 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                throw new PatcherException("The update channel signature does not match its contents.");
            }
        }
        catch (CryptographicException e)
        {
            throw new PatcherException("The configured update publisher key is invalid.", e);
        }
    }

    static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 80
            || keyId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new PatcherException("The repository signing key id is invalid.");
        }
    }
}
