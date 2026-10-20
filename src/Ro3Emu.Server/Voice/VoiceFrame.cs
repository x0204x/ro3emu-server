using System.Buffers.Binary;

namespace Ro3Emu.Server.Voice;

/// <summary>Thrown by <see cref="VoiceFrame.ReadAsync"/> on a frame the voice gate must close the connection for
/// (bad magic, a payload past the connection's cap).</summary>
public sealed class VoiceProtocolException(string message) : Exception(message);

/// <summary>
/// The voice gate's own tiny framing, independent of the game gate's Phonest/Protobuf stack: 'V' 'T' | type u8 |
/// len u16 LE | payload. Auth is the one frame sent as is; every frame after it, both ways, carries a payload sealed
/// with the ticket's secret (<see cref="Ro3Emu.ClientHooks.VoiceCipher"/>: counter | ciphertext | tag), so len
/// counts the sealed bytes.
/// </summary>
public static class VoiceFrame
{
    public const byte Magic0 = (byte)'V', Magic1 = (byte)'T';

    /// <summary>Magic (2) + type (1) + length (2).</summary>
    public const int HeaderSize = 5;

    public static byte[] Write(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = Magic0;
        frame[1] = Magic1;
        frame[2] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Reads one frame off <paramref name="stream"/>. <paramref name="maxPayload"/> is the connection's configured
    /// cap (VoiceOptions.MaxPayload + the header room the caller already added); a longer declared length or a bad
    /// magic throws <see cref="VoiceProtocolException"/> so the caller closes the socket instead of resyncing on
    /// garbage. ReadExactlyAsync already loops over partial TCP reads.
    /// </summary>
    public static async Task<(byte Type, byte[] Payload)> ReadAsync(Stream stream, int maxPayload, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct);
        if (header[0] != Magic0 || header[1] != Magic1)
            throw new VoiceProtocolException("bad magic");
        byte type = header[2];
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(3, 2));
        if (length > maxPayload)
            throw new VoiceProtocolException($"frame of {length} bytes exceeds the {maxPayload}-byte cap");
        if (length == 0) return (type, []);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return (type, payload);
    }
}

/// <summary>Client -> server frame types.</summary>
public static class VoiceUp
{
    public const byte Auth = 0x01, Join = 0x02, Leave = 0x03, Mic = 0x04, Audio = 0x05, Ping = 0x06, Speaker = 0x08;

    /// <summary>Auth payload byte 0. 2 = the stream is sealed after Auth; 1 (plaintext, before encryption) is refused.</summary>
    public const byte AuthVersion = 2;

    /// <summary>Auth payload: version u8 | role id i64 | 16-byte token | 16-byte proof of the ticket's secret
    /// (<see cref="Ro3Emu.ClientHooks.VoiceCipher.AuthProof"/>).</summary>
    public const int AuthSize = 1 + 8 + 16 + Ro3Emu.ClientHooks.VoiceCipher.AuthProofSize;
}

/// <summary>Server -> client frame types.</summary>
public static class VoiceDown
{
    public const byte AuthOk = 0x81, JoinResult = 0x82, MemberChange = 0x83, Audio = 0x84, MicState = 0x85,
        Vad = 0x86, Pong = 0x87, Kicked = 0x88;
}

/// <summary>JoinResult codes (VoiceDown.JoinResult payload byte 0).</summary>
public static class VoiceJoinCode
{
    public const byte Ok = 0, NotMember = 1, UnsupportedRoom = 2, Full = 3, NotAuthed = 4;
}

/// <summary>
/// Kicked codes (VoiceDown.Kicked payload byte 0). The in-game client (Ro3Emu.ClientHooks.VoiceClient) only logs the
/// code and leaves the channel, whatever it is; the split is for logs and bots.
/// </summary>
public static class VoiceKickCode
{
    public const byte ReplacedByNewAuth = 1, RemovedFromRoom = 2, IdleTimeout = 3, Flood = 4, OutboundOverflow = 5;
}
