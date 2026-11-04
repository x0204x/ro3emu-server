using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The server against a modified client (docs/systems/anticheat.md): every test plays the cheater, sends the forged
/// message and checks the server refuses or corrects it and counts it (CheatMonitor), and plays the honest client to check
/// it still passes. On the invented fixture, which has no nav grid: the walk tests here are about the speed budget.
/// </summary>
public sealed class AntiCheatTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    /// <summary>The fixture's Novice speed: the default 700 (7 m/s).</summary>
    private const double Speed = 7;

    private static Task<SyntheticHost> StartAsync(Action<ServerOptions>? configure = null, TimeProvider? clock = null) =>
        SyntheticHost.StartAsync(o => { o.WalkCheck = "enforce"; configure?.Invoke(o); }, clock);

    /// <summary>The system clock, moved forward by hand: the message budgets refill on its timestamp.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private long _offsetTicks;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
        public override long GetTimestamp() =>
            base.GetTimestamp() + (long)(Interlocked.Read(ref _offsetTicks) * (TimestampFrequency / (double)TimeSpan.TicksPerSecond));
    }

    private static GameSession Session(SyntheticHost host, BotClient bot) =>
        host.Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private static Task<T> OnLoop<T>(SyntheticHost host, Func<T> read) =>
        host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private static async Task<Vec3> PositionAsync(SyntheticHost host, BotClient bot)
    {
        await bot.HeartbeatAsync(); // every message before it has been handled
        return await OnLoop(host, () => Session(host, bot).Position.Clone());
    }

    private static long Refusals(SyntheticHost host, BotClient bot, string kind) =>
        host.Service<CheatMonitor>().LoggedCount(bot.Account!, kind);

    private static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    // ---------------------------------------------------------------- the walk budget

    [Fact]
    public void AnHonestWalkNeverRunsTheBudgetDry()
    {
        var budget = new WalkAllowance();
        var t = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        // Two minutes of straight legs of 0.9 s at full speed (the client's walk cadence), then a stop.
        for (int leg = 0; leg < 133; leg++)
            Assert.True(budget.TrySpend(t.AddSeconds(0.9 * (leg + 1)), Speed, Speed * 0.9), $"leg {leg}");
    }

    [Fact]
    public void WalksHeldBackByLagArriveTogetherAndPass()
    {
        var budget = new WalkAllowance();
        var t = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        for (int leg = 1; leg <= 20; leg++) Assert.True(budget.TrySpend(t.AddSeconds(0.9 * leg), Speed, Speed * 0.9));
        // 3 s without a packet, then the four legs walked meanwhile within 10 ms.
        var burst = t.AddSeconds(0.9 * 20 + 3.6);
        for (int leg = 0; leg < 4; leg++) Assert.True(budget.TrySpend(burst.AddMilliseconds(leg * 3), Speed, Speed * 0.9), $"burst leg {leg}");
    }

    [Fact]
    public void DoubleSpeedRunsDryAndSmallStepsDoNotHelp()
    {
        var budget = new WalkAllowance();
        var t = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        int refused = 0;
        for (int leg = 1; leg <= 60; leg++)
            if (!budget.TrySpend(t.AddSeconds(0.9 * leg), Speed, 2 * Speed * 0.9)) refused++;
        Assert.True(refused > 10, $"{refused} of 60 double-speed legs refused");

        // The old check allowed 1.5 m on top of every walk however close together: 1.4 m every 10 ms is 140 m/s.
        var spam = new WalkAllowance();
        int accepted = 0;
        for (int step = 1; step <= 200; step++)
            if (spam.TrySpend(t.AddMilliseconds(10 * step), Speed, 1.4)) accepted++;
        Assert.InRange(accepted * 1.4, 0, Speed * WalkAllowance.Tolerance * (WalkAllowance.CapSeconds + 3) + WalkAllowance.FloorMetres);
    }

    [Fact]
    public void ATeleportPastTheSavedUpBudgetIsRefused()
    {
        var budget = new WalkAllowance();
        var t = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(budget.TrySpend(t, Speed, 0));
        double cap = Speed * WalkAllowance.Tolerance * WalkAllowance.CapSeconds + WalkAllowance.FloorMetres;
        Assert.False(budget.TrySpend(t.AddMinutes(10), Speed, cap + 5)); // standing ten minutes saves no more than the cap
        Assert.True(budget.TrySpend(t.AddMinutes(10), Speed, cap - 1));
    }

    [Fact]
    public void AHeldBudgetSavesNothingUntilTheHoldEnds()
    {
        var budget = new WalkAllowance();
        var t = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(budget.TrySpend(t, Speed, 0));
        // A 2 s cast while standing: what is left is the floor, and the cast's 2 s add nothing.
        budget.Hold(t, t.AddSeconds(2), Speed, walkingSeconds: 0);
        Assert.Equal(WalkAllowance.FloorMetres, budget.Available(t.AddSeconds(2), Speed), 3);
        Assert.False(budget.TrySpend(t.AddSeconds(2), Speed, 10)); // walked 10 m during the cast without reporting it
        Assert.Equal(WalkAllowance.FloorMetres + Speed * WalkAllowance.Tolerance, budget.Available(t.AddSeconds(3), Speed), 3);

        // Cast while walking for 0.5 s: the stop that follows may still report the 0.5 s walked.
        var walking = new WalkAllowance();
        Assert.True(walking.TrySpend(t, Speed, 0));
        walking.Hold(t.AddSeconds(5), t.AddSeconds(7), Speed, walkingSeconds: 0.5);
        Assert.True(walking.TrySpend(t.AddSeconds(5.01), Speed, Speed * 0.5));
        // A cancelled cast gives the walk back at once.
        walking.Hold(t.AddSeconds(6), t.AddSeconds(9), Speed, walkingSeconds: 0);
        walking.Release(t.AddSeconds(6.1));
        Assert.True(walking.TrySpend(t.AddSeconds(7.1), Speed, Speed));
    }

    // ---------------------------------------------------------------- walks on the gate

    [Theory]
    [InlineData(12d, 80f)]
    [InlineData(30d, 150f)]
    public async Task AuthorizedStorySpeedUsesTheBudgetAndStillRejectsAnOversizedWalk(double speed, float distance)
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("StoryRider");
        var start = await PositionAsync(host, bot);
        var scene = host.Service<SceneService>();
        var session = Session(host, bot);
        // 自造場景驗證共用速度入口；真實螞蟻階段授權另由 AntStory 測試核對。
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            scene.StoryMovementSpeed = role => ReferenceEquals(role, session) ? speed : null;
            return Task.CompletedTask;
        });
        await bot.WalkAsync(start.X + distance, start.Y, start.Z, stop: true);
        Assert.Equal(start.X + distance, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(0, Refusals(host, bot, CheatMonitor.Walk));
        await bot.WalkAsync(start.X + distance + 500, start.Y, start.Z, stop: true);
        Assert.Equal(start.X + distance, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Walk));
    }

    [Fact]
    public async Task ATeleportWalkIsPutBackAndCounted()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Jumper");
        var start = await PositionAsync(host, bot);
        await bot.WalkAsync(start.X + 200, start.Y, start.Z, stop: true);
        Assert.Equal(start.X, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Walk));

        // An honest step from there still passes.
        await bot.WalkAsync(start.X + 3, start.Y, start.Z, stop: true);
        Assert.Equal(start.X + 3, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Walk));
    }

    [Fact]
    public async Task ManySmallWalksCannotOutrunTheSpeed()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Sprinter");
        var start = await PositionAsync(host, bot);
        for (int step = 1; step <= 80; step++) await bot.WalkAsync(start.X + 1.4f * step, start.Y, start.Z);
        var end = await PositionAsync(host, bot);
        // 112 m asked for at once: the saved-up budget (about 54 m) and what the sending time adds, no more.
        Assert.InRange(end.X - start.X, 0, (float)(Speed * WalkAllowance.Tolerance * (WalkAllowance.CapSeconds + 3) + WalkAllowance.FloorMetres));
        Assert.True(Refusals(host, bot, CheatMonitor.Walk) > 0);
    }

    [Fact]
    public async Task AWalkThatIsNotANumberIsRefusedEvenWhenOnlyReporting()
    {
        await using var host = await StartAsync(o => o.WalkCheck = "log");
        await using var bot = await host.InWorldAsync("Nowhere");
        var start = await PositionAsync(host, bot);
        await bot.WalkAsync(float.NaN, start.Y, start.Z, stop: true);
        Assert.Equal(start.X, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Walk));
        // Report-only mode lets a fast walk through, but counts it.
        await bot.WalkAsync(start.X + 500, start.Y, start.Z, stop: true);
        Assert.Equal(start.X + 500, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(2, Refusals(host, bot, CheatMonitor.Walk));
    }

    [Fact]
    public async Task WalkingAtTheSpeedPasses()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Walker");
        var start = await PositionAsync(host, bot);
        // Legs of 0.9 s at the speed, past what the budget saves up when standing (about 54 m).
        float x = start.X;
        for (int leg = 1; leg <= 12; leg++)
        {
            await Task.Delay(900);
            x += (float)(Speed * 0.85);
            await bot.WalkAsync(x, start.Y, start.Z, stop: leg == 12);
        }
        Assert.Equal(x, (await PositionAsync(host, bot)).X, 1);
        Assert.Equal(0, Refusals(host, bot, CheatMonitor.Walk));
    }

    // ---------------------------------------------------------------- floods

    [Fact]
    public async Task AFloodOfMessagesIsCutAndCountedButNormalTrafficIsNot()
    {
        await using var host = await StartAsync(o => { o.AntiCheat.MessagesPerSecond = 20; o.AntiCheat.MessageBurst = 100; });
        await using var calm = await host.InWorldAsync("Calm");
        var here = await PositionAsync(host, calm);
        for (int i = 0; i < 20; i++) await calm.WalkAsync(here.X, here.Y, here.Z, stop: true);
        await PositionAsync(host, calm);
        Assert.Equal(0, Refusals(host, calm, CheatMonitor.Flood));

        await using var flood = await host.InWorldAsync("Flood");
        here = await PositionAsync(host, flood);
        for (int i = 0; i < 400; i++) await flood.WalkAsync(here.X, here.Y, here.Z, stop: true);
        await flood.HeartbeatAsync();
        Assert.True(Refusals(host, flood, CheatMonitor.Flood) > 100, $"{Refusals(host, flood, CheatMonitor.Flood)} dropped");
    }

    [Fact]
    public async Task TheRoleScreenHasSixSlots()
    {
        await using var host = await StartAsync(o => o.AntiCheat.MessageLimits.Clear());
        await using var bot = host.NewBot();
        await bot.LoginAsync("collector", "pw");
        await bot.EnterGateAsync();
        for (int i = 1; i <= 6; i++) Assert.Equal(0, (await bot.CreateRoleAsync("Alt" + i)).Result);
        Assert.Equal(ErrorCodes.Parameter, (await bot.CreateRoleAsync("Alt7")).Result);
        Assert.Equal(6, (await host.Service<Ro3Emu.Server.Accounts.RoleStore>().ForAccountAsync("collector")).Count);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.RoleLimit));
    }

    [Fact]
    public void ARateBucketRefillsAtItsRateWhileItRefuses()
    {
        var bucket = new RateBucket(0.5, 5);
        for (int i = 0; i < 5; i++) Assert.True(bucket.TryTake(1_000));
        Assert.False(bucket.TryTake(1_000));
        Assert.False(bucket.TryTake(2_000)); // half a token
        Assert.True(bucket.TryTake(3_000));  // one token after 2 s; the refused attempt in between kept the refill
        Assert.False(bucket.TryTake(3_000));
        for (int i = 0; i < 5; i++) Assert.True(bucket.TryTake(60_000));
        Assert.False(bucket.TryTake(60_000)); // refilled up to the burst, not past it
    }

    [Fact]
    public async Task SixRolesAreCreatedInARowOnOneConnection()
    {
        await using var host = await StartAsync();
        await using var bot = host.NewBot();
        await bot.LoginAsync("sixslots", "pw");
        await bot.EnterGateAsync();
        for (int i = 1; i <= 6; i++) Assert.Equal(0, (await bot.CreateRoleAsync("Six" + i)).Result);
        Assert.Equal(0, Refusals(host, bot, CheatMonitor.Flood));
    }

    [Fact]
    public async Task ARoleCreateOverItsBudgetIsAnsweredAndTheBudgetRefills()
    {
        var clock = new SteppedClock();
        await using var host = await StartAsync(o => o.AntiCheat.MessageLimits["1006"] = "0.5/1", clock);
        await using var bot = host.NewBot();
        await bot.LoginAsync("hasty", "pw");
        await bot.EnterGateAsync();
        Assert.Equal(0, (await bot.CreateRoleAsync("Hasty1")).Result);
        Assert.Equal(ErrorCodes.TooManyRequests, (await bot.CreateRoleAsync("Hasty2")).Result);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, (await bot.CreateRoleAsync("Hasty2")).Result);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Flood));
    }

    [Fact]
    public async Task ARoleSelectOverItsBudgetIsAnswered()
    {
        await using var host = await StartAsync(o => o.AntiCheat.MessageLimits["1007"] = "0.5/1");
        await using var bot = await host.InWorldAsync("Switcher");
        await bot.SwitchRoleAsync();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => bot.EnterWorldAsync(bot.Roles.Single().Id));
        Assert.Contains(ErrorCodes.TooManyRequests.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message);
    }

    // ---------------------------------------------------------------- chat

    [Fact]
    public async Task ChatLinesTooLongOrTooFastAreRefused()
    {
        await using var host = await StartAsync(o => o.AntiCheat.ChatLines = "1/3");
        await using var bot = await host.InWorldAsync("Talker");
        string map = "4_" + (await OnLoop(host, () => Session(host, bot).WorldScene));
        await bot.JoinChatAsync(map);
        await bot.SendChatAsync(map, 2, "hello");
        Assert.Equal("hello", (await bot.NextChatLineAsync()).Content);

        await bot.SendChatAsync(map, 2, new string('x', 600));
        await bot.HeartbeatAsync();
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Chat));
        for (int i = 0; i < 6; i++) await bot.SendChatAsync(map, 2, "spam " + i);
        await bot.HeartbeatAsync();
        Assert.True(Refusals(host, bot, CheatMonitor.Chat) >= 4); // a burst of 3 (one already spent), then one a second
    }

    [Fact]
    public async Task AnotherPartysChannelIsClosedToOutsiders()
    {
        await using var host = await StartAsync();
        await using var alice = await host.InWorldAsync("Leader");
        await using var eve = await host.InWorldAsync("Outsider");
        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five });
        long team = (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).TeamId;
        Assert.NotEqual(0, team);
        await alice.JoinChatAsync("3_" + team);
        await Assert.ThrowsAnyAsync<Exception>(() => eve.JoinChatAsync("3_" + team));
        await Assert.ThrowsAnyAsync<Exception>(() => eve.JoinChatAsync("@TGS#_3_" + team));
        await eve.JoinChatAsync("3_7"); // no party has that id (a Survivor team's): open as before
    }

    // ---------------------------------------------------------------- saved client state

    [Fact]
    public async Task SettingsPastTheLimitAreNotSaved()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Hoarder");
        await bot.SaveClientSettingAsync(1, new byte[64]);
        var big = new SaveSettingReq { DbSettingInfo = new DBSettingInfo() };
        for (int key = 0; key < 600; key++) big.DbSettingInfo.ClientSetting[1000 + key] = ByteString.CopyFrom(new byte[1024]);
        await bot.SendGameAsync(MsgId.SaveSetting, big);
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, MsgId.SaveSetting, SaveSettingResp.Parser)).Result);
        var saved = await OnLoop(host, () => Session(host, bot).Settings!.ClientSetting.Keys.ToList());
        Assert.Equal([1], saved);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.Payload));
    }

    [Fact]
    public async Task OnlyConfiguredGuidesAreKept()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Pupil");
        var update = new UpdateGuideReq { GuideIds = { 7001, 99_999_999 } };
        update.Records[7001] = 1;
        update.Records[88_888_888] = 1;
        await bot.SendGameAsync(GuideService.UpdateGuide, update);
        await bot.HeartbeatAsync();
        var guides = await OnLoop(host, () => Session(host, bot).Guides!.Clone());
        Assert.Equal([7001L], guides.FinishedGuideIds);
        Assert.Equal([7001L], guides.GuideFinishRecord.Keys);
    }

    // ---------------------------------------------------------------- quest reports (client data)

    [ClientDataFact]
    public async Task AnAreaReportedFromAcrossTheMapIsRefused()
    {
        // The first story quest (10101010) asks to reach area 10000001 of the South Gate field, which the client tests
        // itself and reports with 31209; the server holds the report to the player standing near that region.
        await using var host = await StartAsync(o => o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data"));
        await using var bot = await host.InWorldAsync("Explorer");
        var session = Session(host, bot);
        var region = host.Service<Ro3Emu.Server.Maps.WorldData>().Map(session.SceneEntry)!.Regions[10000001];
        var scene = host.Service<SceneService>();
        await host.Service<GameLoop>().InvokeAsync(() => scene.TeleportInMapAsync(session,
            new Ro3Emu.Server.Maps.MapPoint(region.X + 150, region.Y, region.Z + 150, 0)));
        await bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = 10000001 });
        await bot.HeartbeatAsync();
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.QuestArea));
        Assert.Equal(TaskState.Running, (await OnLoop(host, () => session.Tasks!.Infos[TaskService.FirstTask].Clone())).Status);

        await host.Service<GameLoop>().InvokeAsync(() => scene.TeleportInMapAsync(session,
            new Ro3Emu.Server.Maps.MapPoint(region.X, region.Y, region.Z, 0)));
        await bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = 10000001 });
        var step = await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser, m => m.Infos.ContainsKey(10101020));
        Assert.Equal(TaskState.Delivery, step.Infos[TaskService.FirstTask].Inc.Status);
        Assert.Equal(1, Refusals(host, bot, CheatMonitor.QuestArea));
    }
}
