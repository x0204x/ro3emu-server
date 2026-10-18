using Google.Protobuf;
using Microsoft.Extensions.Options;
using Phonest.Proto;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// The micro-service gate ("ms gate", server list gate_address). Rpc indexes follow the client's
/// IClientGateMicroServiceStub / IGateClientMicroService (Phonest.Common.Micro.Internal).
/// </summary>
public sealed class MicroGateServer(IOptions<ServerOptions> options, GateCrypto crypto, MicroServiceRouter router, MicroSessions sessions, ILogger<MicroGateServer> log, Diagnostics.ServerMetrics? metrics = null, Game.GameLoop? loop = null)
    : GateListener(options.Value.ListenAddress(), options.Value.MicroGatePort, log, metrics)
{
    // client -> gate
    private const ushort RequestEncryptToken = 1;
    private const ushort ConfirmEncryptKey = 2;
    private const ushort ClientHeartbeat = 6;
    private const ushort ReplyHeartbeat = 16;
    private const ushort MicroRequest = 33;

    // gate -> client
    private const ushort ResponseEncryptToken = 10;
    private const ushort ConfirmEncryptKeyAck = 11;
    private const ushort MicroResponse = 34;

    protected override string Name => "MicroGate";

    protected override bool IsSignedIn(PhonestChannel channel) => (channel.Session as MicroSession)?.Account != null;

    protected override ValueTask OnDisconnectedAsync(PhonestChannel channel)
    {
        if (channel.Session is MicroSession session) sessions.Remove(session);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body)
    {
        if (loop != null && loop.Phase != Game.GameLoopPhase.Accepting) throw new OperationCanceledException("Micro gate is quiescing.");
        var session = channel.Session as MicroSession ?? (MicroSession)(channel.Session = new MicroSession(channel));
        switch (index)
        {
            case RequestEncryptToken:
                session.HandshakeToken = GateCrypto.NewHandshakeToken();
                await channel.SendAsync(ResponseEncryptToken, new EncryptToken { Token = session.HandshakeToken });
                break;

            case ConfirmEncryptKey:
                var sessionKey = crypto.OpenSessionKey(EncryptKeyString.Parser.ParseFrom(body.Span), session.HandshakeToken);
                channel.EnableConvertor(sessionKey);
                await channel.SendAsync(ConfirmEncryptKeyAck, new Phonest.Proto.Void());
                Log.LogInformation("MicroGate: {Remote} encrypted session up", channel.Remote);
                break;

            case ClientHeartbeat:
            case ReplyHeartbeat:
                break;

            case MicroRequest:
                var request = MsRequest.Parser.ParseFrom(body.Span);
                var result = await router.InvokeAsync(session, request.Service, request.Method, request.Params.Memory);
                if (request.Rid != 0)
                {
                    await channel.SendAsync(MicroResponse, new MsResponse { Rid = request.Rid, Params = ByteString.CopyFrom(result) });
                }
                break;

            default:
                Log.LogWarning("MicroGate: {Remote} sent unknown rpc index {Index} ({Length} bytes)", channel.Remote, index, body.Length);
                break;
        }
    }
}

/// <summary>Per-connection state on the micro gate.</summary>
public sealed class MicroSession(PhonestChannel channel)
{
    public PhonestChannel Channel { get; } = channel;

    private static long s_nextId;

    /// <summary>Process-unique id: an unverified session's own reservation key.</summary>
    public long Id { get; } = Interlocked.Increment(ref s_nextId);

    public long HandshakeToken { get; set; }

    /// <summary>Account bound by LoginService.verifySdk.</summary>
    /// <summary>The verified account; set only by <see cref="MicroSessions.SignIn"/> (tests may set it).</summary>
    public string? Account { get; internal set; }
}
