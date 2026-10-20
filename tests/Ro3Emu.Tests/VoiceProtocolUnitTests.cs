using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>Pure unit tests for the voice relay's protocol pieces: no server, no sockets.</summary>
public sealed class VoiceProtocolUnitTests
{
    /// <summary>A stream that hands back at most <see cref="_chunk"/> bytes per read, to exercise
    /// ReadExactlyAsync's handling of a frame split across several TCP reads.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield(); // force a real async hop, the way a socket read would
            int n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
            if (n <= 0) return 0;
            data.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return n;
        }
    }

    [Fact]
    public void WriteThenReadRoundTripsTypeAndPayload()
    {
        byte[] frame = VoiceFrame.Write(VoiceUp.Audio, [1, 2, 3, 4]);
        Assert.Equal((byte)'V', frame[0]);
        Assert.Equal((byte)'T', frame[1]);
        Assert.Equal(VoiceUp.Audio, frame[2]);
        Assert.Equal(4, BitConverter.ToUInt16(frame, 3));
    }

    [Fact]
    public async Task ReadAsyncRejectsBadMagic()
    {
        byte[] bytes = VoiceFrame.Write(VoiceUp.Ping, []);
        bytes[0] = (byte)'X';
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<VoiceProtocolException>(() => VoiceFrame.ReadAsync(stream, 600, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsyncRejectsAPayloadPastTheCap()
    {
        byte[] bytes = VoiceFrame.Write(VoiceUp.Audio, new byte[100]);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<VoiceProtocolException>(() => VoiceFrame.ReadAsync(stream, 50, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsyncReassemblesAFrameSplitAcrossSeveralReads()
    {
        byte[] payload = Enumerable.Range(0, 37).Select(i => (byte)i).ToArray();
        byte[] bytes = VoiceFrame.Write(VoiceUp.Audio, payload);
        using var stream = new ChunkedStream(bytes, chunk: 3);
        var (type, read) = await VoiceFrame.ReadAsync(stream, 600, CancellationToken.None);
        Assert.Equal(VoiceUp.Audio, type);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadAsyncThrowsOnATruncatedStream()
    {
        byte[] bytes = VoiceFrame.Write(VoiceUp.Audio, new byte[10]);
        using var stream = new MemoryStream(bytes[..^3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => VoiceFrame.ReadAsync(stream, 600, CancellationToken.None));
    }

    [Fact]
    public void RateLimiterAllowsBurstThenThrottles()
    {
        var limiter = new VoiceRateLimiter(framesPerSecond: 10, burst: 5);
        int allowed = 0;
        for (int i = 0; i < 30; i++) if (limiter.TryTake()) allowed++;
        Assert.Equal(15, allowed); // framesPerSecond + burst, all taken instantly (negligible time passed)
        Assert.False(limiter.TryTake());
    }

    [Fact]
    public async Task RateLimiterRefillsOverTime()
    {
        // Real elapsed time by design (Environment.TickCount64, not an injected/fake clock): a short real wait here
        // is simpler and more honest than faking it.
        var limiter = new VoiceRateLimiter(framesPerSecond: 10, burst: 0);
        for (int i = 0; i < 10; i++) Assert.True(limiter.TryTake());
        Assert.False(limiter.TryTake());
        await Task.Delay(1100);
        Assert.True(limiter.TryTake());
    }

    [Fact]
    public void RateLimiterFloodDetectionNeedsTheWholeWindowOverBudget()
    {
        var limiter = new VoiceRateLimiter(framesPerSecond: 10, burst: 0);
        // A short burst alone (under the 5 s window's budget) does not flag flood.
        for (int i = 0; i < 20; i++) Assert.False(limiter.RecordAndCheckFlood());
        // Sustained way past 3x the rate for the rest of the window does.
        bool flagged = false;
        for (int i = 0; i < 200 && !flagged; i++) flagged = limiter.RecordAndCheckFlood();
        Assert.True(flagged);
    }

    [Fact]
    public void TicketRegistryIssuesConsumesOnceAndReplacesOnReissue()
    {
        var clock = new FakeClock();
        var registry = new VoiceTicketRegistry(clock);
        var first = registry.Issue("alice", 1001, ttlSeconds: 60);
        Assert.Equal("alice", registry.Consume(1001, first.Token, Proof(first, 1001))?.Account);
        Assert.Null(registry.Consume(1001, first.Token, Proof(first, 1001))); // single use

        var second = registry.Issue("alice", 1001, ttlSeconds: 60);
        var third = registry.Issue("alice", 1001, ttlSeconds: 60); // replaces "second" before it was ever used
        Assert.Null(registry.Consume(1001, second.Token, Proof(second, 1001)));
        Assert.Equal("alice", registry.Consume(1001, third.Token, Proof(third, 1001))?.Account);
    }

    [Fact]
    public void TicketRegistryHandsTheIssuedSecretBackOnlyToItsOwnTicket()
    {
        var registry = new VoiceTicketRegistry(new FakeClock());
        var a = registry.Issue("alice", 1001, ttlSeconds: 60);
        var b = registry.Issue("bob", 1002, ttlSeconds: 60);
        Assert.Equal(Ro3Emu.ClientHooks.VoiceCipher.SecretSize, a.Secret.Length);
        Assert.NotEqual(a.Secret, b.Secret);
        Assert.Equal(a.Secret, registry.Consume(1001, a.Token, Proof(a, 1001))?.Secret);
        Assert.Equal(b.Secret, registry.Consume(1002, b.Token, Proof(b, 1002))?.Secret);
    }

    [Fact]
    public void TicketRegistryRefusesTheRightTokenWithoutTheSecretAndKeepsTheTicket()
    {
        // Someone who saw the Auth frame on the wire has the token but not the secret: they must not use the ticket
        // up (or replace the player's live voice connection); the player's own Auth still works afterwards.
        var registry = new VoiceTicketRegistry(new FakeClock());
        var ticket = registry.Issue("alice", 1001, ttlSeconds: 60);
        byte[] guessed = Ro3Emu.ClientHooks.VoiceCipher.AuthProof(Ro3Emu.ClientHooks.VoiceCipher.NewSecret(), 1001, ticket.Token);
        Assert.Null(registry.Consume(1001, ticket.Token, guessed));
        Assert.Null(registry.Consume(1001, ticket.Token, new byte[Ro3Emu.ClientHooks.VoiceCipher.AuthProofSize]));
        Assert.Equal("alice", registry.Consume(1001, ticket.Token, Proof(ticket, 1001))?.Account);
    }

    private static byte[] Proof(VoiceTicketGrant grant, long roleId) =>
        Ro3Emu.ClientHooks.VoiceCipher.AuthProof(grant.Secret, roleId, grant.Token);

    [Fact]
    public void TicketRegistryRejectsWrongTokenButKeepsTheTicketValid()
    {
        var clock = new FakeClock();
        var registry = new VoiceTicketRegistry(clock);
        var ticket = registry.Issue("alice", 1001, ttlSeconds: 60);
        byte[] wrong = ticket.Token.ToArray();
        wrong[0] ^= 0xFF;
        Assert.Null(registry.Consume(1001, wrong, Ro3Emu.ClientHooks.VoiceCipher.AuthProof(ticket.Secret, 1001, wrong)));
        Assert.Equal("alice", registry.Consume(1001, ticket.Token, Proof(ticket, 1001))?.Account);
    }

    [Fact]
    public void TicketRegistryExpiresAfterTtl()
    {
        var clock = new FakeClock();
        var registry = new VoiceTicketRegistry(clock);
        var ticket = registry.Issue("alice", 1001, ttlSeconds: 60);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(registry.Consume(1001, ticket.Token, Proof(ticket, 1001)));
    }
}
