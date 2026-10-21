using System.Collections.Concurrent;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Phonest.Proto;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The Phonest silent reconnect of the game gate (test plan §5.2).</summary>
public sealed class ReconnectTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-reconnect-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly ConnectServerResp.Types.RespType Reconnected = ConnectServerResp.Types.RespType.Reconnected;
    private static readonly ConnectServerResp.Types.RespType Refused = ConnectServerResp.Types.RespType.ReconnectRefused;
    private WebApplication? _app;
    private bool _stopped;
    private string _http = "";
    private readonly ConcurrentQueue<(string Phase, GameSession Session)> _phases = new();
    private readonly ConcurrentQueue<(string Step, GameSession Session)> _cleanupSteps = new();

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(int graceSeconds = 60, bool gameData = false)
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory;
            if (gameData) o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = hp;
            o.MicroGatePort = mp;
            o.GameGatePort = gp;
            o.ReconnectGraceSeconds = graceSeconds;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
        // Probes: every handover phase and every role cleanup step (none may run while a role waits for its client).
        Service<Ro3Emu.Server.Game.Ownership.RoleHandover>().PhaseForTest = (phase, session) => _phases.Enqueue((phase, session));
        Service<RoleSessionService>().CleanupFailureForTest = (step, session) => { _cleanupSteps.Enqueue((step, session)); return null; };
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            if (!_stopped) await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private T Service<T>() where T : notnull => _app!.Services.GetRequiredService<T>();

    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await OnLoop(condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never: " + what);
            await Task.Delay(25);
        }
    }

    private BotClient NewBot() => new(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));

    /// <summary>A role in the world. <paramref name="gateAuth"/>: the bot answers OnGetAuth like the real client.</summary>
    private async Task<BotClient> InWorldAsync(string name, bool gateAuth = true)
    {
        var bot = NewBot();
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        if (gateAuth) Assert.True(await bot.CompleteGateAuthAsync());
        return bot;
    }

    private GameSession SessionOf(string account) =>
        Service<SessionRegistry>().All().Single(s => string.Equals(s.Account, account, StringComparison.OrdinalIgnoreCase));

    private Task<bool> InGrace(GameSession session) =>
        OnLoop(() => Service<ReconnectService>().TicketOf(session)?.GraceUntil != null && session.TransportLost);

    private Task WaitGraceAsync(GameSession session) =>
        Until(() => Service<ReconnectService>().TicketOf(session)?.GraceUntil != null, "reconnect grace");

    private int Detached(GameSession session) => _phases.Count(p => p.Phase == "Detached" && ReferenceEquals(p.Session, session));

    private static AoIViewChangeData View(BotEvent e) => AoIViewChangeData.Parser.ParseFrom(e.Body);

    [Fact]
    public async Task GracefulDropReconnectsTheSameSessionWithoutLogout()
    {
        await StartAsync(gameData: true);
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        var role = session.Role!;
        await alice.WalkAsync(alice.Self!.MovementInfo.Pos.X + 1, alice.Self.MovementInfo.Pos.Y, alice.Self.MovementInfo.Pos.Z + 1, stop: true);
        await alice.HeartbeatAsync();
        var p = await OnLoop(() => session.Position.Clone());
        long handovers = Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().HandoverCount;
        var oldAuth = alice.ReconnectAuth!;
        Assert.NotNull(alice.ReconnectMailbox);
        Assert.Equal($"ava-{role.Id}", alice.GateCallerId);

        await alice.DropGameAsync(BotDrop.Graceful);
        await WaitGraceAsync(session);
        Assert.True(await InGrace(session));
        await Task.Delay(1000);
        var result = await alice.ReconnectGameAsync();

        Assert.Equal(Reconnected, result.Result);
        Assert.False(oldAuth.AsSpan().SequenceEqual(alice.ReconnectAuth), "the ticket rotates on use");
        Assert.Equal(0, result.Login!.Result);
        Assert.Equal(role.Id, result.Login.RoleID);
        Assert.Equal("alice", result.Login.Account);
        Assert.Equal(await OnLoop(() => session.SceneEntry), result.Login.SceneId);
        Assert.Equal((ulong)role.Id, result.Self!.Uuid);
        Assert.Equal(p.X, result.Self.MovementInfo.Pos.X);
        Assert.Equal(p.Z, result.Self.MovementInfo.Pos.Z);
        Assert.True(result.AoiView);
        Assert.Equal(handovers, Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().HandoverCount);
        Assert.Same(session, SessionOf("alice"));
        Assert.Same(role, await OnLoop(() => session.Role));
        Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.Attached, await OnLoop(() => session.Owner!.State));
        Assert.False(await OnLoop(() => session.TransportLost));
        Assert.Empty(_cleanupSteps);
        Assert.Empty(_phases);
        Assert.Equal(1, Service<ReconnectService>().Reconnects);

        // The new connection is the session's transport: a walk is accepted on it.
        await alice.WalkAsync(p.X + 1, p.Y, p.Z, stop: true);
        await alice.HeartbeatAsync();
        Assert.Equal(p.X + 1, (await OnLoop(() => session.Position)).X);
    }

    [Fact]
    public async Task ObserverSeesNoLeaveOrSecondEnterWhileThePlayerReconnects()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        await using var bob = await InWorldAsync("Bob", gateAuth: false);
        ulong aliceId = alice.Self!.Uuid, bobId = bob.Self!.Uuid;
        await bob.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView) && View(e).Bases.Any(b => b.Uuid == aliceId), Wait);
        await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView) && View(e).Bases.Any(b => b.Uuid == bobId), Wait);
        int bobStart = bob.Inbox.Snapshot().Count;
        int aliceStart = alice.Inbox.Snapshot().Count;
        var session = SessionOf("alice");

        await alice.DropGameAsync(BotDrop.Abrupt);
        await WaitGraceAsync(session);
        var result = await alice.ReconnectGameAsync();
        Assert.Equal(Reconnected, result.Result);

        // Alice gets Bob replayed, to her only.
        Assert.Contains(alice.Inbox.Snapshot().Skip(aliceStart),
            e => e.IsGame(GameAoiSink.SyncAoiView) && View(e).Bases.Any(b => b.Uuid == bobId));
        // Bob still shows Alice: her walk reaches him as a move, with no leave or enter in between.
        await alice.WalkAsync(alice.Self!.MovementInfo.Pos.X + 1, alice.Self.MovementInfo.Pos.Y, alice.Self.MovementInfo.Pos.Z, stop: true);
        await bob.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.MovementChange)
            && MovementChangeNtf.Parser.ParseFrom(e.Body).Uid == (long)aliceId, Wait);
        await bob.HeartbeatAsync();
        Assert.DoesNotContain(bob.Inbox.Snapshot().Skip(bobStart), e => e.IsGame(GameAoiSink.SyncAoiView)
            && (View(e).LeaveAoi.Contains(aliceId) || View(e).Bases.Any(b => b.Uuid == aliceId)));
    }

    [Fact]
    public async Task HalfOpenOldSocketIsKickedAsReconnectedAndItsCloseCleansNothing()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        long handovers = Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().HandoverCount;

        await alice.DropGameAsync(BotDrop.HalfOpen);
        var result = await alice.ReconnectGameAsync();
        Assert.Equal(Reconnected, result.Result);

        var kick = await alice.Inbox.WaitAsync(e => e.Kind == BotEventKind.Disconnect && e.Name == "retired", Wait);
        Assert.Equal(ReconnectService.ReasonBeReconnected, DisconnectClientInfo.Parser.ParseFrom(kick.Body).Reason);
        await Task.Delay(500); // the server closes the old socket; its reader ends
        var p = alice.Self!.MovementInfo.Pos;
        await alice.WalkAsync(p.X + 1, p.Y, p.Z, stop: true);
        await alice.HeartbeatAsync();
        Assert.Equal(p.X + 1, (await OnLoop(() => session.Position)).X);
        Assert.Equal(handovers, Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().HandoverCount);
        Assert.Same(session, SessionOf("alice"));
        Assert.NotNull(await OnLoop(() => session.Role));
        Assert.Empty(_cleanupSteps);
    }

    [Fact]
    public async Task ExpiredGraceLogsOutOnceAndRefusesTheLateReconnect()
    {
        await StartAsync(graceSeconds: 1);
        await using var alice = await InWorldAsync("Alice");
        await using var bob = await InWorldAsync("Bob", gateAuth: false);
        ulong aliceId = alice.Self!.Uuid;
        long roleId = (long)aliceId;
        var session = SessionOf("alice");
        var p = alice.Self.MovementInfo.Pos;
        await alice.WalkAsync(p.X + 2, p.Y, p.Z, stop: true);
        await alice.HeartbeatAsync();
        float walkedX = p.X + 2;

        await alice.DropGameAsync(BotDrop.Graceful);
        await Until(() => Detached(session) == 1, "grace expiry handover");
        await bob.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView) && View(e).LeaveAoi.Contains(aliceId), Wait);
        Assert.Contains(_cleanupSteps, s => s.Step == "position" && ReferenceEquals(s.Session, session));
        Assert.Equal(walkedX, Service<RoleStore>().Get(roleId)!.X);
        Assert.Equal(1, Service<ReconnectService>().GracesExpired);
        Assert.Null(await OnLoop(() => session.Role));
        Assert.DoesNotContain(Service<SessionRegistry>().All(), s => s.Account == "alice");

        Assert.Equal(Refused, await alice.TryReconnectGateAsync());
        await Task.Delay(300);
        Assert.Equal(1, Detached(session));

        await using var again = NewBot();
        await again.LoginAsync("alice", "pw");
        await again.EnterGateAsync();
        var self = await again.EnterWorldAsync(roleId);
        Assert.Equal(walkedX, self.MovementInfo.Pos.X);
    }

    [Fact]
    public async Task BadCredentialsAreRefusedWithoutTouchingTheGraceAndRateLimited()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.Graceful);
        await WaitGraceAsync(session);

        var wrongAuth = MessagePackSerializer.Serialize(new string('0', 32));
        var unknown = MessagePackSerializer.Serialize(new object[] { "ava-999999999", "game", 1, false });
        Assert.Equal(Refused, await alice.TryReconnectGateAsync(auth: wrongAuth));
        Assert.Equal(Refused, await alice.TryReconnectGateAsync(mailbox: unknown));
        Assert.Equal(Refused, await alice.TryReconnectGateAsync(mailbox: [0xC1]));
        Assert.Equal(Refused, await alice.TryReconnectGateAsync(auth: [0xC1]));
        Assert.Equal(Refused, await alice.TryReconnectGateAsync(auth: MessagePackSerializer.Serialize("not hex")));
        Assert.True(await InGrace(session));
        Assert.Empty(_phases);

        // Five refusals from this address inside 10 s: the sixth attempt is refused unseen, even with valid credentials.
        Assert.Equal(Refused, await alice.TryReconnectGateAsync());
        Assert.Equal(6, Service<ReconnectService>().Refusals);
        Assert.True(await InGrace(session));

        await OnLoop(() => { Service<ReconnectService>().ResetRefusalsForTest(); return true; });
        var result = await alice.ReconnectGameAsync();
        Assert.Equal(Reconnected, result.Result);
        Assert.Empty(_phases);
        Assert.Empty(_cleanupSteps);
    }

    [Fact]
    public async Task DuplicateLoginDuringGraceCleansTheOldRoleOnceAndRefusesItsReconnect()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        long roleId = (long)alice.Self!.Uuid;
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.Graceful);
        await WaitGraceAsync(session);

        await using var again = NewBot();
        await again.LoginAsync("alice", "pw");
        await again.EnterGateAsync();
        await Until(() => Detached(session) == 1, "replaced session handover");
        Assert.Null(await OnLoop(() => Service<ReconnectService>().TicketOf(session)));

        Assert.Equal(Refused, await alice.TryReconnectGateAsync());
        await again.EnterWorldAsync(roleId);
        await Task.Delay(300);
        Assert.Equal(1, Detached(session));
        Assert.Equal(1, _cleanupSteps.Count(s => s.Step == "world" && ReferenceEquals(s.Session, session)));
        Assert.Equal(0, Service<ReconnectService>().GracesExpired);
    }

    [Fact]
    public async Task RoleSwitchRevokesTheAvatarTicket()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        await alice.SwitchRoleAsync();
        Assert.Null(await OnLoop(() => Service<ReconnectService>().TicketOf(session)));
        Assert.NotNull(alice.ReconnectMailbox); // the client keeps the stale ReconnectInfo (CleanAllEntities does not clear it)

        await alice.DropGameAsync(BotDrop.Graceful);
        Assert.Equal(Refused, await alice.TryReconnectGateAsync());
        await Until(() => !Service<SessionRegistry>().All().Any(s => s.Account == "alice"), "role screen logout");
    }

    [Fact]
    public async Task DropDuringASceneHandoffResumesItThroughTheClientsSceneLoaded()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        using var switched = alice.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent("!warp 10100"))).EnsureSuccessStatusCode();
        var change = SwitchSceneLineResp.Parser.ParseFrom((await switched.WaitAsync(Wait)).Body);
        Assert.Equal(10100, change.SceneEntry);

        await alice.DropGameAsync(BotDrop.Abrupt);
        await WaitGraceAsync(session);
        var result = await alice.ReconnectGameAsync(expectResync: false);
        Assert.Equal(Reconnected, result.Result);
        Assert.Equal(10100, result.Login!.SceneId);

        using var loadedExpected = alice.Inbox.Expect(e => e.IsGame(MsgId.SceneLoaded));
        using var selfExpected = alice.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        using var aoiExpected = alice.Inbox.Expect(e => e.IsGame(GameAoiSink.SyncAoiView));
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
        Assert.Equal(0, SceneLoadedResp.Parser.ParseFrom((await loadedExpected.WaitAsync(Wait)).Body).Result);
        Assert.Equal((ulong)session.Role!.Id, EntitySelf.Parser.ParseFrom((await selfExpected.WaitAsync(Wait)).Body).Base.Uuid);
        // No NPCs on this data-less map: the reconnect still gets the 31037 its resync waits for.
        await aoiExpected.WaitAsync(Wait);
        Assert.Equal(10100, await OnLoop(() => session.SceneEntry));
        Assert.False(await OnLoop(() => session.SceneLoading));
        Assert.Empty(_phases);
    }

    [Fact]
    public async Task EmptyMapStillGetsAnAoiView()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.Graceful);
        await WaitGraceAsync(session);
        int start = alice.Inbox.Snapshot().Count;
        var result = await alice.ReconnectGameAsync();
        Assert.Equal(Reconnected, result.Result);
        var view = View(alice.Inbox.Snapshot().Skip(start).First(e => e.IsGame(GameAoiSink.SyncAoiView)));
        Assert.Empty(view.Bases);
        Assert.Empty(view.LeaveAoi);
    }

    [Fact]
    public async Task MicroBindsWithTheIssuedGateAuthOnly()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.Graceful);
        await WaitGraceAsync(session);
        Assert.Equal(Reconnected, (await alice.ReconnectGameAsync()).Result);
        var roles = await alice.MicroPositionalAsync("LoginService", "getRoleInfo", ["", ""]);
        Assert.Equal(true, ((object[])roles["Ret"]!)[0]);

        // A fresh, unverified micro connection: a wrong secret binds nothing.
        await alice.ReconnectMicroAsync();
        Assert.False(await alice.BindGateAsync(alice.GateCallerId!, new byte[16]));
        var refused = await alice.MicroPositionalAsync("LoginService", "getRoleInfo", ["", ""]);
        Assert.Equal(false, ((object[])refused["Ret"]!)[0]);
        Assert.True(await alice.BindGateAsync(alice.GateCallerId!, alice.GateAuth!));
        roles = await alice.MicroPositionalAsync("LoginService", "getRoleInfo", ["", ""]);
        Assert.Equal(true, ((object[])roles["Ret"]!)[0]);

        // refreshAuth (hourly in the client) returns the next secret in base64; the old one stops working.
        var refreshed = await alice.MicroAsync("PhonestGate", "refreshAuth",
            new Dictionary<string, object?> { ["Id"] = alice.GateCallerId, ["OldAuth"] = alice.GateAuth });
        var ret = (object[])refreshed["Ret"]!;
        Assert.Equal(true, ret[0]);
        var next = Convert.FromBase64String((string)ret[1]!);
        await alice.ReconnectMicroAsync();
        Assert.False(await alice.BindGateAsync(alice.GateCallerId!, alice.GateAuth!));
        Assert.True(await alice.BindGateAsync(alice.GateCallerId!, next));
    }

    [Fact]
    public async Task ShutdownDuringGraceRunsTheCleanupAndFence()
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.Graceful);
        await WaitGraceAsync(session);

        _stopped = true;
        await _app!.StopAsync();
        Assert.Equal(1, Detached(session));
        Assert.Contains(_cleanupSteps, s => s.Step == "world" && ReferenceEquals(s.Session, session));
        Assert.Contains(_phases, p => p.Phase == "BeforeFence" && ReferenceEquals(p.Session, session));
        Assert.Null(session.Role); // the loop has stopped: its state is quiet now
        Assert.Null(session.Owner);
        Assert.True(Service<GameLoop>().DrainStatus.Clean);
    }

    [Fact]
    public async Task RoleScreenDropLogsOutImmediately()
    {
        await StartAsync();
        await using var alice = NewBot();
        await alice.LoginAsync("alice", "pw");
        await alice.EnterGateAsync();
        await alice.DropGameAsync(BotDrop.Graceful);
        await Until(() => !Service<SessionRegistry>().All().Any(s => s.Account == "alice"), "role screen logout");
        Assert.Equal(0, Service<ReconnectService>().GracesEntered);
    }

    [Fact]
    public async Task ClientWithoutGateAuthLogsOutImmediately()
    {
        // Every bot before reconnect support (and any client that never answered OnGetAuth) keeps today's logout.
        await StartAsync();
        await using var alice = await InWorldAsync("Alice", gateAuth: false);
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.Graceful);
        await Until(() => Detached(session) == 1, "immediate handover");
        Assert.Equal(0, Service<ReconnectService>().GracesEntered);
        Assert.Equal(Refused, await alice.TryReconnectGateAsync());
    }

    [Theory]
    [InlineData("fin")]
    [InlineData("rst")]
    public async Task GmNetDropIsReconnectable(string mode)
    {
        await StartAsync();
        await using var alice = await InWorldAsync("Alice");
        var session = SessionOf("alice");
        await alice.DropGameAsync(BotDrop.HalfOpen); // the bot stops using it; the server drops it
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent("!netdrop " + mode))).EnsureSuccessStatusCode();
        await WaitGraceAsync(session);
        var result = await alice.ReconnectGameAsync();
        Assert.Equal(Reconnected, result.Result);
        Assert.Empty(_phases);
    }
}
