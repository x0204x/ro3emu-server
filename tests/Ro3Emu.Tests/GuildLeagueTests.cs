using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Guild League (GuildLeagueService): the server-owned calendar (opening weeks from DailyActivityCustomConfig, then the two-week cycle), registration, 30410-30412, a match on
/// the Main/Sub battlefields with 30415/30433/30110/30414 and the settlement.
/// </summary>
public sealed partial class GuildLeagueTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-league-" + Guid.NewGuid().ToString("N"));
    // Members join in real time (GuildService.EnterTime) and stop being temporary after an hour: the realm opens on a
    // Monday more than a week ahead of today.
    private readonly DateTime _opening = FutureMonday();
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
        public long Now => Interlocked.Read(ref _seconds);
    }

    private static DateTime FutureMonday()
    {
        var day = DateTime.Today.AddDays(8);
        while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
        return day;
    }

    /// <summary>Unix seconds of a local time in opening week <paramref name="week"/> (1-based), weekday 1 = Monday.</summary>
    private long At(int week, int weekday, int hour, int minute, int second = 0)
    {
        var local = _opening.AddDays((week - 1) * 7 + weekday - 1).AddHours(hour).AddMinutes(minute).AddSeconds(second);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private GuildLeagueService League => Service<GuildLeagueService>();

    public async Task InitializeAsync()
    {
        _clock.Set(At(1, 1, 10, 0));
        var (web, micro, gate) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = gate;
            o.SeasonStart = _opening.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); o.GuildLeagueMinMembers = 2;
        }, _clock);
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
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

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Until(Func<Task<bool>> condition, string what)
    {
        for (int i = 0; i < 750; i++) { if (await condition()) return; await Task.Delay(20); }
        Assert.Fail("timed out: " + what);
    }

    /// <summary>Move the clock and wait until the league's per-second work for that second has run.</summary>
    private async Task Go(long at)
    {
        _clock.Set(at);
        await Until(() => Read(() => League.Idle(at)), "the league sweep at " + at);
    }

    private async Task Step(int seconds)
    {
        for (int i = 0; i < seconds; i++) await Go(_clock.Now + 1);
    }

    private async Task<long> Guild(string name, BotClient leader, BotClient member)
    {
        foreach (var b in new[] { leader, member }) await Gm(b, "!lvl 60");
        await leader.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name,
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 }
        });
        Assert.Equal(0, (await Next(leader, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(leader, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid > 0)).Guid;
        await leader.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(leader, GuildService.Edit, GuildEditResp.Parser)).Result);
        await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
        Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, n => n.IsJoin);
        return guild;
    }

    private static async Task<GetGuildLeagueMgrDataRsp> MgrData(BotClient bot)
    {
        await bot.SendGameAsync(GuildLeagueService.MgrData, new GetGuildLeagueMgrDataReq());
        return await Next(bot, GuildLeagueService.MgrData, GetGuildLeagueMgrDataRsp.Parser);
    }

    private static async Task<GetGuildLeagueGroupInfoRsp> GroupInfo(BotClient bot)
    {
        await bot.SendGameAsync(GuildLeagueService.GroupInfo, new GetGuildLeagueGroupInfoReq());
        return await Next(bot, GuildLeagueService.GroupInfo, GetGuildLeagueGroupInfoRsp.Parser);
    }

    private async Task<int> Enter(BotClient bot, long scene, int camp)
    {
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = scene });
        int result = (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result;
        if (result != 0) return result;
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == scene);
        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser, e => e.Base.Uuid == (ulong)Session(bot).Role!.Id);
        Assert.Equal(camp, self.Base.EntityCamp);
        return 0;
    }

    private List<MailService.Letter> Letters(BotClient bot)
    {
        var bytes = Service<RoleStore>().LoadMailbox(Session(bot).Role!.Id);
        return bytes == null ? [] : JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters;
    }

    [ClientDataFact]
    public async Task CalendarRunsTheOpeningWeeksThenATwoWeekCycle()
    {
        var calendar = Service<GuildLeagueCalendar>();
        var tournaments = calendar.From(At(1, 1, 0, 0)).Take(4).ToArray();
        // Monday opening, DailyActivityCustomConfig row 1: 129 Wed 05:00, 109 Wed 20:30, 110 Fri 20:30, 111 Sat 20:30.
        Assert.Equal((1, At(1, 3, 5, 0), At(1, 3, 20, 30), At(1, 3, 21, 0), At(1, 5, 20, 30), At(1, 6, 20, 30)),
            (tournaments[0].Index, tournaments[0].Init, tournaments[0].Preliminary.Start, tournaments[0].Preliminary.End,
             tournaments[0].Semifinal.Start, tournaments[0].Final.Start));
        // Rows 3-4: Wed and Sat of week 3 at 20:00, the final on Saturday of week 4.
        Assert.Equal((At(3, 3, 5, 0), At(3, 3, 20, 0), At(3, 6, 20, 0), At(4, 6, 20, 0), At(4, 6, 20, 30)),
            (tournaments[1].Init, tournaments[1].Preliminary.Start, tournaments[1].Semifinal.Start, tournaments[1].Final.Start,
             tournaments[1].Final.End));
        // Then every two weeks from week 5 on the rows' own first _kDay and start (13 is Saturday of the next week).
        long Steady(int week, int id)
        {
            var row = Service<GameData>().Row("DailyActivityConfig", id)!.Value;
            int day = (int)GameData.Number(row.GetProperty("_kDay")[0])!.Value;
            int start = (int)GameData.Number(row.GetProperty("_kTime")[0])!.Value;
            return At(week, day, start / 100, start % 100);
        }
        Assert.Equal((3, Steady(5, 129), Steady(5, 109), Steady(5, 110), Steady(5, 111)),
            (tournaments[2].Index, tournaments[2].Init, tournaments[2].Preliminary.Start, tournaments[2].Semifinal.Start, tournaments[2].Final.Start));
        Assert.Equal(Steady(7, 129), tournaments[3].Init);

        var info = calendar.Info(At(1, 1, 12, 0));
        Assert.Equal((GuildLeagueStatus.NotOpen, false, At(1, 3, 20, 30)), (info.EStatus, info.Open, info.NextPreliminaryTime));
        Assert.Equal(GuildLeagueStatus.Init, calendar.Info(At(1, 3, 6, 0)).EStatus);
        info = calendar.Info(At(1, 3, 20, 40));
        Assert.Equal((GuildLeagueStatus.FightOne, true, At(1, 3, 21, 0)), (info.EStatus, info.Open, info.EndTime));
        info = calendar.Info(At(1, 4, 12, 0));
        Assert.Equal((GuildLeagueStatus.FightTwo, false, At(1, 5, 20, 30)), (info.EStatus, info.Open, info.StartTime));
        Assert.Equal(GuildLeagueStatus.FightThree, calendar.Info(At(1, 6, 12, 0)).EStatus);
        info = calendar.Info(At(2, 2, 12, 0));
        Assert.Equal((GuildLeagueStatus.Reward, 1, At(3, 3, 20, 0)), (info.EStatus, info.Index, info.NextPreliminaryTime));
        Assert.Equal((GuildLeagueStatus.FightThree, 3), (calendar.Info(At(6, 2, 12, 0)).EStatus, calendar.Info(At(6, 2, 12, 0)).Index));
        await Task.CompletedTask;
    }

    [ClientDataFact]
    public async Task TournamentRegistersPlaysAMatchAndSettles()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        long astra = await Guild("Astra", alice, bob);
        long borea = await Guild("Borea", carol, dave);

        var before = await MgrData(alice);
        Assert.Equal(GuildLeagueStatus.NotOpen, before.ActivityInfo.EStatus);
        Assert.Equal(At(1, 3, 20, 30), before.ActivityInfo.NextPreliminaryTime);
        Assert.Equal(astra, before.MyGuildInfo.GuildID);
        Assert.Equal(0, before.MyGuildInfo.Rank);

        // Wednesday 05:00 of the opening week: registration places both guilds in Ragnarok (tier 1) by strength.
        await Go(At(1, 3, 5, 0, 30));
        await Until(() => Read(() => League.State.Index == 1), "registration");
        var registered = await MgrData(alice);
        Assert.Equal(GuildLeagueStatus.Init, registered.ActivityInfo.EStatus);
        Assert.Equal(1, registered.ActivityInfo.Index);
        Assert.Equal(1, registered.MyGuildInfo.Rank);
        Assert.NotEqual(0, registered.MyGuildInfo.RankNum);
        Assert.Equal(new[] { astra, borea }.Order(), registered.RankGuildInfo.Select(g => g.GuildID).Order());
        var schedule = await GroupInfo(alice);
        Assert.Equal(4, schedule.Info.Count);
        Assert.Equal(new[] { 0, 1, 0, 1 }, schedule.Info.Select(p => p.BlueGuild.Matcharea));
        Assert.Equal(2, schedule.Info.Count(p => p.BlueGuild.GuildID > 0 && p.RedGuild.GuildID == 0)); // byes

        // Not their round: the preliminaries and semifinals are byes for both.
        Assert.Equal(ErrorCodes.LeagueClosed, await Enter(alice, League.MainScene, 1));
        await Go(At(1, 3, 21, 0, 30));
        await Until(() => Read(() => League.State.Cup!.SettledRound == 1), "the preliminaries closed");
        await Go(At(1, 5, 21, 0, 30));
        await Until(() => Read(() => League.State.Cup!.SettledRound == 2), "the semifinals closed");
        Assert.All((await GroupInfo(alice)).Info.Where(p => p.BlueGuild.GuildID > 0), p => Assert.Equal(6, p.BlueGuild.Score));

        // The final: both guilds meet. Blue is the guild of the first semifinal (the client's FightThree pairing).
        await Go(At(1, 6, 20, 30, 5));
        // Quest targets of a fought match (TaskService.OnGuildLeagueMatch): 106603 [stage, n] and 106019 [stage, points, n].
        foreach (var (account, task) in new[] { ("alice", 706050L), ("alice", 500042L), ("alice", 1602030L), ("bob", 706050L), ("bob", 1602030L) })
            await QuestProbe.AcceptAsync(_app, _http, account, task);
        var open = await MgrData(alice);
        Assert.Equal((GuildLeagueStatus.FightThree, true), (open.ActivityInfo.EStatus, open.ActivityInfo.Open));
        var cup = await Read(() => League.State.Cup!);
        var pairs = GuildLeagueBracket.Pairs(cup.Groups[0].Pairs.Select(p => new GuildLeagueBracket.Pair(p[0], p[1])).ToArray(),
            new Dictionary<long, int> { [astra] = 6, [borea] = 6 }, 3);
        long blue = pairs[0].Blue;
        Assert.Equal(new[] { astra, borea }.Order(), new[] { pairs[0].Blue, pairs[0].Red }.Order());
        int astraCamp = blue == astra ? 1 : 2, boreaCamp = 3 - astraCamp;

        Assert.Equal(0, await Enter(alice, League.MainScene, astraCamp));
        var sync = await Next(alice, GuildLeagueService.SyncNotify, SyncPlayerGuildLeagueDungeonNtf.Parser);
        Assert.Equal(blue, sync.Info.BlueGuild.GuildID);
        var baseInfo = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, b => b.DungeonEntry == League.MainScene);
        Assert.Equal(3, baseInfo.InitData.GuildLeagueInitStr.CurStage);
        Assert.Contains(baseInfo.Vars, v => v.VarId == 411 && v.Vi64.Count == 4);
        Assert.Equal(0, await Enter(carol, League.MainScene, boreaCamp));
        Assert.Equal(0, await Enter(bob, League.SubScene, astraCamp));

        // A defeat on the Main Battlefield: 20 personal points and 2 guild points (UITips 90001/90002).
        await OnLoop(async () =>
        {
            Session(alice).Position = Session(carol).Position.Clone();
            await Service<CharacterService>().SetHpSpAsync(Session(carol), 1, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = Session(carol).Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Until(() => Read(() => Session(carol).Dead), "carol's defeat");
        var dead = await Next(carol, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, n => n.BDead && n.ReviveTime > 0);
        await Next(alice, GuildLeagueService.TitleNotify, GuildLeagueBattlePointTitleNtf.Parser, t => t.TitleId == 1037); // First Blood
        // The league revives at the camp's side once the countdown is over (LC_SceneCom_Revive_GuildLeague sends nothing).
        await Go(dead.ReviveTime);
        await Until(() => Read(() => !Session(carol).Dead), "carol's revival");

        // Alice holds the Fire point (region 43) alone for the stay time: 60 guild points and 30433 event 2.
        var fire = Service<WorldData>().Map(League.MainScene)!.Regions[43];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(fire.X, fire.Y, fire.Z, 0)));
        await Step(8);
        var captured = await Next(carol, GuildLeagueService.EventNotify, GuildLeagueEventNtf.Parser, e => e.EventID == 2);
        Assert.Equal(astra, captured.Id);
        var score = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser,
            e => e.Vars.Any(v => v.VarId == 411 && v.Vi64[astraCamp == 1 ? 0 : 2] >= 62));

        // The in-battle board (30110) carries both battlefields keyed by scene.
        await alice.SendGameAsync(30110, new DungeonStatisticsReq());
        var board = await Next(alice, 30110, DungeonStatisticsRes.Parser);
        Assert.Equal(new[] { League.MainScene, League.SubScene }.Order(), board.DungeonRolesInfo.Keys.Order());
        var mine = board.DungeonRolesInfo[League.MainScene].DungeonRolesInfo.Single(r => r.RoleBase.Id == Session(alice).Role!.Id);
        Assert.Equal(1, mine.DicParams[6]);
        Assert.Equal(1, mine.DicParams[11]);
        Assert.True(mine.DicParams[18] >= 20 + 10 + 65); // defeat, First Blood title (1037 _kScore [3,10]), capture

        // Waves: the courtyard Boss after 3 minutes; on the Sub-Battlefield 12 guards at once, 7 more guards and 4
        // commanders after 5 minutes, the Boss after 10 (UITips 90007/90008).
        int Actors(bool main) => (main ? League.Matches.Single().Main : League.Matches.Single().Sub).Actors.Count;
        Assert.Equal(12, await Read(() => Actors(false)));
        await Go(At(1, 6, 20, 33, 1));
        Assert.Equal(1, await Read(() => Actors(true)));
        await Go(At(1, 6, 20, 35, 1));
        Assert.Equal(23, await Read(() => Actors(false)));
        await Go(At(1, 6, 20, 40, 1));
        Assert.Equal(24, await Read(() => Actors(false)));

        // Morale 1000 gives the guild's members on both battlefields the first morale buff (UITips 90011).
        await Read(() => { var m = League.Matches.Single(); m.Morale[astra] = 1000; m.Dirty = true; return 0; });
        await Step(1);
        await Until(() => Read(() => Session(alice).Buffs.Any(b => b.ConfigId == 4801101)
            && Session(bob).Buffs.Any(b => b.ConfigId == 4801101) && Session(carol).Buffs.All(b => b.ConfigId != 4801101)), "the morale buff");
        await Next(bob, DungeonService.Event, DungeonEventNotify.Parser,
            e => e.Vars.Any(v => v.VarId == 412 && v.Vi64[astraCamp == 1 ? 0 : 2] == 1000));

        // Time-out at 21:00: Astra leads on points and wins; 30414 carries the report.
        await Go(At(1, 6, 21, 0, 1));
        var result = await Next(alice, GuildLeagueService.ResultNotify, GuildLeagueDungeonResultNtf.Parser);
        Assert.Equal(blue == astra ? 0 : 1, result.History.ResultInfo.Result);
        Assert.Equal(new[] { League.MainScene, League.SubScene }.Order(), result.History.DungeonRolesInfo.Keys.Order());
        await Next(bob, GuildLeagueService.ResultNotify, GuildLeagueDungeonResultNtf.Parser);
        // The byes of rounds 1 and 2 were no match; the final is one for both fighters. 1602030 asks for more than
        // 20 personal points in the finals: Alice scored, Bob (Sub-Battlefield, no kill or capture) did not.
        await QuestProbe.DoneAsync(_app, "alice", 706050);
        await QuestProbe.WaitAsync(_app, "alice", 500042, i => i.Status == TaskState.Delivery, "delivered");
        await QuestProbe.DoneAsync(_app, "alice", 1602030); // delivery waits on its action 115
        await QuestProbe.DoneAsync(_app, "bob", 706050);
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "bob", 1602030))!.CompleteNum[0]);
        await Until(() => Read(() => League.State.Cup!.Settled), "the tournament settlement");

        await alice.SendGameAsync(GuildLeagueService.Info, new GetGuildLeagueInfoReq());
        var reports = await Next(alice, GuildLeagueService.Info, GetGuildLeagueInfoRsp.Parser);
        Assert.Equal(3, Assert.Single(reports.Info.History).ResultInfo.CurStage);
        var final = await GroupInfo(alice);
        Assert.Equal(GuildLeagueStatus.Reward, final.EStatus);
        var scores = final.Info.SelectMany(p => new[] { p.BlueGuild, p.RedGuild }).Where(g => g.GuildID > 0).ToDictionary(g => g.GuildID);
        Assert.Equal((7, 6), (scores[astra].Score, scores[borea].Score));
        var ranked = await MgrData(alice);
        Assert.Equal((1, GuildLeagueRankLift.None), (ranked.MyGuildInfo.RankNum, ranked.MyGuildInfo.RankLift));

        // Rewards: the winner's tier award by mail 1038 for 20+ personal points; no auction (fewer than 1000 guild points),
        // mail 1124 to the participants; the Ragnarok group winner is told about the Honor Emblem (1039).
        await Until(() => Task.FromResult(Letters(alice).Any(l => l.TemplateId == 1038)), "alice's league award");
        Assert.NotEmpty(Letters(alice).Single(l => l.TemplateId == 1038).Attachments);
        Assert.DoesNotContain(Letters(carol), l => l.TemplateId == 1038);
        Assert.Contains(Letters(bob), l => l.TemplateId == 1124);
        Assert.Contains(Letters(alice), l => l.TemplateId == 1039);

        // GuildLeagueEndTime: the battlefields empty a minute after the end.
        await Go(At(1, 6, 21, 1, 5));
        await Until(() => Read(() => Session(alice).InstanceId == 0 && Session(bob).InstanceId == 0), "the departure");
    }
}
