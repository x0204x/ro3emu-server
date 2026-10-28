using Google.Protobuf;
using Phonest.Proto;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A game message serialized once for many recipients: the body and its EntityMessage envelope are immutable bytes
/// shared by every send. Compression and RC4 stay per connection (stateful streams); only the frame copy is per viewer.
/// The source message must not be changed after it is prepared.
/// </summary>
public sealed class PreparedMessage
{
    public PreparedMessage(uint msgId, IMessage message)
    {
        MsgId = msgId;
        Type = message.Descriptor;
        Body = message.ToByteArray();
        var parameters = GameMessageWire.Parameters(msgId, Body);
        Envelope = new EntityMessage { Parameters = ByteString.CopyFrom(parameters) }.ToByteArray();
        Frame = Net.PhonestChannel.Frame(GameGateServer.EntityMessageOut, Envelope);
    }

    public uint MsgId { get; }
    public Google.Protobuf.Reflection.MessageDescriptor Type { get; }
    public byte[] Body { get; }
    /// <summary>The serialized EntityMessage frame body (Phonest index <see cref="GameGateServer.EntityMessageOut"/>).</summary>
    public byte[] Envelope { get; }
    /// <summary>The whole wire frame, shared by every recipient (stage G: one copy per viewer was 40% of a walk).</summary>
    public byte[] Frame { get; }
}
