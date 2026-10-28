using System.Buffers.Binary;

namespace Ro3Emu.Server.Game;

/// <summary>Builds the game-message bytes carried by <c>EntityMessage.Parameters</c>.</summary>
internal static class GameMessageWire
{
    /// <summary>
    /// The client reserves outer message id 2 for a batch.  A <c>COMMON_ERROR_NTF</c> therefore has to be one
    /// big-endian batch record whose inner message id is also 2; every other message keeps the ordinary
    /// little-endian id followed by its payload.
    /// </summary>
    internal static byte[] Parameters(uint msgId, ReadOnlySpan<byte> payload)
    {
        if (msgId != MsgId.CommonError)
        {
            var direct = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(direct, msgId);
            payload.CopyTo(direct.AsSpan(4));
            return direct;
        }

        var batch = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(batch, MsgId.CommonError);
        BinaryPrimitives.WriteInt32BigEndian(batch.AsSpan(4), payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(batch.AsSpan(8), MsgId.CommonError);
        payload.CopyTo(batch.AsSpan(12));
        return batch;
    }
}
