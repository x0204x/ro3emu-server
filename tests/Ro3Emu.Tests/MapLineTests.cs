using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Tencent.IM;

namespace Ro3Emu.Tests;

/// <summary>Real 70009/70010/30005 flow, isolation and failed-load recovery over loopback game gates.</summary>
public sealed class MapLineTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-lines-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private sealed class Clock : TimeProvider
    {
        public TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }
    private readonly Clock _clock = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Player(string name) => Service<SessionRegistry>().All().Single(s => s.Account == name.ToLowerInvariant());
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private static string RepoRoot()
    {
        string? p = AppContext.BaseDirectory;
        while (p != null && !File.Exists(Path.Combine(p, "Ro3Emu.slnx"))) p = Path.GetDirectoryName(p);
        return p!;
    }
    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; }, _clock);
        SureHitRolls.Install(_app);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
    }
    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id); await Ready(Player(name)); return bot;
    }
    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
        => parser.ParseFrom((await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);
    private async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < end)
        {
            if (await Service<GameLoop>().InvokeAsync(() => Task.FromResult(predicate()))) return;
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Bounded world-state condition did not complete.");
    }
    private Task Ready(GameSession s) => Until(() => s.Handoff == null && !s.SceneLoading && Service<World>().PlayersIn(s.WorldScene).Contains(s));
    private async Task Load(BotClient bot, GameSession s)
    {
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = s.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser); await Ready(s);
    }
    private async Task<SwitchSceneLineResp> Move(BotClient bot, GameSession s, long map, bool newLine = false, bool load = true)
    {
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(s, map, createLine: newLine)));
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, p => p.Result == 0);
        Assert.Equal(s.LineNumber, sw.Linenum); Assert.Equal(s.WorldScene, sw.SceneID);
        if (load) await Load(bot, s); return sw;
    }
    private static bool Chat(BotEvent e, string text) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8
        && BitConverter.ToUInt32(e.Body, 0) == 4 && NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg.Content == text;

    [ClientDataTheory]
    [InlineData("4_1")]
    [InlineData("@TGS#_4_1")]
    public async Task SameMapSwitchReservesUntilLoadedRejectsSecondAndIsolatesAoiChatAndSocial(string channel)
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("Alice"); var b = Player("Bob");
        await Move(alice, a, 10100); await Move(bob, b, 10100);
        Assert.Contains(b, a.Visible); Assert.Contains(a, b.Visible);
        await alice.JoinChatAsync(channel); await bob.JoinChatAsync(channel);
        await alice.SendChatAsync(channel, 2, "before-switch");
        await bob.Inbox.WaitAsync(e => Chat(e, "before-switch"), Wait);
        var sw = await Move(bob, b, 10100, newLine: true, load: false);
        Assert.Equal(2, sw.Linenum); Assert.Equal(0, b.InstanceId);
        Assert.DoesNotContain(b, a.Visible); Assert.DoesNotContain(a, b.Visible);
        Assert.DoesNotContain(b, Service<World>().PlayersIn(b.WorldScene));
        var reservation = Service<PublicLineManager>().Reserved(b);
        Assert.NotNull(reservation);
        await bob.SendGameAsync(70009, new GetSceneLineInfoReq { PlayerUUID = (ulong)b.Role!.Id });
        var listing = await Next(bob, 70009, GetSceneLineInfoResp.Parser);
        Assert.Equal(0, listing.Result); Assert.Equal(new[] { 1, 2 }, listing.LineInfo.Select(l => l.Linenum));
        Assert.All(listing.LineInfo, l => Assert.Equal(1, l.Currolenum));
        await bob.SendGameAsync(MsgId.SwitchSceneLine, new SwitchSceneLineReq { Linenum = 1 });
        Assert.Equal(ErrorCodes.SwitchingScenes, (await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).Result);
        Assert.Equal(reservation, Service<PublicLineManager>().Reserved(b));
        await Load(bob, b);
        Assert.Null(Service<PublicLineManager>().Reserved(b));
        await bob.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
        Assert.NotEqual(0, (await Next(bob, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await alice.SendChatAsync(channel, 2, "line-one-only");
        await alice.Inbox.WaitAsync(e => Chat(e, "line-one-only"), Wait);
        await alice.SendChatAsync(b.Role.Id.ToString(), 1, "across-lines-whisper");
        await bob.Inbox.WaitAsync(e => Chat(e, "across-lines-whisper"), Wait);
        Assert.DoesNotContain(bob.Inbox.Snapshot(), e => Chat(e, "line-one-only"));
        await bob.JoinChatAsync(channel);
        await bob.SendGameAsync(70009, new GetSceneLineInfoReq()); await Next(bob, 70009, GetSceneLineInfoResp.Parser);
        Assert.DoesNotContain(bob.Inbox.Snapshot(), e => Chat(e, "line-one-only")); // local history is also isolated
        var found = await alice.MicroAsync("NameService", "getUidByName", new() { ["Name"] = "bob" });
        Assert.Equal(b.Role.Id.ToString(), found["Uid"]);
        await bob.SendGameAsync(MsgId.SwitchSceneLine, new SwitchSceneLineReq { Linenum = 1, ESource = SwitchSceneSourceType.SwitchSceneSourceManual });
        var back = await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(0, back.Result); Assert.Equal(1, back.Linenum); Assert.Equal(SwitchSceneSourceType.SwitchSceneSourceManual, back.ESource);
        await Load(bob, b); Assert.Contains(b, a.Visible); Assert.Contains(a, b.Visible);
    }

    [ClientDataFact]
    public async Task InvalidLineAndForgedOwnerRefusedThenFailedLoadRequiresReconnectOnValidLine()
    {
        var alice = await InWorld("Alice"); var a = Player("Alice");
        await Move(alice, a, 10100);
        foreach (var req in new[]{new SwitchSceneLineReq{Linenum=999},new SwitchSceneLineReq{Linenum=2,PlayerUUID=999999},
            new SwitchSceneLineReq{Linenum=2,SceneEntry=10111},new SwitchSceneLineReq{Linenum=2,Area=8}})
        {
            await alice.SendGameAsync(MsgId.SwitchSceneLine, req);
            Assert.NotEqual(0, (await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).Result);
            Assert.Null(a.Handoff); Assert.Equal(1, a.LineNumber);
        }
        await Move(alice, a, 10100, newLine: true, load: false);
        long generation = a.SceneGeneration;
        // 30012 is also the client's failed-load recovery request. The old config-only acknowledgment is ambiguous.
        await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = 10100 });
        await Until(() => a.SceneRecoveryRequired && Service<PublicLineManager>().Reserved(a) == null);
        Assert.True(a.SceneGeneration > generation);
        await OnLoop(() => Service<SceneService>().SceneLoadedAsync(a, new SceneLoadedReq { SceneId = 10100 }));
        Assert.DoesNotContain(a, Service<World>().PlayersIn(a.WorldScene));
        await alice.DisposeAsync();
        await Until(() => !Service<SessionRegistry>().All().Contains(a));
        await using var reconnect = await InWorld("Alice", create: false);
        var current = Player("Alice"); Assert.Equal(0, current.InstanceId);
        Assert.Contains(Service<PublicLineManager>().Snapshot(current.SceneEntry), l => l.Map.LineNumber == current.LineNumber);
        Assert.Null(current.Handoff); Assert.False(current.SceneRecoveryRequired);
    }

    [ClientDataFact]
    public async Task HostilePlayersAtSamePositionCannotDamageAcrossLinesButCanAfterJoining()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("Alice"); var b = Player("Bob");
        await Move(alice, a, 10100); await Move(bob, b, 10100, newLine: true);
        await OnLoop(() =>
        {
            Service<CombatService>().PlayerHostile = (_, _) => true; // force hostility so locality is the deciding gate
            return Task.CompletedTask;
        });
        long hp = Service<CharacterService>().Stats(b)[2000];
        int skill = Service<SkillService>().NormalAttack(a);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = b.Role!.Id });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(hp, Service<CharacterService>().Stats(b)[2000]);
        Assert.Null(a.Attack); Assert.Null(a.SkillCast);
        await bob.SendGameAsync(MsgId.SwitchSceneLine, new SwitchSceneLineReq { Linenum = 1 });
        Assert.Equal(0, (await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).Result);
        await Load(bob, b);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = b.Role.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(() => Service<CharacterService>().Stats(b)[2000] < hp);
    }

    [ClientDataFact]
    public async Task LoadDeadlineReleasesReservationAndFencesLateAcknowledgment()
    {
        await using var alice = await InWorld("Alice"); var a = Player("Alice");
        await Move(alice, a, 10100, newLine: true, load: false);
        var old = a.Handoff!;
        await OnLoop(() =>
        {
            _clock.Offset = TimeSpan.FromSeconds(31);
            Service<SceneService>().ExpireLineLoad(a);
            Assert.True(a.SceneRecoveryRequired);
            Assert.Null(Service<PublicLineManager>().Reserved(a));
            return Task.CompletedTask;
        });
        await OnLoop(() => Service<SceneService>().SceneLoadedAsync(a, new SceneLoadedReq { SceneId = 10100 }));
        Assert.True(a.SceneGeneration > old.Generation);
        Assert.DoesNotContain(a, Service<World>().PlayersIn(old.Destination.WorldScene));
    }

    [ClientDataFact]
    public async Task SuspendedLoadPublicationCannotBeSupersededByAnotherLineOfTheSameMap()
    {
        await using var alice = await InWorld("Alice");
        var player = Player("Alice");
        await Move(alice, player, 10100, newLine: true, load: false);
        var handoff = player.Handoff!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(() =>
        {
            Service<SceneService>().BeforeSelf.Add(async s =>
            {
                if (s == player) { entered.TrySetResult(); await release.Task; }
            });
            return Task.CompletedTask;
        });
        try
        {
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
            Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await entered.Task.WaitAsync(Wait);
            await OnLoop(async () =>
            {
                Assert.False(await Service<SceneService>().EnterSceneAsync(player, 10100, lineNumber: 1));
                Assert.False(await Service<SceneService>().EnterSceneAsync(player, 10100, createLine: true));
                await Service<SceneService>().SwitchLineAsync(player, new SwitchSceneLineReq { Linenum = 1 });
                Assert.Same(handoff, player.Handoff);
                Assert.Equal(handoff.Reservation, Service<PublicLineManager>().Reserved(player));
                Assert.DoesNotContain(player, Service<World>().PlayersIn(player.WorldScene));
            });
            Assert.Equal(ErrorCodes.SwitchingScenes,
                (await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).Result);
        }
        finally { release.TrySetResult(); }
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await Ready(player);
        Assert.Equal(handoff.Generation, player.SceneGeneration);
        Assert.Equal(2, player.LineNumber);
        Assert.Null(Service<PublicLineManager>().Reserved(player));
    }

    [ClientDataFact]
    public async Task SuspendedCastCannotCreateGroundUnitAfterLineDeparture()
    {
        await using var alice = await InWorld("Alice"); var a = Player("Alice");
        await Move(alice, a, 10100, newLine: true);
        var old = a.RuntimeMap;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            await Service<GmCommands>().RunAsync(a, "!job 2100");
            a.Progress!.Skills[12010] = 1;
            Service<CombatService>().CastLanded.Add(async (s, _) => { if (s == a) { entered.TrySetResult(); await release.Task; } });
        });
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = a.Position.Clone() });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await entered.Task.WaitAsync(Wait);
            await Move(alice, a, 10100);
            Assert.NotEqual(old, a.RuntimeMap);
            await OnLoop(async () =>
            {
                Assert.True(Service<CombatService>().HasPendingMapWork(old));
                await Service<PublicLineManager>().RetireEmptyAsync();
                Assert.Contains(Service<PublicLineManager>().Snapshot(10100), l => l.Map == old);
            });
        }
        finally { release.TrySetResult(); }
        await Until(() => !Service<CombatService>().HasPendingMapWork(old));
        await OnLoop(() => Service<PublicLineManager>().RetireEmptyAsync());
        Assert.Empty(a.SkillObjects);
        // Unit maps are loop-owned: enumerating them from the test thread raced the loop ("collection was modified").
        var units = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits().ToList()));
        Assert.DoesNotContain(units, u => u.WorldScene == old.WorldScene);
        Assert.DoesNotContain(Service<PublicLineManager>().Snapshot(10100), l => l.Map == old);
    }

    [ClientDataFact]
    public async Task FaultDuringLeaveReleasesExactDestinationAndCannotPublishOldLoad()
    {
        await using var alice = await InWorld("Alice"); var a = Player("Alice");
        await Move(alice, a, 10100);
        await OnLoop(() =>
        {
            Service<SceneService>().Leaving.Add(s => s == a ? Task.FromException(new IOException("controlled leave failure")) : Task.CompletedTask);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => OnLoop(() => Service<SceneService>().EnterSceneAsync(a, 10100, createLine: true)));
        Assert.Null(Service<PublicLineManager>().Reserved(a)); Assert.True(a.SceneRecoveryRequired);
    }
}
