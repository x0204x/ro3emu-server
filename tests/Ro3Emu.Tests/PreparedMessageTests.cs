using System.Buffers.Binary;
using Google.Protobuf;
using Phonest.Proto;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class PreparedMessageTests
{
    [Fact]
    public void EnvelopeIsByteForByteWhatAPerViewerSendBuilds()
    {
        var movement = new MovementChangeNtf
        {
            Uid = 100004, TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo { Pos = new Vec3 { X = 1.5f, Y = 2, Z = 3 }, Face = 90, ServerTime = 1791500000000 },
        };
        var prepared = new PreparedMessage(31081, movement);

        // GameSession.SendGameBytesAsync: uint32 LE message id + body inside EntityMessage.Parameters.
        var body = movement.ToByteArray();
        var parameters = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(parameters, 31081u);
        body.CopyTo(parameters, 4);
        var expected = new EntityMessage { Parameters = ByteString.CopyFrom(parameters) }.ToByteArray();

        Assert.Equal(expected, prepared.Envelope);
        Assert.Equal(body, prepared.Body);
        Assert.Equal(MovementChangeNtf.Descriptor, prepared.Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1234)]
    public void CommonErrorUsesTheClientsSingleRecordBatchEnvelope(int errorCode)
    {
        var message = new CommonErrorNtf { ErrorCode = errorCode };
        var body = message.ToByteArray();
        var prepared = new PreparedMessage(MsgId.CommonError, message);
        var parameters = EntityMessage.Parser.ParseFrom(prepared.Envelope).Parameters.Span;

        Assert.Equal(12 + body.Length, parameters.Length);
        Assert.Equal(MsgId.CommonError, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(body.Length, BinaryPrimitives.ReadInt32BigEndian(parameters[4..]));
        Assert.Equal(MsgId.CommonError, BinaryPrimitives.ReadUInt32BigEndian(parameters[8..]));
        Assert.Equal(body, parameters[12..].ToArray());
    }
}
