using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// The 77015/77016 handoff from the encrypted game gate to the voice gate (which seals its stream with the secret
/// handed out here). Ours (the client's message
/// table has no such id; 77010-77014 are already taken by ServerImAdapter/Diagnostics/FeedbackButton/PassChallenges).
/// Routed like FeedbackService.SubmitMsg: AdmitRole, since it only reads and writes the sender's own ticket.
/// </summary>
public sealed class VoiceTicketService(IOptions<ServerOptions> options, VoiceTicketRegistry tickets) : Game.Messaging.IMessageModule
{
    /// <summary>Up: empty body.</summary>
    public const uint TicketReq = 77015;

    /// <summary>Down: u16 port | u8 hostLen | host ASCII | i64 roleId | 16-byte token | u32 ttlSeconds | 32-byte
    /// stream secret, little-endian. The host is <see cref="Http.SdkEndpoints.GateHost"/> of the address this session
    /// reached the game gate on (the in-game client does not know it), the same rule as the server list. Relay off:
    /// port 0, empty host, zero token, ttl 0, zero secret.</summary>
    public const uint Ticket = 77016;

    public void Map(Game.Messaging.MessageRoutes routes)
    {
        routes.AdmitRole(id => id == TicketReq);
        routes.Map(TicketReq, "voice", async (session, _, _) =>
        {
            var voice = options.Value.Voice;
            long roleId = session.Role?.Id ?? 0;
            if (!options.Value.VoiceServed() || session.Role == null || session.Account == null)
            {
                await session.SendGameRawAsync(Ticket, Encode(0, "", roleId, new byte[16], 0, new byte[Ro3Emu.ClientHooks.VoiceCipher.SecretSize]));
                return;
            }
            string host = Http.SdkEndpoints.GateHost(session.Channel.LocalAddress?.ToString(), options.Value.PublicHost);
            var grant = tickets.Issue(session.Account, roleId, voice.TicketSeconds);
            await session.SendGameRawAsync(Ticket, Encode((ushort)voice.Port, host, roleId, grant.Token, voice.TicketSeconds, grant.Secret));
        });
    }

    private static byte[] Encode(ushort port, string host, long roleId, byte[] token, int ttlSeconds, byte[] secret)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(host.Length > 255 ? host[..255] : host);
        var body = new byte[2 + 1 + hostBytes.Length + 8 + 16 + 4 + secret.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), port);
        body[2] = (byte)hostBytes.Length;
        hostBytes.CopyTo(body.AsSpan(3));
        int at = 3 + hostBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(at, 8), roleId);
        token.AsSpan(0, 16).CopyTo(body.AsSpan(at + 8, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 24, 4), (uint)ttlSeconds);
        secret.CopyTo(body.AsSpan(at + 28));
        return body;
    }
}
