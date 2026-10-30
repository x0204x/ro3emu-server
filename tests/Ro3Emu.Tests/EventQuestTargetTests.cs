using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The dungeon-event and Territory War quest targets of issue 179: the dungeon task trackers (210005, 200003, 210004,
/// 200000; the siege's 200001 and 210003, TaskService.DungeonTasks.cs), the siege's assists, and the season activity
/// targets 106609 (guild Territory War points) and 107101 (Genesis Movement completion).
/// </summary>
public sealed class EventQuestTargetTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-event-quests-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    // The exported siege occurrence (DailyActivityConfig 115) in the week of 12 Oct 2026, as SiegeTests.
    private readonly ScheduleRows.Window _siege = ScheduleRows.Weekly(115, new DateTime(2026, 10, 12), Zone);
    private readonly Clock _clock;
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class Clock(long start) : TimeProvider
    {
        private long _seconds = start;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => Zone;
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }

    public EventQuestTargetTests() => _clock = new(_siege.Start + 25);

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game; o.PlayBook = 101; o.SeasonStart = "2026-05-19";
            o.GmAccounts = ["eventgm", "eventseason"];
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var p = new DirectoryInfo(AppContext.BaseDirectory); p != null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "Ro3Emu.slnx"))) return p.FullName;
        throw new DirectoryNotFoundException();
    }

    private async Task<BotClient> Bot(string name)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        await b.EnterWorldAsync((await b.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return b;
    }

    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var envelope = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(envelope.Body);
    }

    private async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await Read(condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    /// <summary>The tracker's entry for the session: task id and counters, (0, []) when none.</summary>
    private (long Task, long[] Counts) Tracker(GameSession s) =>
        Service<TaskService>().DungeonRunTasks(s).SingleOrDefault() is { } info ? (info.TaskId, [.. info.CompleteNum]) : (0, []);

    private async Task Shows(GameSession s, long task, params long[] counts) =>
        await Until(() => Tracker(s) is var (shown, now) && shown == task && now.SequenceEqual(counts),
            $"tracker shows {task} [{string.Join(',', counts)}]");

    [ClientDataFact]
    public void EachDungeonsTasksFormOneChainInPreTaskOrder()
    {
        var tasks = Service<TaskService>();
        long[] lair = [9000100, 9000101, 9000102, 9000103, 9000104, 9000105, 9000106, 9000107, 9000108, 9000109, 9000110];
        long[] clouds = [9000002, 9000013, 9000014, 9000015, 9000016, 9000017, 9000018, 9000019];
        long[] siege = [4900000, 4900020, 4900021, 4900022, 4900023, 4900024];
        Assert.Equal(lair, tasks.DungeonTaskChain(3010401)); // Dracula's Lair, 210005 each
        Assert.Equal(clouds, tasks.DungeonTaskChain(3011108)); // Above the Sea of Clouds: 200003, 210005, 210004
        Assert.Equal(siege, tasks.DungeonTaskChain(2230101)); // the siege: 200000, 210003, 200001
        Assert.Equal(9000020, Assert.Single(tasks.DungeonTaskChain(3011112)));
        Assert.Empty(tasks.DungeonTaskChain(1010001)); // the Adventure Group ritual keeps its own quests
    }

    [ClientDataFact]
    public async Task ARunShowsItsCurrentStepAndANewInstanceStartsOver()
    {
        await using var bot = await Bot("EventLair");
        var s = Session(bot);
        var tasks = Service<TaskService>();
        await OnLoop(() => { s.InstanceId = 9001; tasks.OnDungeonRun(s, 3010401, 9001, 0); return Task.CompletedTask; });
        await Shows(s, 9000100, 0);
        await OnLoop(() => { tasks.OnDungeonRun(s, 3010401, 9001, 3); return Task.CompletedTask; });
        await Shows(s, 9000103, 0); // three waves passed: "Defeat Wraith."
        await OnLoop(() => { tasks.OnDungeonRun(s, 3010401, 9001, 1); return Task.CompletedTask; });
        await Shows(s, 9000103, 0); // a run never goes back
        await OnLoop(() => { tasks.OnDungeonRun(s, 3010401, 9001, TaskService.RunOver); return Task.CompletedTask; });
        await Until(() => Tracker(s).Task == 0, "a settled run shows nothing");

        // A Guild Challenge stage is at its last step, the boss; leaving the instance drops it.
        await OnLoop(() => { s.InstanceId = 9002; tasks.OnDungeonRun(s, 3011108, 9002, TaskService.FinalStep); return Task.CompletedTask; });
        await Shows(s, 9000019, 0);
        await OnLoop(() => { s.InstanceId = 0; return Task.CompletedTask; });
        Assert.Equal(0, (await Read(() => Tracker(s))).Task);

        await OnLoop(() => { s.InstanceId = 9003; tasks.OnDungeonRun(s, 3010401, 9003, 0); return Task.CompletedTask; });
        await Shows(s, 9000100, 0);
        await OnLoop(() => { s.InstanceId = 0; return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task TheSiegeChainFollowsAssistsAndScore()
    {
        await using var bot = await Bot("EventSiege");
        var s = Session(bot);
        var tasks = Service<TaskService>();
        long score = 0, assists = 0;
        tasks.DungeonRunValue = (_, target) => target == 200001 ? score : assists;
        await OnLoop(() => { s.InstanceId = 9101; tasks.OnDungeonRun(s, 2230101, 9101, 0); return Task.CompletedTask; });
        await Shows(s, 4900000, 0); // "Waiting for the battle to start"
        await OnLoop(() => { assists = 1; tasks.OnDungeonRun(s, 2230101, 9101, 1); return Task.CompletedTask; });
        await Shows(s, 4900020, 1); // [210003, 2]
        await OnLoop(() => { assists = 2; score = 450; return Task.CompletedTask; });
        await Shows(s, 4900023, 450); // 100 and the C rating 400 are met, B is 800
        await OnLoop(() => { score = 5000; return Task.CompletedTask; });
        await Until(() => Tracker(s).Task == 0, "the A rating ends the chain");
        await OnLoop(() => { s.InstanceId = 0; return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ASiegeKillGivesTheOtherRecentHittersAnAssist()
    {
        await using var attack = await Bot("EventGm");
        await using var helper = await Bot("EventHelper");
        await using var defense = await Bot("EventDef");
        long ga = await Guild(attack, "EvAtk"), gd = await Guild(defense, "EvDef");
        await Join(attack, helper, ga);
        await attack.JoinChatAsync("4_1");
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Enter(attack, 1); await Enter(helper, 1); await Enter(defense, 2);
        var wars = Service<TerritoryWarsService>();
        GameSession a = Session(attack), h = Session(helper), d = Session(defense);
        await Shows(h, 4900000, 0); // loaded in Prepare: "Reach Dungeon Stage 1" waits for the Gate stage
        _clock.Set(_siege.Start + 300);
        await OnLoop(wars.ReconcileAsync);
        await Shows(h, 4900020, 0); // the Gate stage passed it: the assists step shows
        await OnLoop(async () =>
        {
            foreach (var handler in Service<CombatService>().PvpDealtAsync) await handler(h, d, 10, false);
            foreach (var handler in Service<CombatService>().PvpDealtAsync) await handler(a, d, 10, true);
        });
        Assert.Equal(1, await Read(() => wars.SiegeTrackerValue(h, 210003)));
        Assert.Equal(0, await Read(() => wars.SiegeTrackerValue(a, 210003))); // the killer is no assist
        Assert.Equal(1, await Read(() => wars.SiegeTrackerValue(a, 200001)));
        await Shows(h, 4900020, 1);
    }

    [ClientDataFact]
    public async Task GuildWarPointsAndMovementCompletionFeedTheSeasonTasks()
    {
        await using var bot = await Bot("EventSeason");
        var s = Session(bot);
        var tasks = Service<TaskService>();
        tasks.GuildWarPoints = _ => 12000;
        await OnLoop(() =>
        {
            s.NewActivities ??= new DBNewActivityInfo();
            var world = new DBWorldExploration();
            foreach (var (phase, rate) in new[] { (101L, 10000), (102L, 5050), (103L, 0) })
                world.PhaseInfos[phase] = new DBWorldExplorationPhase { PhaseCfgId = phase, TotalRate = rate };
            s.NewActivities.ActivityInfo[111] = new PlayerActivityInfo { ActivityParam = new PlayerActivityParam { WorldExploration = world } };
            return Task.CompletedTask;
        });
        await QuestProbe.GmAsync(_http, "eventseason", "!quest accept 120006");
        await QuestProbe.GmAsync(_http, "eventseason", "!quest accept 120008");
        await QuestProbe.GmAsync(_http, "eventseason", "!quest accept 2101004");
        await OnLoop(() => tasks.RefreshAsync(s));
        await QuestProbe.DoneAsync(_app, "eventseason", 120006); // [106609, 10000]
        await QuestProbe.AtAsync(_app, "eventseason", 120008, 12000); // [106609, 20000]
        var movements = await QuestProbe.WaitAsync(_app, "eventseason", 2101004, i => i.CompleteNum.Count == 3, "three movements");
        long[] percent = [100, 50, 0];
        Assert.Equal(percent, movements.CompleteNum);
    }

    private async Task<long> Guild(BotClient bot, string name)
    {
        await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(bot), 99, null));
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq { Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, g => g.Guid > 0)).Guid;
    }

    private async Task Join(BotClient leader, BotClient member, long guild)
    {
        await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(member), 99, null));
        await leader.SendGameAsync(GuildService.Invite, new GuildInviteReq { TarPlayerID = { Session(member).Role!.Id } });
        var invite = await Next(member, GuildService.InviteNtf, GuildInviteNtf.Parser);
        await member.SendGameAsync(GuildService.InviteAck, new GuildInviteAckReq { GuildID = guild, Token = invite.Token, Accept = true });
        Assert.Equal(0, (await Next(member, GuildService.InviteAck, GuildInviteAckResp.Parser)).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
    }

    private async Task Enter(BotClient bot, int expectedCamp)
    {
        await bot.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = 2230101 });
        Assert.Equal(0, (await Next(bot, 30400, EnterDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 2230101);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 2230101);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser,
            e => e.Base.Uuid == (ulong)Session(bot).Role!.Id && e.Base.EntityCamp == expectedCamp);
        await Next(bot, 30401, DungeonBaseInfoNotify.Parser, i => i.DungeonEntry == 2230101);
    }
}
