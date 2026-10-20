using System.Security.Cryptography;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// One-time handoff from the encrypted game gate to the voice gate: the game gate hands the client a 16-byte token
/// for its own role id and the session's 32-byte stream secret (77016 VoiceTicket); the voice gate's Auth frame
/// presents the token back, and every frame after it is sealed with the secret
/// (<see cref="Ro3Emu.ClientHooks.VoiceCipher"/>). Mirrors <see cref="Micro.GateAuthRegistry"/>'s lock +
/// constant-time compare shape. In memory only.
/// </summary>
public sealed class VoiceTicketRegistry(TimeProvider clock)
{
    private sealed record Ticket(string Account, byte[] Token, byte[] Secret, DateTimeOffset Expires);

    private readonly object _sync = new();
    private readonly Dictionary<long, Ticket> _byRole = new();

    /// <summary>A fresh ticket for <paramref name="roleId"/>, replacing whatever ticket that role already held.</summary>
    public VoiceTicketGrant Issue(string account, long roleId, int ttlSeconds)
    {
        var token = RandomNumberGenerator.GetBytes(16);
        var secret = Ro3Emu.ClientHooks.VoiceCipher.NewSecret();
        var expires = clock.GetUtcNow() + TimeSpan.FromSeconds(ttlSeconds);
        lock (_sync) _byRole[roleId] = new Ticket(account, token, secret, expires);
        return new VoiceTicketGrant(token, secret);
    }

    /// <summary>
    /// The account the ticket belongs to and its stream secret, or null for a wrong token or proof, an unknown role id,
    /// or an expired ticket. <paramref name="proof"/> is the Auth's <see cref="Ro3Emu.ClientHooks.VoiceCipher.AuthProof"/>:
    /// the token alone can be read off the wire, the proof needs the secret.
    /// Single use: only a successful match removes the ticket (so it cannot be replayed); a wrong-token or wrong-proof
    /// attempt leaves a still-valid ticket in place for a retry with the right one, an expired one is discarded outright.
    /// </summary>
    public VoiceTicketClaim? Consume(long roleId, byte[] token, byte[] proof)
    {
        lock (_sync)
        {
            if (!_byRole.TryGetValue(roleId, out var ticket)) return null;
            if (clock.GetUtcNow() > ticket.Expires) { _byRole.Remove(roleId); return null; }
            if (!CryptographicOperations.FixedTimeEquals(ticket.Token, token)) return null;
            byte[] expected = Ro3Emu.ClientHooks.VoiceCipher.AuthProof(ticket.Secret, roleId, ticket.Token);
            if (!Ro3Emu.ClientHooks.VoiceCipher.ProofEquals(expected, proof)) return null;
            _byRole.Remove(roleId);
            return new VoiceTicketClaim(ticket.Account, ticket.Secret);
        }
    }
}

/// <summary>What the game gate sends the client: the Auth token and the stream secret.</summary>
public sealed record VoiceTicketGrant(byte[] Token, byte[] Secret);

/// <summary>A consumed ticket: whose it was and the secret its stream is sealed with.</summary>
public sealed record VoiceTicketClaim(string Account, byte[] Secret);
