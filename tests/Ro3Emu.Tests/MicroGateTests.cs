using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phonest.Proto;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

public sealed class MicroGateTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public void Rc4MatchesReferenceVector()
    {
        var data = Encoding.ASCII.GetBytes("Plaintext");
        new Rc4(Encoding.ASCII.GetBytes("Key")).Process(data);
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(data));
    }

    [Fact]
    public void ConvertorInteroperatesWithStockZlib()
    {
        var key = RandomNumberGenerator.GetBytes(20);
        var server = new StreamConvertor(key);
        var client = new ClientSide(key);

        for (int round = 0; round < 3; round++)
        {
            var up = Encoding.UTF8.GetBytes($"client frame {round} " + new string('x', 500 * round));
            Assert.Equal(up, server.ConvertInput(client.Pack(up)));
            var down = Encoding.UTF8.GetBytes($"server frame {round} " + new string('y', 700 * round));
            Assert.Equal(down, client.Unpack(server.ConvertOutput(down)));
        }
    }

    [Fact]
    public async Task HandshakeAndVerifySdkOverSocket()
    {
        int port = TestPorts.FreePort();
        var options = Options.Create(new ServerOptions { DataDir = _dataDir, MicroGatePort = port });
        using var accounts = new AccountStore(options);
        var crypto = GateCrypto.LoadOrCreate(_dataDir, NullLogger.Instance);
        var micro = new MicroSessions(NullLogger<MicroSessions>.Instance);
        var router = new MicroServiceRouter([new LoginService(accounts, new RoleStore(accounts), micro, NullLogger<LoginService>.Instance)], NullLogger<MicroServiceRouter>.Instance);
        using var gate = new MicroGateServer(options, crypto, router, micro, NullLogger<MicroGateServer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await gate.StartAsync(cts.Token);

        Assert.Equal(AccountStore.LoginResult.Registered, accounts.Login("tester", "pw"));
        string ticket = accounts.IssueSdkToken("tester");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var reader = new FrameReader(socket);

        // [1] requestEncryptToken -> [10] EncryptToken, both plaintext.
        await socket.SendAsync(Frame(1, new Phonest.Proto.Void()), SocketFlags.None);
        var (index, body) = await reader.NextAsync(null);
        Assert.Equal(10, index);
        long token = EncryptToken.Parser.ParseFrom(body).Token;

        // Client side of Utils.GenEncryptKeyFromToken, then [2] in plaintext.
        var sessionKey = RandomNumberGenerator.GetBytes(20); // the client's SHA-1 of 16 random bytes
        var encryptKey = new EncryptKey
        {
            HeadPadding = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(7)),
            Key = ByteString.CopyFrom(sessionKey),
            Token = token,
            TailPadding = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(17)),
        };
        using var rsa = RSA.Create();
        rsa.ImportFromPem(crypto.PublicKeyPem);
        var keyString = new EncryptKeyString { KeyString = ByteString.CopyFrom(rsa.Encrypt(encryptKey.ToByteArray(), RSAEncryptionPadding.OaepSHA1)) };
        await socket.SendAsync(Frame(2, keyString), SocketFlags.None);

        // From here on the server talks compressed + RC4.
        var client = new ClientSide(sessionKey);
        (index, _) = await reader.NextAsync(client);
        Assert.Equal(11, index);

        var request = new MsRequest
        {
            Rid = 7,
            Service = "LoginService",
            Method = "verifySdk",
            Params = ByteString.CopyFrom(MessagePackSerializer.Serialize(new Dictionary<string, object> { ["Ticket"] = ticket })),
        };
        await socket.SendAsync(client.Pack(Frame(33, request)), SocketFlags.None);
        (index, body) = await reader.NextAsync(client);
        Assert.Equal(34, index);
        var response = MsResponse.Parser.ParseFrom(body);
        Assert.Equal(7UL, response.Rid);
        var map = MessagePackSerializer.Deserialize<Dictionary<string, object>>(response.Params.Memory);
        Assert.Equal(true, ((object[])map["Ret"])[0]);
        var mixed = MessagePackSerializer.Deserialize<Dictionary<string, object>>((byte[])map["Token"]);
        Assert.Equal("tester", mixed["Username"]);

        cts.Cancel();
    }

    /// <summary>A crafted empty session key (RC4 divides by its length) is a failed key exchange, not a crash with a stack.</summary>
    [Fact]
    public async Task AnEmptySessionKeyFailsTheKeyExchangeWithoutAStack()
    {
        var (gate, log, crypto, port, cts) = StartLogged();
        using (gate)
        using (cts)
        {
            await gate.StartAsync(cts.Token);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            await SendKeyAsync(socket, new FrameReader(socket), crypto, []);
            await ClosedAsync(socket, cts.Token);
            Assert.All(log.Lines, line => Assert.Null(line.Error));
            Assert.Contains(log.Lines, line => line.Text.Contains("failed the key exchange", StringComparison.Ordinal));
            cts.Cancel();
        }
    }

    /// <summary>Arguments that are not msgpack are the sender's fault: one closing line, no Error with a stack per connection.</summary>
    [Fact]
    public async Task RequestArgumentsThatDoNotParseCloseWithoutAStack()
    {
        var (gate, log, crypto, port, cts) = StartLogged();
        using (gate)
        using (cts)
        {
            await gate.StartAsync(cts.Token);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var reader = new FrameReader(socket);
            var sessionKey = RandomNumberGenerator.GetBytes(20);
            await SendKeyAsync(socket, reader, crypto, sessionKey);
            var client = new ClientSide(sessionKey);
            Assert.Equal(11, (await reader.NextAsync(client)).Item1);
            var request = new MsRequest { Rid = 7, Service = "LoginService", Method = "verifySdk", Params = ByteString.CopyFrom(0xC1, 0xC1, 0xC1) };
            await socket.SendAsync(client.Pack(Frame(33, request)), SocketFlags.None);
            await ClosedAsync(socket, cts.Token);
            Assert.All(log.Lines, line => Assert.Null(line.Error));
            Assert.Contains(log.Lines, line => line.Text.Contains("do not parse", StringComparison.Ordinal));
            cts.Cancel();
        }
    }

    private (MicroGateServer Gate, RecordingLogger<MicroGateServer> Log, GateCrypto Crypto, int Port, CancellationTokenSource Cts) StartLogged()
    {
        int port = TestPorts.FreePort();
        var options = Options.Create(new ServerOptions { DataDir = _dataDir, MicroGatePort = port });
        var accounts = new AccountStore(options);
        _stores.Add(accounts);
        var crypto = GateCrypto.LoadOrCreate(_dataDir, NullLogger.Instance);
        var micro = new MicroSessions(NullLogger<MicroSessions>.Instance);
        var log = new RecordingLogger<MicroGateServer>();
        var router = new MicroServiceRouter([new LoginService(accounts, new RoleStore(accounts), micro, NullLogger<LoginService>.Instance)], NullLogger<MicroServiceRouter>.Instance);
        return (new MicroGateServer(options, crypto, router, micro, log), log, crypto, port, new CancellationTokenSource(TimeSpan.FromSeconds(30)));
    }

    private readonly List<AccountStore> _stores = [];

    private static async Task SendKeyAsync(Socket socket, FrameReader reader, GateCrypto crypto, byte[] sessionKey)
    {
        await socket.SendAsync(Frame(1, new Phonest.Proto.Void()), SocketFlags.None);
        var (_, body) = await reader.NextAsync(null);
        var encryptKey = new EncryptKey { Key = ByteString.CopyFrom(sessionKey), Token = EncryptToken.Parser.ParseFrom(body).Token };
        using var rsa = RSA.Create();
        rsa.ImportFromPem(crypto.PublicKeyPem);
        await socket.SendAsync(Frame(2, new EncryptKeyString { KeyString = ByteString.CopyFrom(rsa.Encrypt(encryptKey.ToByteArray(), RSAEncryptionPadding.OaepSHA1)) }), SocketFlags.None);
    }

    private static async Task ClosedAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try { while (await socket.ReceiveAsync(buffer, SocketFlags.None, ct) > 0) { } }
        catch (SocketException) { }
    }

    [Theory]
    [InlineData((ushort)999)] // an rpc index the gate does not know
    [InlineData((ushort)33)]  // a request before the encrypted session is up
    [InlineData((ushort)2)]   // a key exchange that is not one
    public async Task AFloodOfLargeGarbageFramesLeavesASmallLog(ushort index)
    {
        int port = TestPorts.FreePort();
        var options = Options.Create(new ServerOptions { DataDir = _dataDir, MicroGatePort = port });
        using var accounts = new AccountStore(options);
        var crypto = GateCrypto.LoadOrCreate(_dataDir, NullLogger.Instance);
        var micro = new MicroSessions(NullLogger<MicroSessions>.Instance);
        var log = new RecordingLogger<MicroGateServer>();
        var router = new MicroServiceRouter([new LoginService(accounts, new RoleStore(accounts), micro, NullLogger<LoginService>.Instance)], NullLogger<MicroServiceRouter>.Instance);
        using var gate = new MicroGateServer(options, crypto, router, micro, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await gate.StartAsync(cts.Token);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var garbage = new byte[1024 * 1024 - 2];
        Random.Shared.NextBytes(garbage);
        var frame = new byte[6 + garbage.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, garbage.Length + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), index);
        garbage.CopyTo(frame, 6);
        int sent = 0;
        try
        {
            for (; sent < 1000; sent++) await socket.SendAsync(frame, SocketFlags.None, cts.Token);
        }
        catch (SocketException) { } // the gate closed the connection: what this test wants
        var buffer = new byte[16];
        try { Assert.Equal(0, await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token)); }
        catch (SocketException) { }

        Assert.True(sent < 1000, "the gate kept reading garbage");
        Assert.All(log.Lines, line => Assert.Null(line.Error));
        Assert.True(log.Lines.Count <= 6, string.Join("\n", log.Lines.Select(l => l.Text)));
        Assert.True(log.Lines.Sum(l => l.Text.Length) < 2000, string.Join("\n", log.Lines.Select(l => l.Text)));
        cts.Cancel();
    }

    [Fact]
    public void LoggedArgumentsAreBounded()
    {
        var args = new Dictionary<object, object?>
        {
            ["Name"] = new string('x', 100_000),
            ["Ticket"] = "secret",
            ["List"] = Enumerable.Range(0, 1000).Select(i => (object?)i).ToArray(),
        };
        string described = MicroServiceRouter.Describe(args);
        Assert.True(described.Length < 600, described);
        Assert.Contains("(100000 chars)", described);
        Assert.Contains("<redacted>", described);
        Assert.Contains("...(+984)", described);
        Assert.DoesNotContain("secret", described);
    }

    /// <summary>Every line a component logs, with its level and exception.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Text, Exception? Error)> _lines = [];

        public IReadOnlyList<(LogLevel Level, string Text, Exception? Error)> Lines { get { lock (_lines) return [.. _lines]; } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static byte[] Frame(ushort index, IMessage message)
    {
        var body = message.ToByteArray();
        var frame = new byte[6 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), index);
        body.CopyTo(frame, 6);
        return frame;
    }

    /// <summary>Client half of the stream conversion built on System.IO.Compression, independent of the server's SharpZipLib.</summary>
    private sealed class ClientSide
    {
        private readonly Rc4 _enc;
        private readonly Rc4 _dec;
        private readonly MemoryStream _deflated = new();
        private readonly ZLibStream _deflate;
        private readonly MemoryStream _inflateInput = new();
        private long _inflateRead;

        public ClientSide(byte[] key)
        {
            _enc = new Rc4(key);
            _dec = new Rc4(key);
            _deflate = new ZLibStream(_deflated, CompressionLevel.Optimal, leaveOpen: true);
        }

        public byte[] Pack(byte[] plain)
        {
            _deflated.SetLength(0);
            _deflate.Write(plain);
            _deflate.Flush(); // sync flush
            var wire = _deflated.ToArray();
            _enc.Process(wire);
            return wire;
        }

        public byte[] Unpack(byte[] wire)
        {
            var copy = wire.ToArray();
            _dec.Process(copy);
            _inflateInput.Write(copy);
            // Re-inflate the whole stream so far and return only the new tail (fine for tests).
            using var inflate = new ZLibStream(new MemoryStream(_inflateInput.ToArray()), CompressionMode.Decompress);
            using var all = new MemoryStream();
            var buf = new byte[4096];
            try
            {
                int n;
                while ((n = inflate.Read(buf)) > 0) all.Write(buf, 0, n);
            }
            catch (InvalidDataException) { }
            var result = all.ToArray()[(int)_inflateRead..];
            _inflateRead = all.Length;
            return result;
        }
    }

    private sealed class FrameReader(Socket socket)
    {
        private readonly List<byte> _plain = [];

        public async Task<(ushort Index, byte[] Body)> NextAsync(ClientSide? convertor)
        {
            var buf = new byte[65536];
            while (true)
            {
                if (_plain.Count >= 4)
                {
                    int size = BinaryPrimitives.ReadInt32LittleEndian(_plain.ToArray());
                    if (_plain.Count >= 4 + size)
                    {
                        var frame = _plain.GetRange(0, 4 + size).ToArray();
                        _plain.RemoveRange(0, 4 + size);
                        return (BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)), frame[6..]);
                    }
                }
                int n = await socket.ReceiveAsync(buf, SocketFlags.None);
                if (n == 0) throw new EndOfStreamException();
                var chunk = buf[..n];
                _plain.AddRange(convertor != null ? convertor.Unpack(chunk) : chunk);
            }
        }
    }
}
