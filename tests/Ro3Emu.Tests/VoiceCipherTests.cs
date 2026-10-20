using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

/// <summary>
/// The voice stream's frame encryption (src/Ro3Emu.ClientHooks/VoiceCipher.cs, the one copy the client, the relay and
/// the bot all use): AES-CTR + HMAC-SHA256, a per-direction counter that must be exactly the next one.
/// </summary>
public sealed class VoiceCipherTests
{
    private static byte[] Secret(byte fill = 7) => Enumerable.Repeat(fill, VoiceCipher.SecretSize).ToArray();

    private static readonly byte[] Speech = Enumerable.Range(0, 321).Select(i => (byte)i).ToArray();

    [Fact]
    public void AClientFrameOpensOnTheServer()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        byte[] sealedFrame = client.Seal(0x05, Speech);
        Assert.Equal(Speech.Length + VoiceCipher.Overhead, sealedFrame.Length);
        Assert.Equal(Speech, server.Open(0x05, sealedFrame));
    }

    [Fact]
    public void AServerFrameOpensOnTheClient()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        Assert.Equal(Speech, client.Open(0x84, server.Seal(0x84, Speech)));
    }

    [Fact]
    public void ManyFramesInOrderAllOpen()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        for (int i = 0; i < 200; i++)
            Assert.Equal(Speech, server.Open(0x05, client.Seal(0x05, Speech)));
    }

    [Fact]
    public void TheWireBytesDoNotShowThePayload()
    {
        using var client = VoiceCipher.ForClient(Secret());
        byte[] silence = new byte[320]; // all zero: the easiest pattern to spot in the clear
        byte[] sealedFrame = client.Seal(0x05, silence);
        byte[] body = sealedFrame.Skip(8).Take(silence.Length).ToArray();
        Assert.NotEqual(silence, body);
        Assert.True(body.Count(b => b == 0) < 20, "ciphertext of zeros is mostly zeros");
    }

    [Fact]
    public void TheSamePayloadTwiceGivesDifferentBytes()
    {
        using var client = VoiceCipher.ForClient(Secret());
        Assert.NotEqual(client.Seal(0x05, Speech), client.Seal(0x05, Speech));
    }

    [Fact]
    public void EveryChangedByteIsRejected()
    {
        using var client = VoiceCipher.ForClient(Secret());
        byte[] sealedFrame = client.Seal(0x05, Speech);
        for (int i = 0; i < sealedFrame.Length; i++)
        {
            using var server = VoiceCipher.ForServer(Secret());
            byte[] changed = (byte[])sealedFrame.Clone();
            changed[i] ^= 0x01;
            Assert.Null(server.Open(0x05, changed));
        }
    }

    [Fact]
    public void AChangedFrameTypeIsRejected()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        Assert.Null(server.Open(0x04, client.Seal(0x05, Speech)));
    }

    [Fact]
    public void AReplayedFrameIsRejected()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        byte[] first = client.Seal(0x05, Speech);
        Assert.NotNull(server.Open(0x05, first));
        Assert.Null(server.Open(0x05, first));
    }

    [Fact]
    public void ASkippedFrameIsRejected()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        client.Seal(0x05, Speech); // dropped on the way
        Assert.Null(server.Open(0x05, client.Seal(0x05, Speech)));
    }

    [Fact]
    public void AFrameSentBackToItsSenderIsRejected()
    {
        // A relay that reflects the client's own frames must not make them readable as server frames.
        using var client = VoiceCipher.ForClient(Secret());
        Assert.Null(client.Open(0x05, client.Seal(0x05, Speech)));
    }

    [Fact]
    public void TheAuthProofDependsOnTheSecretTheRoleAndTheToken()
    {
        byte[] token = Enumerable.Repeat((byte)3, 16).ToArray();
        byte[] proof = VoiceCipher.AuthProof(Secret(), 1001, token);
        Assert.Equal(VoiceCipher.AuthProofSize, proof.Length);
        Assert.Equal(proof, VoiceCipher.AuthProof(Secret(), 1001, token));
        Assert.NotEqual(proof, VoiceCipher.AuthProof(Secret(8), 1001, token));
        Assert.NotEqual(proof, VoiceCipher.AuthProof(Secret(), 1002, token));
        byte[] otherToken = (byte[])token.Clone();
        otherToken[0] ^= 1;
        Assert.NotEqual(proof, VoiceCipher.AuthProof(Secret(), 1001, otherToken));
    }

    [Fact]
    public void AnotherSessionsKeyIsRejected()
    {
        using var client = VoiceCipher.ForClient(Secret(7));
        using var server = VoiceCipher.ForServer(Secret(8));
        Assert.Null(server.Open(0x05, client.Seal(0x05, Speech)));
    }

    [Fact]
    public void AFrameTooShortForCounterAndTagIsRejected()
    {
        using var server = VoiceCipher.ForServer(Secret());
        Assert.Null(server.Open(0x05, new byte[VoiceCipher.Overhead - 1]));
        Assert.Null(server.Open(0x05, null));
    }

    [Fact]
    public void AnEmptyPayloadRoundTrips()
    {
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        byte[]? opened = server.Open(0x06, client.Seal(0x06, []));
        Assert.NotNull(opened);
        Assert.Empty(opened);
    }

    [Fact]
    public void ARejectedFrameDoesNotMoveTheCounter()
    {
        // A forged frame is dropped (and the relay closes the link); it must not shift what the next real frame needs.
        using var client = VoiceCipher.ForClient(Secret());
        using var server = VoiceCipher.ForServer(Secret());
        byte[] real = client.Seal(0x05, Speech);
        byte[] forged = (byte[])real.Clone();
        forged[^1] ^= 0x80;
        Assert.Null(server.Open(0x05, forged));
        Assert.Equal(Speech, server.Open(0x05, real));
    }

    [Fact]
    public void ASecretOfTheWrongSizeIsRefused()
    {
        Assert.Throws<ArgumentException>(() => VoiceCipher.ForClient(new byte[16]));
        Assert.Throws<ArgumentException>(() => VoiceCipher.ForServer(null!));
    }

    [Fact]
    public void NewSecretsAreRandomAndTheRightSize()
    {
        byte[] a = VoiceCipher.NewSecret(), b = VoiceCipher.NewSecret();
        Assert.Equal(VoiceCipher.SecretSize, a.Length);
        Assert.NotEqual(a, b);
    }
}
