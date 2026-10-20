using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phonest.Proto;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Bot;

/// <summary>A protocol response explicitly refused a bot operation.</summary>
public sealed class BotRefusedException(string operation, string result, string message) : InvalidOperationException(message)
{
    public string Operation { get; } = operation;
    public string Result { get; } = result;
}

/// <summary>
/// A scripted RO3 client speaking the same protocol as the real one (HTTP SDK login, server list, micro gate,
/// game gate). Used by end-to-end tests and to stand in for a second player (the real client allows one
/// instance per machine).
/// </summary>
public sealed class BotClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _gatePublicKeyPem;
    private readonly ILogger _log;
    private readonly BotInbox _inbox = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<ulong, TaskCompletionSource<byte[]>> _pendingMicro = new();
    private PhonestChannel? _micro;
    private PhonestChannel? _game;
    /// <summary>Connections the bot dropped or gave up (a refused reconnect, a half-open old socket): their frames
    /// and their end no longer fail the inbox; only a server kick on them is recorded (Disconnect "retired").</summary>
    private readonly HashSet<PhonestChannel> _retired = new(ReferenceEqualityComparer.Instance);
    private ulong _nextRid;
    private bool _avatarBound;
    private ServerMetrics? _loadMetrics;
    private bool _loadMode;
    private TimeSpan _loadSendTimeout = TimeSpan.FromSeconds(3);

    public BotClient(string httpBaseUrl, string gatePublicKeyPem, ILogger? log = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(httpBaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        _gatePublicKeyPem = gatePublicKeyPem;
        _log = log ?? NullLogger.Instance;
    }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public string? Account { get; private set; }

    public List<RoleBaseInfo> Roles { get; } = new();

    /// <summary>All game messages and RPCs the server sent, in order.</summary>
    public BotInbox Inbox => _inbox;

    /// <summary>Optional stage boundaries for load telemetry; each started stage finishes once.</summary>
    public Action<string>? OnStageStarted { get; set; }
    public Action<string, double, Exception?>? OnStageFinished { get; set; }

    /// <summary>Raw socket bytes received on this bot's micro and game connections.</summary>
    public long WireInboundBytes => (_micro?.Metrics?.BytesReceived ?? 0) + (_game?.Metrics?.BytesReceived ?? 0);

    /// <summary>Optional harness hook at the point a game message is offered to the client transport.</summary>
    public Action<uint>? OnGameMessageOffered { get; set; }

    /// <summary>Observe every decoded inbound event while retaining only a bounded recent inbox.</summary>
    public void EnableLoadMode(int inboxCapacity, Action<BotEvent> observer, ServerMetrics? sharedMetrics = null,
        TimeSpan? sendTimeout = null)
    {
        _inbox.ConfigureBounded(inboxCapacity, observer);
        _loadMode = true;
        _loadMetrics = sharedMetrics;
        if (sendTimeout is { } timeout) _loadSendTimeout = timeout;
    }

    private async Task SendWithDrainAsync(PhonestChannel channel, ushort index, IMessage message)
    {
        await channel.SendAsync(index, message);
        if (channel.Metrics != null) await channel.Metrics.WaitForDrainAsync(_loadSendTimeout, _cts.Token);
    }

    private async Task<T> StageAsync<T>(string name, Func<Task<T>> work)
    {
        var clock = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            OnStageStarted?.Invoke(name);
            return await work();
        }
        catch (Exception e) { error = e; throw; }
        finally { OnStageFinished?.Invoke(name, clock.Elapsed.TotalMilliseconds, error); }
    }

    private async Task StageAsync(string name, Func<Task> work) =>
        await StageAsync<object?>(name, async () => { await work(); return null; });

    private Exception Refused(string operation, object result, string message) => _loadMode
        ? new BotRefusedException(operation, result.ToString() ?? "unknown", message)
        : new InvalidOperationException(message);

    // ---------------------------------------------------------------- login

    /// <summary>SDK login, server list, micro gate handshake, verifySdk and getRoleInfo.</summary>
    public async Task LoginAsync(string identity, string credential)
    {
        string ticket = await StageAsync("sdk-login", async () =>
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = identity, ["credential"] = credential, ["channel_id"] = "15" });
            var login = await (await _http.PostAsync("/sdk/login", form, _cts.Token)).Content.ReadFromJsonAsync<JsonElement>(_cts.Token);
            long code = login.GetProperty("code").GetInt64();
            if (code != 0) throw Refused("sdk-login", code, $"sdk login refused: {login}");
            return login.GetProperty("data").GetProperty("token").GetString()!;
        });

        await StageAsync("server-list", async () =>
        {
            var listText = await _http.GetStringAsync("/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true", _cts.Token);
            var list = JsonDocument.Parse(listText["RO3_RECOVERY_JSON:".Length..]).RootElement.GetProperty("data");
            MicroGate = list.GetProperty("gate_address")[0].GetString()!;
            GameGate = list.GetProperty("clusters")[0].GetProperty("game_gate")[0].GetString()!;
        });

        _micro = await StageAsync("micro-handshake", () => ConnectAsync(MicroGate, OnMicroFrame, requestToken: 1, confirmKey: 2));
        await StageAsync("micro-verify", async () =>
        {
            var verify = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(
                await CallMicroAsync("LoginService", "verifySdk", new Dictionary<string, object> { ["Ticket"] = ticket }));
            EngineToken = (byte[])verify["Token"]!;
            var mixed = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(EngineToken);
            Account = (string)mixed["Username"]!;
            EngineSign = (string)mixed["Sign"]!;
        });

        await StageAsync("micro-role-list", async () =>
        {
            var roleInfo = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(
                await CallMicroAsync("LoginService", "getRoleInfo", new object[] { "", "" }));
            Roles.Clear();
            foreach (var entry in (object[])roleInfo["RoleInfos"]!)
            {
                var map = (Dictionary<object, object?>)entry;
                Roles.Add(RoleBaseInfo.Parser.ParseFrom((byte[])map["baseInfo"]!));
            }
        });
        _log.LogInformation("bot {Account}: logged in, {Count} role(s)", Account, Roles.Count);
    }

    public string MicroGate { get; private set; } = "";

    public string GameGate { get; private set; } = "";

    public byte[] EngineToken { get; private set; } = [];

    public string EngineSign { get; private set; } = "";

    // ---------------------------------------------------------------- game gate

    /// <summary>Game gate handshake + ConnectServerReq, then waits for the role screen trigger (OnQueueFinishedNotify).</summary>
    public async Task EnterGateAsync()
    {
        _game = await StageAsync("game-handshake", () => ConnectAsync(GameGate, OnGameFrame, requestToken: 1, confirmKey: 2));
        await StageAsync("game-connect", async () =>
        {
            using var connectExpected = _inbox.Expect(e => e.Kind == BotEventKind.ConnectResponse);
            using var accountExpected = _inbox.Expect(e => e.Kind == BotEventKind.Entity && e.Name == "ClientAccount");
            using var queueExpected = _inbox.Expect(e => e.Kind == BotEventKind.Rpc && e.Name == "OnQueueFinishedNotify");
            await SendWithDrainAsync(_game, 3, new ConnectServerReq { Type = ConnectServerReq.Types.ReqType.NewConnection, Token = ByteString.CopyFrom(EngineToken) });
            var connected = await connectExpected.WaitAsync(Timeout, _cts.Token);
            if (connected.ConnectResult != ConnectServerResp.Types.RespType.Connected)
                throw Refused("game-connect", connected.ConnectResult, $"gate refused: {connected.ConnectResult}");
            await accountExpected.WaitAsync(Timeout, _cts.Token);
            await queueExpected.WaitAsync(Timeout, _cts.Token);
        });
    }

    /// <summary>Another ConnectServerReq{NewConnection} on the open game connection (tests: an account switch on one
    /// connection); returns the gate's answer.</summary>
    public async Task<ConnectServerResp.Types.RespType> ConnectAgainAsync(byte[] engineToken)
    {
        using var connectExpected = _inbox.Expect(e => e.Kind == BotEventKind.ConnectResponse);
        await SendWithDrainAsync(_game!, 3, new ConnectServerReq { Type = ConnectServerReq.Types.ReqType.NewConnection, Token = ByteString.CopyFrom(engineToken) });
        return (await connectExpected.WaitAsync(Timeout, _cts.Token)).ConnectResult;
    }

    public async Task<AccountLoginCreateRoleResp> CreateRoleAsync(string name, int sex = 1, int hair = 0)
    {
        return await StageAsync("create-role", async () =>
        {
            var req = new AccountLoginCreateRoleReq { Token = EngineSign, Account = Account, AreaId = 1, Career = 1000, RoleName = name, Sex = sex };
            if (hair != 0) req.Appearances.Add(RoleProto.HairSlot, hair);
            using var expected = _inbox.Expect(e => e.IsGame(MsgId.LoginRoleCreate));
            await SendGameAsync(MsgId.LoginRoleCreate, req);
            var resp = AccountLoginCreateRoleResp.Parser.ParseFrom((await expected.WaitAsync(Timeout, _cts.Token)).Body);
            if (resp.Result == 0) Roles.Insert(0, resp.RoleBaseInfo);
            else if (_loadMode) throw Refused("create-role", resp.Result, $"role create refused: {resp.Result}");
            return resp;
        });
    }

    /// <summary>Selects a role and plays the client side of the world entry: 70010 -> 30005 -> 31005.</summary>
    public async Task<EntityBase> EnterWorldAsync(long roleId)
    {
        using var selectExpected = _inbox.Expect(e => e.IsGame(MsgId.LoginRoleSelect));
        using var avatarExpected = _inbox.Expect(e => e.Kind == BotEventKind.Entity && e.Name == "ClientAvatar");
        using var switchExpected = _inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        await StageAsync("role-select", async () =>
        {
            await SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleReq { Token = EngineSign, Account = Account, AreaId = 1, Id = roleId });
            var select = AccountLoginSelectRoleResp.Parser.ParseFrom((await selectExpected.WaitAsync(Timeout, _cts.Token)).Body);
            if (select.Result != 0) throw Refused("role-select", select.Result, $"role select failed: {select.Result}");
            await avatarExpected.WaitAsync(Timeout, _cts.Token);
            _avatarBound = true;
        });
        var sw = await StageAsync("scene-handoff", async () =>
            SwitchSceneLineResp.Parser.ParseFrom((await switchExpected.WaitAsync(Timeout, _cts.Token)).Body));
        return await StageAsync("scene-ready", async () =>
        {
            using var loadedExpected = _inbox.Expect(e => e.IsGame(MsgId.SceneLoaded));
            using var selfExpected = _inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
            using var endExpected = _inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
            await SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            var loaded = SceneLoadedResp.Parser.ParseFrom((await loadedExpected.WaitAsync(Timeout, _cts.Token)).Body);
            if (loaded.Result != 0) throw Refused("scene-ready", loaded.Result, $"scene load refused: {loaded.Result}");
            var self = EntitySelf.Parser.ParseFrom((await selfExpected.WaitAsync(Timeout, _cts.Token)).Body);
            await endExpected.WaitAsync(Timeout, _cts.Token);
            Self = self.Base;
            return self.Base;
        });
    }

    public EntityBase? Self { get; private set; }

    /// <summary>Back to the role screen like the client's "switch character": PRE_SWITCH_ROLE, RPC SwitchToAccount, reconnect as account.</summary>
    public async Task SwitchRoleAsync()
    {
        using var preExpected = _inbox.Expect(e => e.IsGame(MsgId.PreSwitchRole));
        await SendGameAsync(MsgId.PreSwitchRole, new PreSwitchRoleReq());
        await preExpected.WaitAsync(Timeout);
        using var switchExpected = _inbox.Expect(e => e.Kind == BotEventKind.Rpc && e.Name == "SwitchToAccountCB");
        await SendRpcAsync("SwitchToAccount");
        await switchExpected.WaitAsync(Timeout);
        _avatarBound = false;
        Self = null;
        using var connectExpected = _inbox.Expect(e => e.Kind == BotEventKind.ConnectResponse);
        using var accountExpected = _inbox.Expect(e => e.Kind == BotEventKind.Entity && e.Name == "ClientAccount");
        using var queueExpected = _inbox.Expect(e => e.Kind == BotEventKind.Rpc && e.Name == "OnQueueFinishedNotify");
        await SendWithDrainAsync(_game!, 3, new ConnectServerReq { Type = ConnectServerReq.Types.ReqType.SwitchToAccount, Token = ByteString.CopyFrom(EngineToken) });
        var resp = await connectExpected.WaitAsync(Timeout);
        if (resp.ConnectResult != ConnectServerResp.Types.RespType.SwitchToAccount) throw new InvalidOperationException($"switch refused: {resp.ConnectResult}");
        await accountExpected.WaitAsync(Timeout);
        await queueExpected.WaitAsync(Timeout);
    }

    // ---------------------------------------------------------------- silent reconnect

    /// <summary>ReconnectInfo.Auth / .Mailbox of the latest entity that carried one (opaque msgpack, as the client keeps them).</summary>
    public byte[]? ReconnectAuth { get; private set; }
    public byte[]? ReconnectMailbox { get; private set; }

    /// <summary>The latest OnGetAuth(callerId, relogin, reconnect, auth) the game gate sent.</summary>
    public string? GateCallerId { get; private set; }
    public byte[]? GateAuth { get; private set; }

    /// <summary>
    /// What the real client does after the avatar entity: OnGetAuth -> micro PhonestGate.bind -> RPC
    /// OnClientReadyInternal(bound, false, false). The server only keeps a role for a reconnect once the client has
    /// done this; a heartbeat round trip makes sure it was handled.
    /// </summary>
    public async Task<bool> CompleteGateAuthAsync()
    {
        var getAuth = await _inbox.WaitAsync(e => e.Kind == BotEventKind.Rpc && e.Name == "OnGetAuth", Timeout);
        var (_, relogin, reconnect) = ReadGetAuth(getAuth.Body);
        bool bound = await BindGateAsync(GateCallerId!, GateAuth!);
        await SendRpcAsync("OnClientReadyInternal", bound, relogin, reconnect);
        await HeartbeatAsync();
        return bound;
    }

    /// <summary>HEARTBEAT (msgId 1, empty) and its Pong: everything sent before it has been handled.</summary>
    public async Task HeartbeatAsync()
    {
        using var pong = _inbox.Expect(e => e.IsGame(MsgId.Heartbeat));
        await SendGameBytesAsync(MsgId.Heartbeat, []);
        await pong.WaitAsync(Timeout, _cts.Token);
    }

    /// <summary>Micro PhonestGate.bind {Id, Auth, ClusterId} (GameClient.Bind); true when the gate accepted it.</summary>
    public async Task<bool> BindGateAsync(string callerId, byte[] auth, int clusterId = 1)
    {
        var resp = await MicroAsync("PhonestGate", "bind", new Dictionary<string, object?> { ["Id"] = callerId, ["Auth"] = auth, ["ClusterId"] = clusterId });
        return resp.TryGetValue("Ret", out var ret) && ret is object[] { Length: > 0 } pair && pair[0] is true;
    }

    /// <summary>Drops the game connection: <see cref="BotDrop.Graceful"/> closes it (FIN), <see cref="BotDrop.Abrupt"/>
    /// resets it (RST), <see cref="BotDrop.HalfOpen"/> just stops using it (still read, for the server's kick).</summary>
    public async Task DropGameAsync(BotDrop mode)
    {
        var game = _game ?? throw new InvalidOperationException("no game connection");
        Retire(game);
        _game = null;
        switch (mode)
        {
            case BotDrop.Graceful: await game.DisposeAsync(); break;
            case BotDrop.Abrupt: game.AbortReset(); break;
            case BotDrop.HalfOpen: break;
        }
    }

    /// <summary>The client's ReconnectToServer always replaces the micro connection; the new one is not verified.</summary>
    public async Task ReconnectMicroAsync()
    {
        if (_micro is { } old)
        {
            Retire(old);
            await old.DisposeAsync();
        }
        _micro = await ConnectAsync(MicroGate, OnMicroFrame, requestToken: 1, confirmKey: 2);
    }

    /// <summary>
    /// A new game connection with ConnectServerReq{ReConnection, entityInfo, auth} (default: the bot's ReconnectInfo).
    /// On Reconnected the connection becomes the bot's game connection; otherwise it is dropped.
    /// </summary>
    public async Task<ConnectServerResp.Types.RespType> TryReconnectGateAsync(byte[]? mailbox = null, byte[]? auth = null)
    {
        var channel = await ConnectAsync(GameGate, OnGameFrame, requestToken: 1, confirmKey: 2);
        using var connectExpected = _inbox.Expect(e => e.Kind == BotEventKind.ConnectResponse);
        await SendWithDrainAsync(channel, 3, new ConnectServerReq
        {
            Type = ConnectServerReq.Types.ReqType.ReConnection,
            EntityInfo = ByteString.CopyFrom(mailbox ?? ReconnectMailbox ?? []),
            Auth = ByteString.CopyFrom(auth ?? ReconnectAuth ?? []),
        });
        var result = (await connectExpected.WaitAsync(Timeout, _cts.Token)).ConnectResult;
        if (result == ConnectServerResp.Types.RespType.Reconnected)
        {
            if (_game is { } previous) Retire(previous);
            _game = channel;
        }
        else
        {
            Retire(channel);
            await channel.DisposeAsync();
        }
        return result;
    }

    /// <summary>
    /// The client's silent reconnect (spec §1.4): new micro connection, ReConnection on a new game connection, the
    /// ClientAvatar entity and OnGetAuth, PhonestGate.bind, OnClientReadyInternal(true, true, true), 1011, then 1023
    /// PLAYER_ASSET {assetType=[]}; with <paramref name="expectResync"/> also ENTITYSELF and a 31037.
    /// </summary>
    public async Task<BotReconnectResult> ReconnectGameAsync(bool expectResync = true)
    {
        await ReconnectMicroAsync();
        using var avatarExpected = _inbox.Expect(e => e.Kind == BotEventKind.Entity && e.Name == "ClientAvatar");
        using var authExpected = _inbox.Expect(e => e.Kind == BotEventKind.Rpc && e.Name == "OnGetAuth");
        var result = await TryReconnectGateAsync();
        if (result != ConnectServerResp.Types.RespType.Reconnected) return new(result, null, null, false);
        await avatarExpected.WaitAsync(Timeout, _cts.Token);
        var (_, relogin, reconnect) = ReadGetAuth((await authExpected.WaitAsync(Timeout, _cts.Token)).Body);
        bool bound = await BindGateAsync(GateCallerId!, GateAuth!);
        using var loginExpected = _inbox.Expect(e => e.IsGame(MsgId.LoginReconnect));
        using var selfExpected = _inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        using var aoiExpected = _inbox.Expect(e => e.IsGame(Ro3Emu.Server.Maps.GameAoiSink.SyncAoiView));
        await SendRpcAsync("OnClientReadyInternal", bound, relogin, reconnect);
        var login = UserLoginReconnectRsp.Parser.ParseFrom((await loginExpected.WaitAsync(Timeout, _cts.Token)).Body);
        using var assetExpected = _inbox.Expect(e => e.IsGame(MsgId.PlayerAsset));
        await SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
        EntityBase? self = null;
        bool aoi = false;
        if (expectResync)
        {
            self = EntitySelf.Parser.ParseFrom((await selfExpected.WaitAsync(Timeout, _cts.Token)).Body).Base;
            Self = self;
            await aoiExpected.WaitAsync(Timeout, _cts.Token);
            aoi = true;
        }
        await assetExpected.WaitAsync(Timeout, _cts.Token);
        return new(result, login, self, aoi);
    }

    private (string CallerId, bool Relogin, bool Reconnect) ReadGetAuth(byte[] body)
    {
        var reader = new MessagePackReader(body);
        string callerId = reader.ReadString()!;
        bool relogin = reader.ReadBoolean(), reconnect = reader.ReadBoolean();
        var auth = reader.ReadBytes();
        GateCallerId = callerId;
        GateAuth = auth.HasValue ? auth.Value.ToArray() : [];
        return (callerId, relogin, reconnect);
    }

    private void Retire(PhonestChannel channel)
    {
        lock (_retired) _retired.Add(channel);
    }

    private bool IsRetired(PhonestChannel channel)
    {
        lock (_retired) return _retired.Contains(channel);
    }

    /// <summary>Joins a chat group the way the client does (romsg 77002).</summary>
    public async Task JoinChatAsync(string groupId)
    {
        using var expected = _inbox.Expect(e => e.IsGame(ChatService.JoinChannel));
        await SendGameAsync(ChatService.JoinChannel, new JoinChatChannelReq { GroupId = groupId });
        var response = JoinChatChannelRsp.Parser.ParseFrom((await expected.WaitAsync(Timeout, _cts.Token)).Body);
        if (response.Result != 0) throw Refused("join-chat", response.Result, $"chat join refused for {groupId}: {response.Result}");
    }

    /// <summary>Sends a Tencent.IM text command through the 77010 tunnel (what the patched client's ServerImAdapter does).</summary>
    public Task SendChatAsync(string receiveId, uint chatType, string content, int channelIndex = 0)
    {
        // The client always sends this JSON; LC_ChatHelper.SaveAllChannelContent drops lines without iChannelIndex.
        string extra = channelIndex == 0 ? "" :
            $"{{\"templateId\":2,\"commonData\":{{\"iChannelIndex\":{channelIndex},\"bIsExposed\":false,\"bIsCopy\":false,\"eChatType\":{chatType},\"iRoleId\":{Self?.Uuid ?? 0},\"iUiWordTemplate\":0,\"iReplayTargetId\":0}}}}";
        var body = new Tencent.IM.SendTextChatMessageRequest { ReceiveId = receiveId, ChatType = chatType, Content = content, ExtraParam = extra }.ToByteArray();
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame, 3u); // SEND_TEXT_CHAT_MESSAGE
        body.CopyTo(frame, 8);
        return SendGameBytesAsync(ChatService.TunnelUp, frame);
    }

    /// <summary>Next pushed chat line (tunnel cmd 4 NOTIFY_NEW_TEXT_CHAT_MESSAGE).</summary>
    public async Task<Tencent.IM.TextChatMessage> NextChatLineAsync()
    {
        var e = await _inbox.WaitAsync(e => e.IsGame(ChatService.TunnelDown) && BitConverter.ToUInt32(e.Body) == 4, Timeout);
        return Tencent.IM.NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg;
    }

    /// <summary>The head-name lookup the client makes for every player unit (UserDataService.getAttributeBatchByVersion).</summary>
    public Task<Dictionary<string, object?>> GetRoleAttributesAsync(params long[] uids) => GetRoleFieldsAsync(uids, "Base", "Name");

    /// <summary>getAttributeBatchByVersion for the given global cache types ("Base", "Personal"...), each at version 0.</summary>
    public async Task<Dictionary<string, object?>> GetRoleFieldsAsync(IEnumerable<long> uids, params string[] types)
    {
        var request = new Dictionary<string, object>
        {
            ["UidFieldVersions"] = uids.ToDictionary(u => u.ToString(), _ => (object)types.ToDictionary(t => t, _ => (object)0)),
        };
        return MessagePackSerializer.Deserialize<Dictionary<string, object?>>(await CallMicroAsync("UserDataService", "getAttributeBatchByVersion", request));
    }

    public async Task SaveClientSettingAsync(int key, byte[] value)
    {
        var req = new SaveSettingReq { DbSettingInfo = new DBSettingInfo() };
        req.DbSettingInfo.ClientSetting[key] = ByteString.CopyFrom(value);
        using var expected = _inbox.Expect(e => e.IsGame(MsgId.SaveSetting));
        await SendGameAsync(MsgId.SaveSetting, req);
        await expected.WaitAsync(Timeout);
    }

    /// <summary>Named RPC on the bound entity: methodName + msgpack args concatenated (GateClientHandler.CallRpc).</summary>
    public Task SendRpcAsync(string method, params object[] args)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        foreach (var arg in args) MessagePackSerializer.Serialize(arg.GetType(), ref writer, arg);
        writer.Flush();
        return SendWithDrainAsync(_game!, 4, new EntityMessage { MethodName = ByteString.CopyFromUtf8(method), Parameters = ByteString.CopyFrom(buffer.WrittenSpan) });
    }

    public Task WalkAsync(float x, float y, float z, bool stop = false) =>
        SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = x, Y = y, Z = z }, BStop = stop, SpeedRate = 1 });

    /// <summary>
    /// Like the real client: before an avatar exists game messages go as RPC "EZAdaptorCS"(uint msgId, bin body),
    /// afterwards raw (uint32 LE msgId ‖ body, no methodName).
    /// </summary>
    public Task SendGameAsync(uint msgId, IMessage body) => SendGameBytesAsync(msgId, body.ToByteArray());

    public Task SendGameBytesAsync(uint msgId, byte[] bytes)
    {
        OnGameMessageOffered?.Invoke(msgId);
        if (_avatarBound)
        {
            var parameters = new byte[4 + bytes.Length];
            BitConverter.TryWriteBytes(parameters, msgId);
            bytes.CopyTo(parameters, 4);
            return SendWithDrainAsync(_game!, 4, new EntityMessage { Parameters = ByteString.CopyFrom(parameters) });
        }
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.Write(msgId);
        writer.Write(bytes);
        writer.Flush();
        return SendWithDrainAsync(_game!, 4, new EntityMessage { MethodName = ByteString.CopyFromUtf8("EZAdaptorCS"), Parameters = ByteString.CopyFrom(buffer.WrittenSpan) });
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<PhonestChannel> ConnectAsync(string hostPort, PhonestChannel.FrameHandler handler, ushort requestToken, ushort confirmKey)
    {
        var parts = hostPort.Split(':');
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        PhonestChannel? channel = null;
        try
        {
            await socket.ConnectAsync(IPAddress.Parse(parts[0]), int.Parse(parts[1]), _cts.Token)
                .AsTask().WaitAsync(Timeout, _cts.Token);
            channel = new PhonestChannel(socket, _log) { Metrics = _loadMetrics?.Connect() };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.Session = ready;
            _ = Task.Run(async () =>
            {
                try
                {
                    await channel.RunAsync(handler, _cts.Token);
                    if (!_cts.IsCancellationRequested)
                    {
                        var failure = new IOException("bot connection reached EOF");
                        ready.TrySetException(failure);
                        if (IsRetired(channel))
                        {
                            await channel.DisposeAsync(); // like the client: the server's FIN closes the socket
                            return;
                        }
                        _inbox.Add(new BotEvent(BotEventKind.Disconnect) { Name = "eof" });
                        _inbox.Fail(failure);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        ready.TrySetException(e);
                        if (IsRetired(channel)) return;
                        _inbox.Add(new BotEvent(BotEventKind.Disconnect) { Name = e.GetType().Name });
                        _inbox.Fail(e);
                    }
                }
                catch (Exception e)
                {
                    ready.TrySetException(e);
                    if (!IsRetired(channel)) _inbox.Fail(e);
                }
            });
            await SendWithDrainAsync(channel, requestToken, new Phonest.Proto.Void());
            await ready.Task.WaitAsync(Timeout, _cts.Token);
            return channel;
        }
        catch
        {
            if (channel != null) await channel.DisposeAsync();
            else socket.Dispose();
            throw;
        }
    }

    /// <summary>Client side of Utils.GenEncryptKeyFromToken: RSA-OAEP(EncryptKey) with the gate public key.</summary>
    private async Task AnswerEncryptTokenAsync(PhonestChannel channel, ReadOnlyMemory<byte> body, ushort confirmKey)
    {
        long token = EncryptToken.Parser.ParseFrom(body.Span).Token;
        // The client hashes 16 random bytes with SHA-1; 20 random bytes are the same to the server.
        var sessionKey = RandomNumberGenerator.GetBytes(20);
        var key = new EncryptKey
        {
            HeadPadding = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(8)),
            Key = ByteString.CopyFrom(sessionKey),
            Token = token,
            TailPadding = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
        };
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_gatePublicKeyPem);
        channel.EnableConvertor(sessionKey, plaintextNextSend: true);
        await SendWithDrainAsync(channel, confirmKey, new EncryptKeyString { KeyString = ByteString.CopyFrom(rsa.Encrypt(key.ToByteArray(), RSAEncryptionPadding.OaepSHA1)) });
    }

    private async ValueTask OnMicroFrame(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body)
    {
        switch (index)
        {
            case 10: await AnswerEncryptTokenAsync(channel, body, 2); break;
            case 11: ((TaskCompletionSource)channel.Session!).TrySetResult(); break;
            case 34:
                var response = MsResponse.Parser.ParseFrom(body.Span);
                TaskCompletionSource<byte[]>? pending;
                lock (_pendingMicro) _pendingMicro.Remove(response.Rid, out pending);
                pending?.TrySetResult(response.Params.ToByteArray());
                break;
            case 35:
                var push = MsPush.Parser.ParseFrom(body.Span);
                _inbox.Add(new BotEvent(BotEventKind.Rpc) { Name = "push:" + push.Service + "." + push.Method, Body = push.Params.ToByteArray() });
                break;
        }
    }

    /// <summary>A micro call with string-keyed args (as the Lua friend calls send them); the decoded response map.</summary>
    public async Task<Dictionary<object, object?>> MicroAsync(string service, string method, Dictionary<string, object?> args) =>
        MessagePackSerializer.Deserialize<Dictionary<object, object?>>(await CallMicroAsync(service, method, args));

    /// <summary>Lua DALs also send positional msgpack params (MailService content/read/group sync).</summary>
    public async Task<Dictionary<object, object?>> MicroPositionalAsync(string service, string method, object?[] args) =>
        MessagePackSerializer.Deserialize<Dictionary<object, object?>>(await CallMicroAsync(service, method, args));

    private async ValueTask OnGameFrame(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body)
    {
        if (IsRetired(channel))
        {
            // A dropped or superseded connection: only the server's kick on it is of interest.
            if (index == 5) _inbox.Add(new BotEvent(BotEventKind.Disconnect) { Name = "retired", Body = body.ToArray() });
            return;
        }
        switch (index)
        {
            case 10: await AnswerEncryptTokenAsync(channel, body, 2); break;
            case 11: ((TaskCompletionSource)channel.Session!).TrySetResult(); break;
            case 1: _inbox.Add(new BotEvent(BotEventKind.ConnectResponse) { ConnectResult = ConnectServerResp.Parser.ParseFrom(body.Span).Type }); break;
            case 4:
                var info = ChannelEntityInfo.Parser.ParseFrom(body.Span);
                if (info.HasReconnectInfo && info.ReconnectInfo.Length > 0)
                {
                    // Phonest ReconnectInfo, [MessagePackObject(true)] {Auth: bin, Mailbox: bin}; opaque to the client.
                    var reconnect = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(info.ReconnectInfo.Memory);
                    ReconnectAuth = reconnect["Auth"];
                    ReconnectMailbox = reconnect["Mailbox"];
                }
                _inbox.Add(new BotEvent(BotEventKind.Entity) { Name = info.Type.ToStringUtf8(), Body = info.EntityId.ToByteArray() });
                break;
            case 5:
                if (!ReferenceEquals(channel, _game))
                {
                    _inbox.Add(new BotEvent(BotEventKind.Disconnect) { Name = "retired", Body = body.ToArray() });
                    break;
                }
                _inbox.Add(new BotEvent(BotEventKind.Disconnect) { Body = body.ToArray() });
                _inbox.Fail(new IOException("server requested bot disconnect"));
                break;
            case 2:
                var message = EntityMessage.Parser.ParseFrom(body.Span);
                if (message.HasMethodName)
                {
                    _inbox.Add(new BotEvent(BotEventKind.Rpc) { Name = message.MethodName.ToStringUtf8(), Body = message.Parameters.ToByteArray() });
                }
                else
                {
                    var p = message.Parameters.Memory;
                    _inbox.Add(new BotEvent(BotEventKind.Game) { MsgId = BitConverter.ToUInt32(p.Span), Body = p[4..].ToArray() });
                }
                break;
        }
    }

    private async Task<byte[]> CallMicroAsync(string service, string method, object args)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong rid;
        lock (_pendingMicro)
        {
            rid = ++_nextRid;
            _pendingMicro[rid] = tcs;
        }
        try
        {
            await SendWithDrainAsync(_micro!, 33, new MsRequest
            {
                Rid = rid,
                Service = service,
                Method = method,
                Params = ByteString.CopyFrom(MessagePackSerializer.Serialize(args)),
            });
            return await tcs.Task.WaitAsync(Timeout, _cts.Token);
        }
        finally { lock (_pendingMicro) _pendingMicro.Remove(rid); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_game != null) await _game.DisposeAsync();
        if (_micro != null) await _micro.DisposeAsync();
        PhonestChannel[] retired;
        lock (_retired) retired = [.. _retired];
        foreach (var channel in retired) await channel.DisposeAsync();
        _http.Dispose();
    }
}

/// <summary>How <see cref="BotClient.DropGameAsync"/> loses the game connection.</summary>
public enum BotDrop { Graceful, Abrupt, HalfOpen }

/// <summary>What a silent reconnect got: the gate's answer, then (on Reconnected) 1011, ENTITYSELF and whether a 31037 came.</summary>
public sealed record BotReconnectResult(ConnectServerResp.Types.RespType Result, UserLoginReconnectRsp? Login, EntityBase? Self, bool AoiView);
