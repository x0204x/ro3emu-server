using System.Security.Cryptography;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// Phonest gate auth: the game gate's OnGetAuth(callerId, relogin, reconnect, auth) gives the
/// client a 16-byte secret for its avatar entity id; the client presents it as micro PhonestGate.bind {Id, Auth,
/// ClusterId} on whatever micro connection it has (a silent reconnect always opens a new one), which binds that
/// connection to the account. In memory only; touched from the game loop and from micro connections, hence the lock.
/// </summary>
public sealed class GateAuthRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (string Account, byte[] Auth)> _byCaller = new(StringComparer.Ordinal);

    /// <summary>A fresh secret for <paramref name="callerId"/>, replacing the previous one.</summary>
    public byte[] Issue(string callerId, string account)
    {
        var auth = RandomNumberGenerator.GetBytes(16);
        lock (_sync) _byCaller[callerId] = (account, auth);
        return auth;
    }

    /// <summary>The account the secret belongs to, or null (constant-time compare).</summary>
    public string? Verify(string? callerId, byte[]? auth)
    {
        if (callerId == null || auth == null) return null;
        lock (_sync)
            return _byCaller.TryGetValue(callerId, out var entry) && CryptographicOperations.FixedTimeEquals(entry.Auth, auth)
                ? entry.Account : null;
    }

    /// <summary>refreshAuth: replaces a valid secret of <paramref name="account"/> with a new one.</summary>
    public byte[]? Refresh(string? callerId, byte[]? oldAuth, string? account)
    {
        if (callerId == null || oldAuth == null || account == null) return null;
        lock (_sync)
        {
            if (!_byCaller.TryGetValue(callerId, out var entry) || !CryptographicOperations.FixedTimeEquals(entry.Auth, oldAuth)
                || !string.Equals(entry.Account, account, StringComparison.Ordinal))
                return null;
            var auth = RandomNumberGenerator.GetBytes(16);
            _byCaller[callerId] = (entry.Account, auth);
            return auth;
        }
    }

    public void Revoke(string callerId)
    {
        lock (_sync) _byCaller.Remove(callerId);
    }
}
