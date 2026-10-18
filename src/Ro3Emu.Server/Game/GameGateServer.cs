using MessagePack;
using Microsoft.Extensions.Options;
using Phonest.Proto;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The game gate (server list game_gate). Rpc indexes follow Phonest.Proto IClientGateServiceStub (client -> gate)
/// and IGateClientService (gate -> client).
/// </summary>
public sealed class GameGateServer(
    IOptions<ServerOptions> options, GateCrypto crypto, AccountStore accounts, GameMessageHandler messages, RoleSessionService roleSessions, TrafficLog traffic,
    SessionRegistry sessions, GameLoop loop, ReconnectService reconnects, ILogger<GameGateServer> log, Diagnostics.ServerMetrics metrics)
    : GateListener(options.Value.ListenAddress(), options.Value.GameGatePort, log, metrics)
{
    // client -> gate
    private const ushort RequestEncryptToken = 1;
    private const ushort ConfirmEncryptKey = 2;
    private const ushort ConnectServer = 3;
    private const ushort EntityMessageIn = 4;
    private const ushort ChannelMessageIn = 5;

    // gate -> client
    public const ushort ConnectResponse = 1;
    public const ushort EntityMessageOut = 2;
    public const ushort CreateChannelEntity = 4;
    public const ushort DisconnectClient = 5;
    private const ushort ResponseEncryptToken = 10;
    private const ushort ConfirmEncryptKeyAck = 11;

    protected override string Name => "GameGate";

    protected override GameLoop.CompletionReservation ReserveServeCompletion()
        => loop.ReserveCompletion("game connection")
           ?? throw new InvalidOperationException("Game loop has no connection cleanup capacity.");

    protected override void OnCleanupFailed(PhonestChannel channel, Exception error)
        => loop.ReportCleanupFailure($"game disconnect {channel.Remote}: {error.GetType().Name}");

    protected override async ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body)
    {
        if (loop.Phase != GameLoopPhase.Accepting) throw new OperationCanceledException("Game gate is quiescing.");
        var session = channel.Session as GameSession ?? (GameSession)(channel.Session = new GameSession(channel, traffic));
        // A silent reconnect moved the session to a newer connection: this (half-open) one is ignored.
        if (!ReferenceEquals(session.Channel, channel)) return;
        switch (index)
        {
            case RequestEncryptToken:
                session.HandshakeToken = GateCrypto.NewHandshakeToken();
                await channel.SendAsync(ResponseEncryptToken, new EncryptToken { Token = session.HandshakeToken });
                break;

            case ConfirmEncryptKey:
                channel.EnableConvertor(crypto.OpenSessionKey(EncryptKeyString.Parser.ParseFrom(body.Span), session.HandshakeToken));
                await channel.SendAsync(ConfirmEncryptKeyAck, new Phonest.Proto.Void());
                Log.LogInformation("GameGate: {Remote} encrypted session up", channel.Remote);
                break;

            // Game state lives on the game loop; the reader waits, so one connection's messages stay in order.
            case ConnectServer:
                var connect = ConnectServerReq.Parser.ParseFrom(body.Span);
                await loop.InvokeAsync(() => OnConnectServerAsync(session, connect));
                break;

            case EntityMessageIn:
                long receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var entityMessage = EntityMessage.Parser.ParseFrom(body.Span);
                await loop.InvokeAsync(() => OnEntityMessageAsync(session, entityMessage, receivedAt));
                break;

            case ChannelMessageIn:
                break;

            default:
                Log.LogWarning("GameGate: {Remote} sent unknown rpc index {Index} ({Length} bytes)", channel.Remote, index, body.Length);
                break;
        }
    }

    /// <summary>
    /// Account stage (spec §4): Connected, then the ClientAccount entity, then OnQueueFinishedNotify — that RPC (not the
    /// connect result) is what makes Lua enter the role-select scene.
    /// </summary>
    private async Task OnConnectServerAsync(GameSession session, ConnectServerReq request)
    {
        var type = request.Type;
        // connectServer only after the encryption handshake.
        if (!session.Channel.Encrypted)
        {
            Log.LogWarning("GameGate: {Remote} connectServer {Type} before the encryption handshake", session.Channel.Remote, type);
            await session.Channel.SendAsync(ConnectResponse, new ConnectServerResp
            {
                Type = type == ConnectServerReq.Types.ReqType.ReConnection
                    ? ConnectServerResp.Types.RespType.ReconnectRefused : ConnectServerResp.Types.RespType.ConnectRefused,
            });
            return;
        }
        if (type == ConnectServerReq.Types.ReqType.ReConnection)
        {
            await reconnects.ReconnectAsync(session, request);
            return;
        }
        string? account = null;
        if (type is ConnectServerReq.Types.ReqType.NewConnection or ConnectServerReq.Types.ReqType.SwitchToAccount && request.HasToken)
        {
            // GameClient.Token = the msgpack MixedLoginToken from LoginService.verifySdk.
            var token = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(request.Token.Memory, Micro.MicroServiceRouter.Untrusted);
            account = accounts.ResolveEngineSign(token.GetValueOrDefault("Sign") as string);
        }
        Log.LogInformation("GameGate: {Remote} connectServer {Type} account={Account}", session.Channel.Remote, type, account ?? "?");
        if (account == null || (type == ConnectServerReq.Types.ReqType.SwitchToAccount && account != session.Account))
        {
            await session.Channel.SendAsync(ConnectResponse, new ConnectServerResp { Type = ConnectServerResp.Types.RespType.ConnectRefused });
            return;
        }
        // Another account on this connection is refused while a role of the first one is in the world.
        if (!await sessions.SignInAsync(session, account, register: type == ConnectServerReq.Types.ReqType.NewConnection))
        {
            await session.Channel.SendAsync(ConnectResponse, new ConnectServerResp { Type = ConnectServerResp.Types.RespType.ConnectRefused });
            return;
        }
        if (!sessions.IsWinner(session) || session.Channel.PeerClosed) return;
        // SwitchToAccount (back to the role screen) answers with its own response type; the rest is the same.
        var resp = type == ConnectServerReq.Types.ReqType.SwitchToAccount
            ? ConnectServerResp.Types.RespType.SwitchToAccount
            : ConnectServerResp.Types.RespType.Connected;
        await session.Channel.SendAsync(ConnectResponse, new ConnectServerResp { Type = resp });
        await session.CreateChannelEntityAsync("ClientAccount", $"acc-{account}-{++session.AccountEntitySerial}", channelId: 1);
        await session.SendRpcAsync("OnQueueFinishedNotify");
    }

    /// <summary>
    /// Client -> server entity messages: in the account stage game messages come as RPC "EZAdaptorCS"(uint msgId, bin body);
    /// with an avatar they are raw (no methodName, parameters = uint32 LE msgId ‖ body). Other named RPCs are Phonest
    /// housekeeping (OnClientReadyInternal, ...).
    /// </summary>
    private async Task OnEntityMessageAsync(GameSession session, EntityMessage message, long receivedAt)
    {
        if (!message.HasMethodName)
        {
            if (message.Parameters.Length < 4) return;
            uint msgId = BitConverter.ToUInt32(message.Parameters.Span);
            await Dispatch(session, msgId, message.Parameters.Memory[4..], receivedAt);
            return;
        }

        string method = message.MethodName.ToStringUtf8();
        if (method == "EZAdaptorCS")
        {
            var reader = new MessagePackReader(message.Parameters.Memory);
            uint msgId = reader.ReadUInt32();
            var bytes = reader.ReadBytes();
            byte[] body = bytes.HasValue ? System.Buffers.BuffersExtensions.ToArray(bytes.Value) : [];
            await Dispatch(session, msgId, body, receivedAt);
            return;
        }

        object? args = null;
        try { args = MessagePackSerializer.Deserialize<object>(message.Parameters.Memory, Micro.MicroServiceRouter.Untrusted); } catch (MessagePackSerializationException) { }
        string described = MicroServiceRouter.Describe(args);
        traffic.Rpc(session.Account, "c2s-rpc", method, () => described);
        Log.LogInformation("GameGate: {Account} rpc {Method} {Args}", session.Account, method, described);
        if (method == "SwitchToAccount") await roleSessions.SwitchToAccountAsync(session);
        else if (method == "OnClientReadyInternal") await ClientReadyAsync(session, message.Parameters.Memory);
    }

    /// <summary>OnClientReadyInternal(bool status, bool relogin, bool reconnect): msgpack values without an array header.</summary>
    private Task ClientReadyAsync(GameSession session, ReadOnlyMemory<byte> parameters)
    {
        var flags = new bool[3];
        try
        {
            var reader = new MessagePackReader(parameters);
            for (int i = 0; i < flags.Length && !reader.End; i++) flags[i] = reader.ReadBoolean();
        }
        catch (Exception e) when (e is MessagePackSerializationException or EndOfStreamException) { return Task.CompletedTask; }
        return reconnects.ClientReadyAsync(session, flags[0], flags[1], flags[2]);
    }

    protected override bool IsSignedIn(PhonestChannel channel) => (channel.Session as GameSession)?.Account != null;

    protected override async ValueTask OnDisconnectedAsync(PhonestChannel channel)
    {
        if (channel.Session is GameSession session)
        {
            await loop.InvokeCleanupAsync(CurrentServeCompletion
                ?? throw new InvalidOperationException("Game connection has no cleanup reservation."), async () =>
            {
                // Swapped out by a reconnect (the half-open old socket): the session lives on the newer one.
                if (!ReferenceEquals(session.Channel, channel)) return;
                if (reconnects.TryEnterGrace(session)) return;
                try { await roleSessions.OnDisconnectedAsync(session); }
                finally { sessions.Unregister(session); }
            });
        }
    }

    private async Task Dispatch(GameSession session, uint msgId, ReadOnlyMemory<byte> body, long receivedAt)
    {
        using var measurement = Metrics!.BeginRequest(session.Channel.Metrics, msgId, receivedAt);
        traffic.ClientToServer(session.Account, msgId, body);
        await messages.HandleAsync(session, msgId, body);
        measurement.Complete();
    }
}
