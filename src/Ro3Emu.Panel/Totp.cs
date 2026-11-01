using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.Panel;

/// <summary>
/// Time-based one-time passwords (RFC 6238 over the HOTP of RFC 4226): HMAC-SHA1, 30-second steps, 6 digits, the
/// parameters every authenticator app uses by default (several ignore any other algorithm). HMAC-SHA1 is what RFC 4226
/// specifies; SHA-1's collision weakness does not reach an HMAC (RFC 4226 appendix A). Secrets travel as RFC 4648 base32
/// without padding.
/// </summary>
public static class Totp
{
    public const int SecretBytes = 20, Digits = 6, StepSeconds = 30;
    /// <summary>Steps accepted on either side of the current one: a phone's clock may be off by up to 30 seconds.</summary>
    public const int Window = 1;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The time step of <paramref name="time"/> (RFC 6238 T, with T0 = 0).</summary>
    public static long TimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The 6-digit code of one step (RFC 4226 section 5.3, dynamic truncation).</summary>
    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, secret);
        hmac.AppendData(counter);
        Span<byte> hash = stackalloc byte[20];
        hmac.GetHashAndReset(hash);
        int offset = hash[^1] & 0x0F;
        int binary = (hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step whose code <paramref name="code"/> is, within <see cref="Window"/> of now and after
    /// <paramref name="lastUsed"/> (a code is good once), or null. Every candidate is compared in constant time.
    /// </summary>
    public static long? Match(byte[] secret, string code, DateTimeOffset now, long lastUsed)
    {
        code = Normalize(code);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        byte[] sent = Encoding.ASCII.GetBytes(code);
        long current = TimeStep(now);
        long? found = null;
        for (long step = current - Window; step <= current + Window; step++)
        {
            bool equal = CryptographicOperations.FixedTimeEquals(sent, Encoding.ASCII.GetBytes(Code(secret, step)));
            if (equal && step > lastUsed && found == null) found = step;
        }
        return found;
    }

    /// <summary>Without blanks: apps show "123 456", people paste it with the blank.</summary>
    public static string Normalize(string? code) => new([.. (code ?? "").Where(c => !char.IsWhiteSpace(c))]);

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = buffer << 8 | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Base32Alphabet[buffer >> (bits - 5) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Base32Alphabet[buffer << (5 - bits) & 31]);
        return text.ToString();
    }

    /// <summary>Base32 to bytes, ignoring blanks, dashes, padding and letter case; null when a character is not base32.</summary>
    public static byte[]? FromBase32(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char c in text.ToUpperInvariant())
        {
            if (c is ' ' or '-' or '=') continue;
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0) return null;
            buffer = buffer << 5 | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    /// <summary>The otpauth:// address an authenticator app reads (Google Authenticator's key URI format).</summary>
    public static string Uri(string issuer, string account, byte[] secret) =>
        "otpauth://totp/" + System.Uri.EscapeDataString(issuer) + ":" + System.Uri.EscapeDataString(account) +
        "?secret=" + ToBase32(secret) + "&issuer=" + System.Uri.EscapeDataString(issuer) +
        "&algorithm=SHA1&digits=" + Digits.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "&period=" + StepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
