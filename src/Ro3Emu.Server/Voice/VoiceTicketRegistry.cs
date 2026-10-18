using System.Security.Cryptography;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// One-time handoff from the encrypted game gate to the plaintext voice gate: the game gate hands the client a
/// 16-byte token for its own role id (77016 VoiceTicket); the voice gate's Auth frame presents both back. Mirrors
/// <see cref="Micro.GateAuthRegistry"/>'s lock + constant-time compare shape. In memory only.
/// </summary>
public sealed class VoiceTicketRegistry(TimeProvider clock)
{
    private sealed record Ticket(string Account, byte[] Token, DateTimeOffset Expires);

    private readonly object _sync = new();
    private readonly Dictionary<long, Ticket> _byRole = new();

    /// <summary>A fresh ticket for <paramref name="roleId"/>, replacing whatever ticket that role already held.</summary>
    public byte[] Issue(string account, long roleId, int ttlSeconds)
    {
        var token = RandomNumberGenerator.GetBytes(16);
        var expires = clock.GetUtcNow() + TimeSpan.FromSeconds(ttlSeconds);
        lock (_sync) _byRole[roleId] = new Ticket(account, token, expires);
        return token;
    }

    /// <summary>
    /// The account the ticket belongs to, or null for a wrong token, an unknown role id, or an expired ticket.
    /// Single use: only a successful match removes the ticket (so it cannot be replayed); a wrong-token attempt
    /// leaves a still-valid ticket in place for a retry with the right one, an expired one is discarded outright.
    /// </summary>
    public string? Consume(long roleId, byte[] token)
    {
        lock (_sync)
        {
            if (!_byRole.TryGetValue(roleId, out var ticket)) return null;
            if (clock.GetUtcNow() > ticket.Expires) { _byRole.Remove(roleId); return null; }
            if (!CryptographicOperations.FixedTimeEquals(ticket.Token, token)) return null;
            _byRole.Remove(roleId);
            return ticket.Account;
        }
    }
}
