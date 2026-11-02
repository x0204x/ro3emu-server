using System.Security.Cryptography;
using Phonest.Proto;

namespace Ro3Emu.Server.Net;

/// <summary>
/// The gate RSA key pair. The client RSA-OAEP(SHA-1)-encrypts an EncryptKey{padding, key, token, padding} with the
/// public key compiled into Phonest.Common (Utils.GenEncryptKeyFromToken); ClientPatch swaps that PEM for ours.
/// </summary>
public sealed class GateCrypto
{
    private readonly RSA _rsa;

    private GateCrypto(RSA rsa) => _rsa = rsa;

    /// <summary>PKCS#1 "RSA PUBLIC KEY" PEM, the form Phonest's TLSUtils.ImportPublicKey reads.</summary>
    public string PublicKeyPem => _rsa.ExportRSAPublicKeyPem();

    public static GateCrypto LoadOrCreate(string dataDir, ILogger log)
    {
        Accounts.PrivateFiles.Directory(dataDir);
        var privatePath = Path.Combine(dataDir, "gate_rsa_private.pem");
        var rsa = RSA.Create();
        if (File.Exists(privatePath))
        {
            rsa.ImportFromPem(File.ReadAllText(privatePath));
        }
        else
        {
            rsa.KeySize = 2048;
            File.WriteAllText(privatePath, rsa.ExportRSAPrivateKeyPem());
            log.LogInformation("Generated a new gate RSA key in {Path}", privatePath);
        }
        // Also an older key, written before the server restricted it.
        Accounts.PrivateFiles.Files(privatePath);
        var crypto = new GateCrypto(rsa);
        File.WriteAllText(Path.Combine(dataDir, "gate_rsa_public.pem"), crypto.PublicKeyPem);
        return crypto;
    }

    /// <summary>Recovers the 20-byte RC4 session key, checking the echoed handshake token.</summary>
    public byte[] OpenSessionKey(EncryptKeyString keyString, long expectedToken)
    {
        var plain = _rsa.Decrypt(keyString.KeyString.ToByteArray(), RSAEncryptionPadding.OaepSHA1);
        var key = EncryptKey.Parser.ParseFrom(plain);
        if (key.Token != expectedToken) throw new CryptographicException("handshake token mismatch");
        if (key.Key.IsEmpty) throw new CryptographicException("empty session key");
        return key.Key.ToByteArray();
    }

    public static long NewHandshakeToken() => BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
}
