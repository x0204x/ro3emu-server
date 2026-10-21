using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>MatchConfig type 2 protocol through the loopback game gate, ready vote and ordinary dungeon handoff.</summary>
public sealed class DungeonMatchTests : IAsyncLifetime
{
    private const int Entry = 100, Multi = 50104;
    private const long Scene = 3120401;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-dungeonmatch-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(2)));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.SeasonStart = "2026-10-08";
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private static StartMatchReq Request(long scene = Scene, int multi = Multi, int mark = 1) => new()
    {
        MatchEntryId = Entry,
        MatchData = new MatchData
        {
            MultiDungeon = new MultiDungeonMatchData
            { DungeonId = scene, MultiDungeonId = multi, MarkId = mark }
        },
    };

    private async Task<StartMatchResp> Start(BotClient bot, StartMatchReq? req = null, int expected = 0)
    {
        await bot.SendGameAsync(DungeonMatchService.StartMatch, req ?? Request());
        var response = await Next(bot, DungeonMatchService.StartMatch, StartMatchResp.Parser);
        Assert.Equal(expected, response.Result);
        return response;
    }

    private async Task<long> Party(BotClient leader, params BotClient[] others)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in others)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
            { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
        return created.TeamId;
    }

    private static async Task<long> Load(BotClient bot, long? expectedTeam = null)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == Scene);
        Assert.NotEqual(sw.SceneEntry, sw.SceneID);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == Scene)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        var info = await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        Assert.True(info.InitData.MultiDungeonInitData.IsMatch);
        Assert.Equal(Multi, info.InitData.MultiDungeonInitData.MultiDungeonId);
        Assert.Equal(sw.SceneID, info.InitData.MultiDungeonInitData.DungeonId);
        if (expectedTeam != null) Assert.Equal(expectedTeam.Value, info.InitData.MultiDungeonInitData.TeamID);
        return sw.SceneID;
    }

    [Fact]
    public void PartyPackingCanSkipAnEarlyIncompatibleGroup()
    {
        Assert.Equal(new[] { 1, 2 }, DungeonMatchService.PickGroupIndices([4, 3, 2], 5));
        Assert.Equal(new[] { 0, 1 }, DungeonMatchService.PickGroupIndices([2, 3, 1], 5));
    }

    [ClientDataFact]
    public async Task SelectionCancellationSoloVoteDeclineAndDeadline()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Start(alice, Request(scene: Scene + 1), ErrorCodes.DungeonDataMissing);
        await Start(alice, Request(mark: 99), ErrorCodes.DungeonDataMissing);
        await Start(alice);
        Assert.True(Service<DungeonMatchService>().Owns(Session(alice)));
        await alice.SendGameAsync(DungeonMatchService.CancelMatch, new CancelMatchReq { MatchEntryId = Entry });
        Assert.Equal(0, (await Next(alice, DungeonMatchService.CancelMatch, CancelMatchResp.Parser)).Result);
        Assert.True((await Next(alice, DungeonMatchService.CancelNotify, CancelMatchNotify.Parser)).ManualCancel);
        Assert.False(Service<DungeonMatchService>().Owns(Session(alice)));

        long soloParty = await Party(alice); // GM solo keeps an existing one-member party's real identity.
        await Start(alice);
        await Gm(alice, "!dungeonsolo off");
        Assert.False(Session(alice).DungeonSoloMatch);
        Assert.False((await Next(alice, DungeonMatchService.CancelNotify, CancelMatchNotify.Parser)).ManualCancel);
        Assert.False(Service<DungeonMatchService>().Owns(Session(alice)));
        await Gm(alice, "!dungeonsolo on");
        await Start(alice);
        var ready = await Next(alice, DungeonMatchService.ReadyNotify, MatchReadyNotify.Parser, n => n.ReadyInfos.Count == 1);
        Assert.Equal(Scene, ready.DungeonId);
        Assert.Equal(Multi, ready.MultiDungeonId);
        Assert.Equal(1, ready.MarkId);
        Assert.Equal((long)alice.Self!.Uuid, ready.ReadyInfos[0].PlayerId);
        await alice.SendGameAsync(DungeonMatchService.Ready, new MatchReadyReq
        { MatchEntryId = Entry, PlayerId = (long)alice.Self.Uuid, IsReady = false });
        Assert.Equal(ErrorCodes.DungeonDeclined,
            (await Next(alice, DungeonMatchService.Ready, MatchReadyResp.Parser)).Result);
        // A refusal fails the match naming the refuser (82643), not a plain cancellation (82639).
        var refused = await Next(alice, DungeonMatchService.FailNotify, FailMatchNtf.Parser);
        Assert.Equal(ErrorCodes.DungeonDeclined, refused.Result);
        Assert.Equal(Entry, refused.MatchEntryId);
        Assert.Equal((long)alice.Self.Uuid, refused.PenaltyMemberGuid);
        Assert.Equal((long)alice.Self.Uuid, refused.FailPlayer);
        Assert.Equal(0, refused.PenaltyMemberUnlockTs);
        Assert.Equal(Request().MatchData, refused.MatchData);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), refused.MatchStartTime);
        Assert.Equal(0, Session(alice).InstanceId);
        Assert.False(Service<DungeonMatchService>().Owns(Session(alice)));

        _clock.Advance(TimeSpan.FromMilliseconds(250));
        await Start(alice);
        var expiring = await Next(alice, DungeonMatchService.ReadyNotify, MatchReadyNotify.Parser,
            n => n.ReadyInfos.Count == 1);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds() + 31, expiring.EndTime);
        _clock.Set(DateTimeOffset.FromUnixTimeSeconds(expiring.EndTime));
        await alice.SendGameAsync(DungeonMatchService.Ready, new MatchReadyReq
        { MatchEntryId = Entry, PlayerId = (long)alice.Self.Uuid, IsReady = true });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, DungeonMatchService.Ready, MatchReadyResp.Parser)).Result);
        // The unconfirmed member let the ready check run out.
        Assert.Equal((long)alice.Self.Uuid, (await Next(alice, DungeonMatchService.FailNotify, FailMatchNtf.Parser)).PenaltyMemberGuid);
        Assert.False(Service<DungeonMatchService>().Owns(Session(alice)));

        _clock.Advance(TimeSpan.FromMilliseconds(250));
        await Start(alice);
        var accepted = await Next(alice, DungeonMatchService.ReadyNotify, MatchReadyNotify.Parser,
            n => n.ReadyInfos.Count == 1);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds() + 31, accepted.EndTime);
        _clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(accepted.EndTime * 1000 - 1));
        await alice.SendGameAsync(DungeonMatchService.Ready, new MatchReadyReq
        { MatchEntryId = Entry, PlayerId = (long)alice.Self.Uuid, IsReady = true });
        Assert.Equal(0, (await Next(alice, DungeonMatchService.Ready, MatchReadyResp.Parser)).Result);
        long instance = await Load(alice, soloParty);
        Assert.Equal(instance, Session(alice).InstanceId);
        Assert.False(Service<DungeonMatchService>().Owns(Session(alice)));
    }

    [ClientDataFact]
    public async Task TwoAndThreePersonPartiesMatchReadyAndEnterOneInstance()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dan = await InWorld("Dan");
        await using var erin = await InWorld("Erin");
        var members = new[] { alice, bob, carol, dan, erin };
        foreach (var bot in members) await Gm(bot, "!lvl 99");
        await Party(alice, bob);
        await Party(carol, dan, erin);
        var started = await Start(alice);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), started.MatchStartTime);
        Assert.True(Service<DungeonMatchService>().Owns(Session(bob)));
        await Start(carol);
        long deadline = _clock.GetUtcNow().ToUnixTimeSeconds() + 30;
        foreach (var bot in members)
        {
            var ready = await Next(bot, DungeonMatchService.ReadyNotify, MatchReadyNotify.Parser,
                n => n.ReadyInfos.Count == 5);
            Assert.Equal(5, ready.ReadyInfos.Select(r => r.PlayerId).Distinct().Count());
            Assert.Equal(deadline, ready.EndTime);
        }
        Assert.All(members, bot => Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(MsgId.SwitchSceneLine)
            && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == Scene));
        foreach (var bot in members)
        {
            await bot.SendGameAsync(DungeonMatchService.Ready, new MatchReadyReq
            { MatchEntryId = Entry, PlayerId = (long)bot.Self!.Uuid, IsReady = true });
            Assert.Equal(0, (await Next(bot, DungeonMatchService.Ready, MatchReadyResp.Parser)).Result);
            if (bot == alice)
            {
                var update = await Next(bob, DungeonMatchService.ReadyNotify, MatchReadyNotify.Parser,
                    n => n.ReadyInfos.Count == 5 && n.ReadyInfos.Any(r => r.PlayerId == (long)alice.Self!.Uuid
                        && r.Status == MatchReadyStatus.Ready));
                Assert.Equal(deadline, update.EndTime);
            }
        }
        long instance = await Load(alice);
        foreach (var bot in members.Skip(1)) Assert.Equal(instance, await Load(bot));
        var teamIds = members.Select(bot => Service<TeamService>().TeamOf((long)bot.Self!.Uuid).TeamId).Distinct();
        Assert.Single(teamIds);
        Assert.NotEqual(0, teamIds.Single());
    }

    [ClientDataFact]
    public async Task ChickenRoutingCrossOwnerExclusionSwitchAndRoleCleanup()
    {
        // Inside the exported Kafra window (activity 114); select it before role login.
        _clock.Set(DateTimeOffset.FromUnixTimeSeconds(
            ScheduleRows.Weekly(114, new DateTime(2026, 11, 10), _clock.LocalTimeZone).Start + 300));
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.True(Service<TimedTodoSchedule>().IsOpen(114));
        await alice.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
        var firstTimed = await Next(alice, TimedBattleService.StartMatch, StartMatchResp.Parser,
            r => r.MatchEntryId == 2);
        Assert.Equal(0, firstTimed.Result);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), firstTimed.MatchStartTime);
        Assert.True(Service<TimedBattleService>().IsMatching(Session(alice)));
        await Start(alice, expected: ErrorCodes.DungeonBusy);
        await alice.SendGameAsync(TimedBattleService.CancelMatch, new CancelMatchReq
        { MatchEntryId = 2, NextMatchEntry = Entry, NextMatchData = Request().MatchData });
        Assert.Equal(0, (await Next(alice, TimedBattleService.CancelMatch, CancelMatchResp.Parser, r => r.MatchEntryId == 2)).Result);
        Assert.Equal(0, (await Next(alice, DungeonMatchService.StartMatch, StartMatchResp.Parser,
            r => r.MatchEntryId == Entry)).Result);
        Assert.True(Service<DungeonMatchService>().Owns(Session(alice)));
        Assert.False(Service<TimedBattleService>().IsMatching(Session(alice)));
        await alice.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
        Assert.Equal(ErrorCodes.DungeonBusy,
            (await Next(alice, TimedBattleService.StartMatch, StartMatchResp.Parser, r => r.MatchEntryId == 2)).Result);
        await alice.SendGameAsync(DungeonMatchService.CancelMatch, new CancelMatchReq
        { MatchEntryId = Entry, NextMatchEntry = 2 });
        Assert.Equal(0, (await Next(alice, DungeonMatchService.CancelMatch, CancelMatchResp.Parser)).Result);
        var switchedTimed = await Next(alice, TimedBattleService.StartMatch, StartMatchResp.Parser,
            r => r.MatchEntryId == 2);
        Assert.Equal(0, switchedTimed.Result);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), switchedTimed.MatchStartTime);
        Assert.False(Service<DungeonMatchService>().Owns(Session(alice)));
        Assert.True(Service<TimedBattleService>().IsMatching(Session(alice)));
        await alice.SendGameAsync(TimedBattleService.CancelMatch, new CancelMatchReq { MatchEntryId = 2 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.CancelMatch, CancelMatchResp.Parser, r => r.MatchEntryId == 2)).Result);

        await Start(alice);
        var role = (long)alice.Self!.Uuid;
        var original = Session(alice);
        await alice.SwitchRoleAsync();
        Assert.False(Service<DungeonMatchService>().Owns(original));
        await alice.EnterWorldAsync(role);
        Assert.False(Session(alice).DungeonSoloMatch);
        await Start(alice);
        Assert.True(Service<DungeonMatchService>().Owns(Session(alice)));
        await alice.SendGameAsync(DungeonMatchService.CancelMatch, new CancelMatchReq
        { MatchEntryId = Entry, NextMatchEntry = Entry, NextMatchData = Request().MatchData });
        Assert.Equal(0, (await Next(alice, DungeonMatchService.CancelMatch, CancelMatchResp.Parser)).Result);
        Assert.Equal(0, (await Next(alice, DungeonMatchService.StartMatch, StartMatchResp.Parser,
            r => r.MatchEntryId == Entry)).Result);
        Assert.True(Service<DungeonMatchService>().Owns(Session(alice)));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Advance(TimeSpan by) => Now = Now.Add(by);
        public void Set(DateTimeOffset value) => Now = value;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
