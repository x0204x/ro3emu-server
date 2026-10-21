using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>32256 GUILD_ENTER_HOME, one room per guild, exit and ejection, campfire on top.</summary>
public sealed class GuildHomeTests : IAsyncLifetime
{
    private const long Home = GuildHomeService.DefaultScene;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-guildhome-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    /// <summary>Thursday 2026-10-08 12:00 UTC: a Bonfire Party day, outside the 20:00-20:25 event.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(long count) => Interlocked.Add(ref _seconds, count);
        public void Set(DateTimeOffset at) => Interlocked.Exchange(ref _seconds, at.ToUnixTimeSeconds());
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Ro3Emu.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    private async Task<BotClient> InWorld(string name, bool create = true, int level = 40)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        if (create && level > 0) await Gm(bot, $"!lvl {level}");
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>()
        .All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            bool done = false;
            await OnLoop(() => { done = condition(); return Task.CompletedTask; });
            if (done) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    private async Task<long> CreateGuild(BotClient founder, string name)
    {
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
    }

    private static async Task Join(BotClient leader, BotClient member, long guild)
    {
        await leader.SendGameAsync(GuildService.Invite, new GuildInviteReq { TarPlayerID = { (long)member.Self!.Uuid } });
        var invite = await Next(member, GuildService.InviteNtf, GuildInviteNtf.Parser);
        await member.SendGameAsync(GuildService.InviteAck, new GuildInviteAckReq { GuildID = guild, Token = invite.Token, Accept = true });
        Assert.Equal(0, (await Next(member, GuildService.InviteAck, GuildInviteAckResp.Parser)).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
    }

    private static async Task<GuildEnterHomeResp> Request(BotClient bot)
    {
        await bot.SendGameAsync(GuildHomeService.EnterHome, new GuildEnterHomeReq());
        return await Next(bot, GuildHomeService.EnterHome, GuildEnterHomeResp.Parser);
    }

    private static async Task<EntitySelf> Load(BotClient bot, long scene)
    {
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == scene)).Result);
        return await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    /// <summary>32256 success: reply, 70010 into the room (reason 3), 30005, ENTITYSELF at the born point.</summary>
    private static async Task<long> Enter(BotClient bot)
    {
        var reply = await Request(bot);
        Assert.Equal(0, reply.Result);
        Assert.NotEqual(0, reply.GuildSceneId);
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Home);
        Assert.Equal(reply.GuildSceneId, change.SceneID);
        Assert.Equal(3, (int)change.Reason);
        var self = await Load(bot, Home);
        Assert.Equal(397f, self.Base.MovementInfo.Pos.X, 1f);
        Assert.Equal(412.02f, self.Base.MovementInfo.Pos.Z, 1f);
        return reply.GuildSceneId;
    }

    private static int HomeSwitches(BotClient bot) => bot.Inbox.Snapshot()
        .Count(e => e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == Home);

    private async Task<SwitchSceneLineResp> Leave(BotClient bot, long outside)
    {
        await bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        return await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == outside);
    }

    [ClientDataFact]
    public async Task NoGuildAndClosedGuildFunctionAreRefusedWithoutSceneChange()
    {
        await using var alice = await InWorld("Alice");
        var refused = await Request(alice);
        Assert.Equal(ErrorCodes.NotInGuild, refused.Result);
        Assert.Equal(0, refused.GuildSceneId);

        await CreateGuild(alice, "Hearth");
        await Gm(alice, "!lvl 20"); // function 13 (Guild) needs condition 2000010, Lv21
        refused = await Request(alice);
        Assert.Equal(ErrorCodes.FeatureUnavailable, refused.Result);
        Assert.Equal(0, refused.GuildSceneId);
        await Task.Delay(150);
        Assert.Equal(0, HomeSwitches(alice));
        Assert.Equal(0, Session(alice).InstanceId);
    }

    [ClientDataFact]
    public async Task GuildmatesShareOneRoomOtherGuildsGetTheirOwnAndLeavingClosesIt()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long hearth = await CreateGuild(alice, "Hearth");
        await Join(alice, bob, hearth);
        long other = await CreateGuild(carol, "Others");
        var home = Service<GuildHomeService>();

        long outside = Session(bob).SceneEntry, aliceOutside = Session(alice).SceneEntry;
        var bobBefore = Session(bob).Position.Clone();
        long room = await Enter(alice);
        Assert.Equal(room, home.RoomOf(hearth)!.Id);
        Assert.Equal(hearth, home.GuildOfRoom(room));
        Assert.Equal(room, await Enter(bob));
        await Until(() => Session(alice).Visible.Contains(Session(bob)) && Session(bob).Visible.Contains(Session(alice)),
            "guildmates see each other");

        // The GM tool's 30400 for the home follows the same rules; the !dungeon test aid stays DungeonDataMissing.
        await Gm(carol, $"!dungeon {Home}");
        Assert.Equal(ErrorCodes.DungeonDataMissing, (await Next(carol, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await carol.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = Home });
        Assert.Equal(0, (await Next(carol, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        var carolChange = await Next(carol, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Home);
        await Load(carol, Home);
        long otherRoom = carolChange.SceneID;
        Assert.NotEqual(room, otherRoom);
        Assert.Equal(otherRoom, home.RoomOf(other)!.Id);
        await Task.Delay(100);
        await OnLoop(() =>
        {
            Assert.DoesNotContain(Session(carol), Session(alice).Visible);
            Assert.DoesNotContain(Session(alice), Session(carol).Visible);
            return Task.CompletedTask;
        });

        // Outside the event the home is empty: no campfire stage, finish or NPCs.
        await Task.Delay(150);
        foreach (var bot in new[] { alice, bob, carol })
        {
            Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(GuildCampfireService.Stage) || e.IsGame(GuildCampfireService.Finish));
            Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(GameAoiSink.SyncAoiView)
                && AoIViewChangeData.Parser.ParseFrom(e.Body).Bases.Any(b => b.EntityTemplateID == 7020004));
        }

        // Already inside: 550 and no scene change.
        int switches = HomeSwitches(alice);
        var again = await Request(alice);
        Assert.Equal(ErrorCodes.AlreadyInGuildHome, again.Result);
        Assert.Equal(0, again.GuildSceneId);
        await Task.Delay(150);
        Assert.Equal(switches, HomeSwitches(alice));
        Assert.Equal(room, Session(alice).InstanceId);

        // 30402 returns to the exact pre-entry scene and point; the room survives while a member remains.
        var back = await Leave(bob, outside);
        Assert.Equal(outside, back.SceneEntry);
        var self = await Load(bob, outside);
        Assert.Equal(bobBefore.X, self.Base.MovementInfo.Pos.X, 1f);
        Assert.Equal(bobBefore.Z, self.Base.MovementInfo.Pos.Z, 1f);
        Assert.Equal(0, Session(bob).InstanceId);
        Assert.Equal(room, home.RoomOf(hearth)?.Id);

        await Leave(alice, aliceOutside);
        await Load(alice, aliceOutside);
        await Until(() => home.RoomOf(hearth) == null && Service<InstanceService>().Find(room) == null
            && Service<World>().PlayersIn(room).Count == 0, "the last exit closes the room");
        var drain = Service<GameLoop>().DrainStatus;
        Assert.Empty(drain.FailedObligations);

        // The next entry creates a fresh room.
        long next = await Enter(bob);
        Assert.NotEqual(room, next);
    }

    [ClientDataFact]
    public async Task BusySwitchingDeadAndFightingMembersAreRefused()
    {
        await using var alice = await InWorld("Alice", level: 99);
        await CreateGuild(alice, "Hearth");
        var session = Session(alice);

        // Dead, then in combat.
        await OnLoop(() => { session.Dead = true; return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Request(alice)).Result);
        // A real hit also stamps LastCombatMs; without it CombatService's tick clears the fight bit at its next pass
        // (OutOfCombatMs), which under a loaded suite could land before the request.
        await OnLoop(() =>
        {
            session.Dead = false; session.StatusBits |= 1L << 15; session.LastCombatMs = MonsterService.NowMs;
            return Task.CompletedTask;
        });
        Assert.Equal(ErrorCodes.ActionInBattle, (await Request(alice)).Result);
        await OnLoop(() => { session.StatusBits &= ~(1L << 15); return Task.CompletedTask; });

        // Switching scenes: a teleport has been sent and its 30005 has not arrived.
        long outside = session.SceneEntry;
        long elsewhere = outside == 10101 ? RoleStore.SpawnSceneEntry : 10101;
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(session, elsewhere)));
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == elsewhere);
        var switching = await Request(alice);
        Assert.Equal(ErrorCodes.SwitchingScenes, switching.Result);
        Assert.Equal(0, switching.GuildSceneId);
        await Load(alice, elsewhere);

        // In another private instance (the training ground).
        await Gm(alice, "!job 1100");
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 2100009 });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        var training = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == 2100009);
        await Load(alice, 2100009);
        var busy = await Request(alice);
        Assert.Equal(ErrorCodes.DungeonBusy, busy.Result);
        Assert.Equal(training.SceneID, session.InstanceId);
        Assert.Equal(0, HomeSwitches(alice));
    }

    [ClientDataFact]
    public async Task ConcurrentFirstEntriesOfOneGuildCreateExactlyOneRoom()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await CreateGuild(alice, "Hearth");
        await Join(alice, bob, guild);
        await Task.WhenAll(alice.SendGameAsync(GuildHomeService.EnterHome, new GuildEnterHomeReq()),
            bob.SendGameAsync(GuildHomeService.EnterHome, new GuildEnterHomeReq()));
        var a = await Next(alice, GuildHomeService.EnterHome, GuildEnterHomeResp.Parser);
        var b = await Next(bob, GuildHomeService.EnterHome, GuildEnterHomeResp.Parser);
        Assert.Equal(0, a.Result);
        Assert.Equal(0, b.Result);
        Assert.Equal(a.GuildSceneId, b.GuildSceneId);
        Assert.Single(Service<GuildHomeService>().Rooms);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneID == a.GuildSceneId);
        await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneID == a.GuildSceneId);
        await Load(alice, Home);
        await Load(bob, Home);
    }

    [ClientDataFact]
    public async Task KickedAndLeavingMembersAreEjectedWhileTheRestStay()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long guild = await CreateGuild(alice, "Hearth");
        await Join(alice, bob, guild);
        await Join(alice, carol, guild);
        long outside = Session(bob).SceneEntry;
        long room = await Enter(alice);
        await Enter(bob);
        await Enter(carol);

        await alice.SendGameAsync(GuildService.Kick, new GuildMemberKickReq { TarPlayerIDs = { (long)bob.Self!.Uuid }, IfSingleKick = true });
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser, x => x.Result == 0)).Result);
        _clock.Advance(1);
        await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == outside);
        await Load(bob, outside);
        Assert.Equal(0, Session(bob).InstanceId);
        Assert.DoesNotContain(bob.Inbox.Snapshot(), e => e.IsGame(DungeonService.Leave));

        await carol.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(carol, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        _clock.Advance(1);
        await Next(carol, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == outside);
        await Load(carol, outside);

        Assert.Equal(room, Session(alice).InstanceId);
        Assert.Equal(room, Service<GuildHomeService>().RoomOf(guild)?.Id);
        Assert.Single(Service<GuildHomeService>().RoomOf(guild)!.ActiveMembers);
        Assert.Equal(ErrorCodes.NotInGuild, (await Request(bob)).Result);
    }

    [ClientDataFact]
    public async Task DisconnectInsideLogsBackInOutsideAndClosesTheRoom()
    {
        long guild, room, outside;
        Vec3 before;
        await using (var alice = await InWorld("Alice"))
        {
            guild = await CreateGuild(alice, "Hearth");
            outside = Session(alice).SceneEntry;
            before = Session(alice).Position.Clone();
            room = await Enter(alice);
        }
        await Until(() => Service<GuildHomeService>().RoomOf(guild) == null && Service<InstanceService>().Find(room) == null,
            "a disconnect closes the last member's room");
        await using var again = await InWorld("Alice", create: false);
        var session = Session(again);
        Assert.Equal(outside, session.SceneEntry);
        Assert.Equal(0, session.InstanceId);
        Assert.Equal(before.X, session.Position.X, 1f);
        Assert.Equal(before.Z, session.Position.Z, 1f);
    }

    [ClientDataFact]
    public async Task ExitWhileDeadRevivesOnReturn()
    {
        await using var alice = await InWorld("Alice");
        await CreateGuild(alice, "Hearth");
        long outside = Session(alice).SceneEntry;
        await Enter(alice);
        var session = Session(alice);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true;
        });
        await Leave(alice, outside);
        var self = await Load(alice, outside);
        Assert.Equal(EntityState.Alive, self.Base.State);
        Assert.False(session.Dead);
        Assert.Equal(0, session.InstanceId);
    }

    [ClientDataFact]
    public async Task CampfireRunsInTheLongLivedRoomOnEachEventDay()
    {
        await using var alice = await InWorld("Alice");
        long guild = await CreateGuild(alice, "Hearth");
        long room = await Enter(alice);

        // 20:00: the members already inside get the fire, the seeds and stage 1.
        _clock.Set(new DateTimeOffset(2026, 10, 8, 20, 0, 10, TimeSpan.Zero));
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            r => r.Bases.Any(b => b.EntityTemplateID == 7020004));
        Assert.Contains(view.Bases, b => b.EntityTemplateID == 7020001);
        Assert.Equal(1, (await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser)).NowStage);
        ulong fire = view.Bases.Single(b => b.EntityTemplateID == 7020004).Uuid;

        _clock.Set(new DateTimeOffset(2026, 10, 8, 20, 15, 0, TimeSpan.Zero));
        await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser, r => r.NowStage == 4);
        var firstBoss = await Boss(room);

        // 20:25: Finish, the fire leaves, the event's actors are cleared; the room itself stays.
        _clock.Set(new DateTimeOffset(2026, 10, 8, 20, 25, 0, TimeSpan.Zero));
        await Next(alice, GuildCampfireService.Finish, GuildCampFireFinishNotify.Parser);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, r => r.LeaveAoi.Contains(fire));
        await Until(() => !Service<World>().AllUnits().OfType<MonsterUnit>().Any(m => m.InstanceId == room && !m.Dead),
            "the finished event's actors are removed");
        Assert.Equal(room, Service<GuildHomeService>().RoomOf(guild)?.Id);

        // Saturday's event in the same room spawns its boss again.
        _clock.Set(new DateTimeOffset(2026, 10, 10, 20, 15, 0, TimeSpan.Zero));
        Assert.Equal(4, (await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser, r => r.NowStage == 4)).NowStage);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, r => r.Bases.Any(b => b.EntityTemplateID == 7020004));
        var secondBoss = await Boss(room);
        Assert.NotEqual(firstBoss.Uid, secondBoss.Uid);
        Assert.Empty(Service<GameLoop>().DrainStatus.FailedObligations);
    }

    [ClientDataFact]
    public async Task EnteringAfterTheEventSendsNoFinish()
    {
        await using var alice = await InWorld("Alice");
        await CreateGuild(alice, "Hearth");
        _clock.Set(new DateTimeOffset(2026, 10, 8, 20, 30, 0, TimeSpan.Zero));
        await Enter(alice);
        _clock.Advance(2);
        await Task.Delay(300);
        _clock.Advance(2);
        await Task.Delay(300);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(GuildCampfireService.Finish) || e.IsGame(GuildCampfireService.Stage));
    }

    private async Task<MonsterUnit> Boss(long room)
    {
        MonsterUnit? boss = null;
        await Until(() => (boss = Service<World>().AllUnits().OfType<MonsterUnit>()
            .FirstOrDefault(m => m.InstanceId == room && m.Template.NpcId == 7020012 && !m.Dead)) != null, "campfire boss");
        return boss!;
    }
}
