using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Where pass EXP comes from (NewActivityService.PassPlay): challenges, daily activity, monsters, dungeons, MVPs,
/// story chapters, the party bonus, catching up, the bonus chest past the last level and the free premium track.
/// The server runs as the real realm does: OpenContent, Pass:FreePremium and Pass:Play on.
/// </summary>
public sealed class PassPlayTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-passplay-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly TestClock _clock = new();
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private sealed class TestClock : TimeProvider
    {
        // A Thursday noon, well inside a game day.
        private long _seconds = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void AdvanceDays(int count) => Interlocked.Add(ref _seconds, count * 86400L);
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.OpenContent.Enabled = true;
            o.Pass.FreePremium = true;
            o.Pass.Play = true;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    /// <summary>Posts one progress event and waits until its owned work has run.</summary>
    private async Task Event(BotClient bot, (NewActivityService.PassGoal, long)[] progress, long kills = 0, long dungeons = 0,
        long mvps = 0, long quest = 0)
    {
        long before = await OnLoop(() => Session(bot).ActivityRuntime?.PassPlay?.Sequence ?? 0);
        int unsaved = await OnLoop(() => Session(bot).ActivityRuntime?.PassPlay?.Unsaved ?? 0);
        await OnLoop(() => { Service<NewActivityService>().PassEvent(Session(bot), progress, kills, dungeons, mvps, quest); return 0; });
        // Owned work runs after this barrier; a second barrier lets a nested commit finish.
        for (int i = 0; i < 500; i++) // 10 s under suite load
        {
            var (sequence, now) = await OnLoop(() => (Session(bot).ActivityRuntime?.PassPlay?.Sequence ?? 0,
                Session(bot).ActivityRuntime?.PassPlay?.Unsaved ?? 0));
            if (sequence != before || now != unsaved) return;
            await Task.Delay(20);
        }
    }

    private Task<long> Currency(BotClient bot, int entry) => OnLoop(() => Session(bot).Progress!.Currency.GetValueOrDefault(entry));
    private Task<PassPlayState> Play(BotClient bot) => OnLoop(() => Session(bot).ActivityRuntime!.PassPlay!);
    private Task<PlayerActivityBattlePass> Pass(BotClient bot) =>
        OnLoop(() => Session(bot).NewActivities!.ActivityInfo[200].ActivityParam.BattlePass);

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task Party(BotClient leader, BotClient member)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
        Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
        Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
    }

    private async Task Until(Func<Task<bool>> condition, string what)
    {
        for (int i = 0; i < 750; i++) { if (await condition()) return; await Task.Delay(20); } // 15 s under suite load
        Assert.Fail("timed out: " + what);
    }

    [ClientDataFact]
    public async Task PremiumTrackIsFreeAndDailyActivityPaysPassExpAndProgress()
    {
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        var pass = await Pass(alice);
        Assert.True(pass.Premium); // no item 703 spent
        Assert.Equal(0, await Currency(alice, 703));

        await Gm(alice, "!item 5 60"); // 60 daily activity points
        await Until(async () => await Currency(alice, NewActivityService.PassExpCurrency) > 0, "pass EXP from daily activity");
        var play = await Play(alice);
        Assert.Equal(60, play.WeeklyProgress["w.activity"]);
        // 60 points x 6, doubled while behind the season pace; the daily challenge pays 300 more (also doubled) when drawn.
        long expected = (60 * 6 + (play.Daily.Contains("d.activity") ? 300 : 0)) * 2;
        Assert.Equal(expected, await Currency(alice, NewActivityService.PassExpCurrency));
        Assert.Equal(expected, play.Earned);
        pass = await Pass(alice);
        Assert.Equal(expected, pass.Level * 2000L + pass.Exp);
    }

    [ClientDataFact]
    public async Task ChallengesCapsChaptersAndPeriodsFollowTheDesign()
    {
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, []);
        var play = await Play(alice);
        Assert.Equal(3, play.Daily.Count);
        Assert.Equal(NewActivityService.DailyFor(Session(alice).Role!.Id, play.Day).Select(c => c.Id), play.Daily);

        // Monsters pay 3 each up to 300 a day; 120 kills complete "Defeat 100 monsters" both as the daily (if drawn)
        // and towards the weekly 1000.
        await Event(alice, [(NewActivityService.PassGoal.Kills, 120)], kills: 120);
        play = await Play(alice);
        Assert.Equal(300, play.KillExpToday);
        Assert.Equal(120, play.WeeklyProgress["w.kills"]);
        Assert.Equal(play.Daily.Contains("d.kills"), play.DailyDone.Contains("d.kills"));

        // Dungeons pay 200 up to five a day.
        for (int i = 0; i < 6; i++) await Event(alice, [(NewActivityService.PassGoal.Dungeons, 1)], dungeons: 1);
        play = await Play(alice);
        Assert.Equal(5, play.DungeonsToday);
        Assert.Equal(5, play.WeeklyProgress["w.dungeons"]);
        Assert.Contains("w.dungeons", play.WeeklyDone);

        // A chapter's last main quest pays 500 once.
        long before = play.Earned;
        await Event(alice, [(NewActivityService.PassGoal.Quests, 1)], quest: 10101251);
        await Event(alice, [(NewActivityService.PassGoal.Quests, 1)], quest: 10101251);
        play = await Play(alice);
        Assert.Equal([10101251L], play.ChaptersPaid);
        Assert.Equal(before + 500, play.Earned); // ahead of the first day's pace: no catch-up double

        // The next game day draws a new set and resets the day's caps; the week keeps going.
        _clock.AdvanceDays(1);
        await Event(alice, []);
        play = await Play(alice);
        Assert.Equal(0, play.KillExpToday);
        Assert.Equal(0, play.DungeonsToday);
        Assert.Empty(play.DailyDone);
        Assert.Equal(120, play.WeeklyProgress["w.kills"]);
        Assert.Equal(2, play.WeeklyProgress["w.days"]);

        // Monday 05:00 starts a new week.
        _clock.AdvanceDays(4);
        await Event(alice, []);
        play = await Play(alice);
        Assert.False(play.WeeklyProgress.ContainsKey("w.kills"));
        Assert.Empty(play.WeeklyDone);
        Assert.Equal(1, play.WeeklyProgress["w.days"]);

        // Everything granted is pass currency the pass itself levelled from.
        long balance = await Currency(alice, NewActivityService.PassExpCurrency);
        Assert.Equal((await Play(alice)).Earned, balance);
        var pass = await Pass(alice);
        Assert.Equal(balance, pass.Level * 2000L + pass.Exp);

        // Saved with the role.
        var saved = Session(alice).ActivityRuntime!.PassPlay!;
        await alice.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        var reloaded = await Play(again);
        Assert.Equal(saved.Earned, reloaded.Earned);
        Assert.Equal(saved.ChaptersPaid, reloaded.ChaptersPaid);
    }

    [ClientDataFact]
    public async Task PartyPlaysAtPlusTwentyPercentAndPastTheLastLevelEarnsChests()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        // Ahead of the pace (no catch-up double): 60 levels done.
        await Gm(alice, "!item 132 120000");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        Assert.Equal(60, (await Pass(alice)).Level);
        await OnLoop(() =>
        {
            var p = Session(alice).ActivityRuntime!.PassPlay ??= new(); p.Earned = 120000;
            p.Instance = Session(alice).NewActivities!.ActivityInfo[200].ActivityInstance; return 0;
        });
        // Today's draw would add a daily challenge to some steps; this test checks the sources without it.
        await Event(alice, []);
        await OnLoop(() => { Session(alice).ActivityRuntime!.PassPlay!.Daily.Clear(); return 0; });

        await Party(alice, bob);
        Assert.Equal(2, await OnLoop(() => Service<TeamService>().MembersOf(Session(alice).Role!.Id).Count));

        long items = await OnLoop(() => Session(alice).Items.Sum(i => (long)i.Stack));
        await Event(alice, [(NewActivityService.PassGoal.Dungeons, 1)], dungeons: 1);
        var play = await Play(alice);
        Assert.Equal(120000 + 240, play.Earned); // 200 x 1.2
        Assert.Equal(240, play.Overflow);
        // MVP fights pay 200 each up to three a day; the third also completes the weekly "Take part in 3 MVP fights" (1000).
        for (int i = 0; i < 8; i++) await Event(alice, [(NewActivityService.PassGoal.Mvps, 1)], mvps: 1);
        play = await Play(alice);
        Assert.Equal(3, play.MvpsToday);
        Assert.Contains("w.mvps", play.WeeklyDone);
        Assert.Equal(120000 + 240 + 3 * 240 + 1200, play.Earned);
        // 2160 past the last level: one chest, 160 carried over.
        Assert.Equal(160, play.Overflow);
        Assert.True(await OnLoop(() => Session(alice).Items.Sum(i => (long)i.Stack)) > items, "the chest's reward arrived");
        await Event(alice, [(NewActivityService.PassGoal.Kills, 100)], kills: 100);
        play = await Play(alice);
        Assert.Equal(160 + 360, play.Overflow); // 300 x 1.2
    }

    [ClientDataFact]
    public async Task PassTwoOpensOnlyOncePassOneIsDoneAndStartsFromZero()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99"); // Pass II's own condition
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        Task<string> State(long id) => OnLoop(() => Session(alice).NewActivities!.ActivityInfo[id].State);
        Task<PlayerActivityBattlePass> Second() => OnLoop(() => Session(alice).NewActivities!.ActivityInfo[201].ActivityParam.BattlePass);
        Assert.NotEqual("_close", await State(200));
        Assert.Equal("_close", await State(201)); // Pass I is not done: both would fill from the same currency

        await Gm(alice, "!item 132 120000");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        Assert.Equal(60, (await Pass(alice)).Level);
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        Assert.NotEqual("_close", await State(201));
        var second = await Second();
        Assert.Equal(0, second.Level * 2000L + second.Exp); // the 120000 already went into Pass I

        // New pass EXP goes into Pass II, not into Pass I's bonus chests.
        await Gm(alice, "!item 5 60");
        await Until(async () => { var p = await Second(); return p.Level * 2000L + p.Exp > 0; }, "Pass II EXP");
        Assert.Equal(0, (await Play(alice)).Overflow);
        Assert.Equal(60, (await Pass(alice)).Level);
    }

    [ClientDataFact]
    public async Task ChallengesReach5027OnlyAfterTheClientAsksAndShowProgress()
    {
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, []);
        Task<long[]> Synced() => OnLoop(() => ((DBTaskInfo)Session(alice).Synced[TaskService.TaskInfoSync]).Infos.Keys
            .Where(id => id >= NewActivityService.DailyTaskBase && id < NewActivityService.DailyTaskBase + 1000).Order().ToArray());
        await Event(alice, [(NewActivityService.PassGoal.Kills, 5)], kills: 5);
        Assert.Empty(await Synced()); // a client without the rows would throw on these ids

        await alice.SendGameBytesAsync(NewActivityService.PassPanelMsg, System.Text.Encoding.UTF8.GetBytes("open"));
        await Until(async () => (await Synced()).Length == 10, "the challenge rows in 5027");
        var play = await Play(alice);
        long[] expected = [.. NewActivityService.DailyChallenges.Where(c => play.Daily.Contains(c.Id)).Select(NewActivityService.TaskIdOf),
            .. NewActivityService.WeeklyChallenges.Select(NewActivityService.TaskIdOf)];
        Assert.Equal(expected.Order(), await Synced());
        await Event(alice, [(NewActivityService.PassGoal.Kills, 100)], kills: 100);
        // Event returns on the pass commit; the 5027 push (SyncTasksAsync) comes after it in the same detached operation.
        await Until(() => OnLoop(() => ((DBTaskInfo)Session(alice).Synced[TaskService.TaskInfoSync]).Infos
            .TryGetValue(NewActivityService.WeeklyTaskBase, out var weekly) && weekly.CompleteNum.Contains(105L)), "the weekly kills in 5027");
        var rows = await OnLoop(() => ((DBTaskInfo)Session(alice).Synced[TaskService.TaskInfoSync]).Infos);
        var weeklyKills = rows[NewActivityService.WeeklyTaskBase];
        Assert.Equal(TaskState.Running, weeklyKills.Status);
        Assert.Equal([105L], weeklyKills.CompleteNum);
        if (play.Daily.Contains("d.kills"))
            Assert.Equal(TaskState.Delivery, rows[NewActivityService.DailyTaskBase].Status); // paid on completion, shown as claimed
    }

    [ClientDataFact]
    public async Task NovicePassLevelsFromMainStoryQuestsFromLevelTen()
    {
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, [(NewActivityService.PassGoal.Quests, 1)], quest: 10101010); // level 1: 9201 not open yet
        Assert.Equal(0, await Currency(alice, NewActivityService.NovicePassExpCurrency));
        await Gm(alice, "!lvl 10");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, [(NewActivityService.PassGoal.Quests, 1)], quest: 10101020);
        await Event(alice, [(NewActivityService.PassGoal.Quests, 1)], quest: 9201101); // not a main quest
        Assert.Equal(300, await Currency(alice, NewActivityService.NovicePassExpCurrency));
    }

    [ClientDataFact]
    public async Task SlashPassListsTodayAndTheWeekOnTheMarqueeAndNobodyElseSeesIt()
    {
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, []);
        await alice.JoinChatAsync("4_1");
        await alice.SendChatAsync("4_1", 2, "/pass", channelIndex: 4);
        var today = BroadCastNotify.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains("Today"), Wait)).Body).Content;
        Assert.StartsWith("Pass Lv 0, 0/2000 EXP. Today (+300 each): ", today);
        foreach (var id in (await Play(alice)).Daily)
            Assert.Contains(NewActivityService.DailyChallenges.Single(c => c.Id == id).Text, today);
        await alice.SendChatAsync("4_1", 2, "/PASS week", channelIndex: 4);
        var week = BroadCastNotify.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains("This week"), Wait)).Body).Content;
        Assert.Contains("Play on 5 days 1/5", week);
        Assert.Contains("(0/7 done, +1000 each)", week);
    }
}
