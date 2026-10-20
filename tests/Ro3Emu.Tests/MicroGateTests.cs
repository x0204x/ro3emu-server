using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using MessagePack;
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
        private readonly List<byte> _plain = new();

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
