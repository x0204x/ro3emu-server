using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Micro;
using Ro3Emu.Bot;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
namespace Ro3Emu.Tests;
/// <summary>自造資料測真正建立／加入／計時／交付與持久化 owner；不呼叫 GM 完成或語音成功回呼。</summary>
public sealed class TavernOwnerTests
{
    private sealed class Clock(DateTimeOffset initial) : TimeProvider
    {
        private long _now = initial.ToUnixTimeMilliseconds();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _now));
        public void Advance(long ms) => Interlocked.Add(ref _now, ms);
    }
    private static GameSession Session(SyntheticHost host, long id) => host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == id);
    private static Task Loop(SyntheticHost host, Func<Task> action) => host.Service<GameLoop>().InvokeAsync(action);
    private static async Task Join(SyntheticHost host, GameSession session) =>
        await Loop(host, () => host.Service<TavernService>().CreateAsync(session,
            new CreateBarRoomReq { BarRoomType = 1, LanguageType = 21, BarRoomName = "自造酒館" }));

    [Fact]
    public async Task RoomLifecycleRequiresLeaseAndDirectoryCarriesRealSummaryBytes()
    {
        string fixture = Fixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("TavernAlice");
            await using var bob = await host.InWorldAsync("TavernBob");
            var a = Session(host, (long)alice.Self!.Uuid); var b = Session(host, (long)bob.Self!.Uuid);
            await Loop(host, () => host.Service<TavernService>().CreateAsync(a,
                new CreateBarRoomReq { BarRoomType = 21, LanguageType = 1, BarRoomName = "錯置類型" }));
            Assert.Equal(0, a.Tavern!.RoomId);
            var rejected = CreateBarRoomResp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(TavernService.Create), TimeSpan.FromSeconds(12))).Body);
            Assert.Equal(ErrorCodes.FeatureUnavailable, rejected.Result);
            await alice.SendGameAsync(TavernService.Create, new CreateBarRoomReq { BarRoomType = 1, LanguageType = 21, BarRoomName = "封包酒館" });
            var create = CreateBarRoomResp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(TavernService.Create), TimeSpan.FromSeconds(12))).Body);
            Assert.Equal(0, create.Result); long room = create.Info.BarRoomSummary.BarRoomGuid; Assert.True(room > 0);
            Assert.Equal(room, a.Tavern.RoomId);
            await Join(host, a); Assert.Equal(room, a.Tavern.RoomId);
            Assert.False(host.Service<TavernService>().RoomAllowed(b, room));
            await bob.SendGameAsync(TavernService.Enter, new EnterBarRoomReq { BarRoomGuid = room });
            var enter = EnterBarRoomResp.Parser.ParseFrom((await bob.Inbox.WaitAsync(e => e.IsGame(TavernService.Enter), TimeSpan.FromSeconds(12))).Body);
            Assert.Equal(0, enter.Result); Assert.Equal(room, enter.Info.BarRoomSummary.BarRoomGuid);
            await bob.SendGameAsync(TavernService.Get, new BarRoomGetReq());
            var get = BarRoomGetResp.Parser.ParseFrom((await bob.Inbox.WaitAsync(e => e.IsGame(TavernService.Get), TimeSpan.FromSeconds(12))).Body);
            Assert.Equal(0, get.Result); Assert.Equal(room, get.Info.BarRoomSummary.BarRoomGuid);
            Assert.True(host.Service<TavernService>().RoomAllowed(b, room));
            var party = host.App.Services.GetServices<IMicroService>().Single(service => service.Name == "PartyService");
            var micro = new MicroSession(a.Channel, 999) { Account = a.Account };
            var directory = Assert.IsType<Dictionary<string, object?>>(await party.InvokeAsync(micro, "getPartysSummary", new object?[] { 4, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>(), 0, 20 }));
            var counts = Assert.IsType<Dictionary<string, object?>>(await party.InvokeAsync(micro, "getSubPartysCountByType", new object?[] { a.Role!.Area.ToString(CultureInfo.InvariantCulture), 4 }));
            var count = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object[]>(counts["GroupCount"])));
            Assert.Equal(1, count["SubType"]); Assert.Equal(1, count["Count"]);
            var team = Assert.IsType<Dictionary<string, object?>>(await party.InvokeAsync(micro, "getPartysSummary", new object?[] { 1 }));
            Assert.Empty(Assert.IsType<object[]>(team["SummaryList"]));
            var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object[]>(directory["SummaryList"])));
            var summary = BarRoomSummary.Parser.ParseFrom(Assert.IsType<byte[]>(row["GameSummaryData"]));
            Assert.Equal(room, summary.BarRoomGuid); Assert.Equal(2, summary.MemberList.Count);
            Assert.Equal(1, summary.BarRoomType); Assert.Equal(21, summary.LanguageType);
            await Loop(host, () => host.Service<TavernService>().ExitAsync(a, new ExitBarRoomReq { BarRoomGuid = room + 1 }));
            Assert.True(host.Service<TavernService>().RoomAllowed(a, room));
            await Loop(host, () => host.Service<TavernService>().ExitAsync(a, new ExitBarRoomReq { BarRoomGuid = room }));
            Assert.False(host.Service<TavernService>().RoomAllowed(a, room));
            await Loop(host, () => host.Service<TavernService>().ExitAsync(b, new ExitBarRoomReq { BarRoomGuid = room }));
            var closed = (Dictionary<string, object?>)host.Service<TavernService>().Directory(new object?[] { 4 });
            Assert.Empty(Assert.IsType<object[]>(closed["SummaryList"]));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task RoomCommitFailureLeavesNoPublishedRoomAndRetryCreatesOne()
    {
        string fixture = Fixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var bot = await host.InWorldAsync("TavernRoomRetry");
            var s = Session(host, (long)bot.Self!.Uuid); var accounts = host.Service<AccountStore>();
            accounts.BeforeOwnedCommitForTest = label => label == "tavern/join" ? Task.FromException(new IOException("自造房間提交故障")) : Task.CompletedTask;
            try { await Assert.ThrowsAsync<IOException>(() => Join(host, s)); Assert.Equal(0, s.Tavern!.RoomId); }
            finally { accounts.BeforeOwnedCommitForTest = null; }
            await Join(host, s); long room = s.Tavern!.RoomId; Assert.True(room > 0);
            await Join(host, s); Assert.Equal(room, s.Tavern.RoomId);
            var directory = (Dictionary<string, object?>)host.Service<TavernService>().Directory(new object?[] { 4 });
            Assert.Single(Assert.IsType<object[]>(directory["SummaryList"]));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task TimeCommitFailureRetriesWithoutChangingMemoryOrPayingTwice()
    {
        string fixture = Fixture(); var clock = new Clock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture, clock);
            await using var bot = await host.InWorldAsync("TavernRetry");
            long role = (long)bot.Self!.Uuid; var s = Session(host, role);
            Assert.Equal(TaskState.Running, s.Tasks!.Infos[2000].Status);
            await Join(host, s); long start = s.Tavern!.AccountedAtMs;
            long beforeGoods = Goods(host, role);
            var accounts = host.Service<AccountStore>();
            accounts.BeforeOwnedCommitForTest = label => label == "tavern/time" ? Task.FromException(new IOException("自造酒館計時交易故障")) : Task.CompletedTask;
            try
            {
                await Loop(host, async () => { clock.Advance(1200000); await Assert.ThrowsAsync<IOException>(() => host.Service<TavernService>().ReconcileAsync(s)); });
                Assert.Equal(0, s.Tavern.ActiveMs); Assert.Equal(start, s.Tavern.AccountedAtMs);
                Assert.Equal(0, s.Tasks.CompleteTaskNum.GetValueOrDefault(2000)); Assert.Equal(beforeGoods, Goods(host, role));
            }
            finally { accounts.BeforeOwnedCommitForTest = null; }
            await Loop(host, () => host.Service<TavernService>().ReconcileAsync(s));
            Assert.Equal(1200000, s.Tavern.ActiveMs);
            Assert.Equal(1, s.Tasks.CompleteTaskNum[2000]);
            var persisted = DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role)!);
            Assert.Equal(1, persisted.CompleteTaskNum[2000]); Assert.Equal(beforeGoods + 5, Goods(host, role));
            await Loop(host, () => host.Service<TavernService>().ReconcileAsync(s));
            Assert.Equal(1, s.Tasks.CompleteTaskNum[2000]); Assert.Equal(beforeGoods + 5, Goods(host, role));
            await bot.DisposeAsync(); await host.RestartAsync();
            await using var again = await host.InWorldAsync("TavernRetry", create: false);
            Assert.Equal(beforeGoods + 5, Goods(host, role));
            Assert.Equal(1, DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role)!).CompleteTaskNum[2000]);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task ExitAcrossDailyBoundarySplitsTimeAndRestartCannotResumeOldLease()
    {
        string fixture = Fixture(); var clock = new Clock(new DateTimeOffset(2026, 10, 30, 4, 59, 59, TimeSpan.Zero));
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture, clock);
            long role; GameSession previousSession;
            await using (var bot = await host.InWorldAsync("TavernDay"))
            {
                role = (long)bot.Self!.Uuid; var s = Session(host, role); previousSession = s; await Join(host, s);
                long oldDay = s.Tavern!.DayEpoch, room = s.Tavern.RoomId;
                await Loop(host, async () => { clock.Advance(2000); await host.Service<TavernService>().ExitAsync(s, new ExitBarRoomReq { BarRoomGuid = room }); });
                Assert.True(s.Tavern.DayEpoch > oldDay);
                Assert.Equal(1000, s.Tavern.ActiveMs); Assert.Equal(0, s.Tavern.RoomId);
                await Join(host, s); Assert.True(s.Tavern.RoomId > 0);
            }
            // Dispose 只代表客戶端 socket 關閉；先等伺服器觀察失聯，才推進自造時鐘。
            await ObservedLoss(host, previousSession);
            clock.Advance(600000); await host.RestartAsync();
            await using var again = await host.InWorldAsync("TavernDay", create: false);
            var loaded = Session(host, role);
            Assert.Equal(0, loaded.Tavern!.RoomId); Assert.Empty(loaded.Tavern.Lease);
            Assert.Equal(1000, loaded.Tavern.ActiveMs);
        }
        finally { Directory.Delete(fixture, true); }
    }

    private static async Task ObservedLoss(SyntheticHost host, GameSession session)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(session.TransportLost || session.Role == null)))
            await Task.Delay(10, limit.Token);
    }

    [Fact]
    public async Task ReconnectGraceExcludesEveryGapButKeepsUncommittedConnectedSeconds()
    {
        string fixture = Fixture(); var clock = new Clock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => { o.GameDataDir = fixture; o.ReconnectGraceSeconds = 900; }, clock);
            await using var bot = await host.InWorldAsync("TavernGaps");
            // InWorld 只完成進場；真正可重連的客戶端還須完成 micro bind / OnClientReadyInternal。
            Assert.True(await bot.CompleteGateAuthAsync());
            var session = Session(host, (long)bot.Self!.Uuid); await Join(host, session);
            Assert.True(host.Service<ReconnectService>().TicketOf(session)!.ClientReady);
            // 每次都在尚未呼叫計時交易前斷線；斷線前 1s + 2s + 3s 仍須完整入帳。
            clock.Advance(1000); await bot.DropGameAsync(BotDrop.Abrupt); await ObservedLoss(host, session);
            Assert.NotNull(session.Role); Assert.NotNull(host.Service<ReconnectService>().TicketOf(session)?.GraceUntil);
            clock.Advance(100000);
            Assert.Equal(Phonest.Proto.ConnectServerResp.Types.RespType.Reconnected, (await bot.ReconnectGameAsync()).Result);
            clock.Advance(2000); await bot.DropGameAsync(BotDrop.Abrupt); await ObservedLoss(host, session);
            Assert.NotNull(session.Role); Assert.NotNull(host.Service<ReconnectService>().TicketOf(session)?.GraceUntil);
            clock.Advance(200000);
            Assert.Equal(Phonest.Proto.ConnectServerResp.Types.RespType.Reconnected, (await bot.ReconnectGameAsync()).Result);
            clock.Advance(3000); await Loop(host, () => host.Service<TavernService>().ReconcileAsync(session));
            Assert.Equal(6000, session.Tavern!.ActiveMs);
            await bot.DropGameAsync(BotDrop.Graceful); await ObservedLoss(host, session);
            clock.Advance(600000);
            await Loop(host, () => host.Service<TavernService>().ReconcileAsync(session));
            Assert.Equal(6000, session.Tavern.ActiveMs);
            Assert.Equal(6, DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(session.Role!.Id)!).Infos[2000].CompleteNum[0]);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task HeldOldDayAwardSettlesOnNextDayLoginExactlyOnce()
    {
        string fixture = Fixture(); var clock = new Clock(new DateTimeOffset(2026, 10, 30, 4, 39, 59, TimeSpan.Zero));
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => { o.GameDataDir = fixture; o.ReconnectGraceSeconds = 0; }, clock);
            long role, beforeGoods; GameSession oldSession;
            var accounts = host.Service<AccountStore>();
            try
            {
                await using var bot = await host.InWorldAsync("TavernHeld");
                role = (long)bot.Self!.Uuid; oldSession = Session(host, role); await Join(host, oldSession);
                beforeGoods = Goods(host, role);
                accounts.BeforeOwnedCommitForTest = label => label == "economy/cd13e/quest-award"
                    ? Task.FromException(new IOException("自造舊日獎勵交易故障")) : Task.CompletedTask;
                await Loop(host, async () => { clock.Advance(1200000); await host.Service<TavernService>().ReconcileAsync(oldSession); });
                Assert.Equal(1200000, oldSession.Tavern!.ActiveMs);
                Assert.Equal(0, oldSession.Tasks!.CompleteTaskNum.GetValueOrDefault(2000));
                Assert.Equal(beforeGoods, Goods(host, role));
                // 真正 socket 離線且關閉寬限，等待完整交接後才移動日界；不手改任務完成值。
                await bot.DropGameAsync(BotDrop.Graceful); await ObservedLoss(host, oldSession);
                Assert.Null(oldSession.Role);
            }
            finally { accounts.BeforeOwnedCommitForTest = null; }
            clock.Advance(2000);
            await using (var login = await host.InWorldAsync("TavernHeld", create: false))
            {
                var current = Session(host, role);
                Assert.Equal(beforeGoods + 5, Goods(host, role));
                Assert.Equal(0, current.Tavern!.ActiveMs);
                Assert.Equal(0, current.Tavern.RoomId);
                Assert.Equal(0, current.Tasks!.Infos[2000].CompleteNum[0]);
                await Loop(host, () => host.Service<TavernService>().ReconcileAsync(current));
                Assert.Equal(beforeGoods + 5, Goods(host, role));
            }
            await host.RestartAsync();
            await using var reloaded = await host.InWorldAsync("TavernHeld", create: false);
            Assert.Equal(beforeGoods + 5, Goods(host, role));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task TimeCommitAppliedFailureRequiresRecoveryAndRestartSettlesOnlyOnce()
    {
        string fixture = Fixture(); var clock = new Clock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture, clock);
            long role, beforeGoods;
            await using (var bot = await host.InWorldAsync("TavernApply"))
            {
                role = (long)bot.Self!.Uuid; var session = Session(host, role); await Join(host, session);
                beforeGoods = Goods(host, role); var accounts = host.Service<AccountStore>();
                accounts.BeforeOwnedApplyForTest = label => label == "tavern/time"
                    ? Task.FromException(new IOException("自造酒館提交後套用故障")) : Task.CompletedTask;
                try
                {
                    await Loop(host, async () => { clock.Advance(1200000); await Assert.ThrowsAsync<IOException>(() => host.Service<TavernService>().ReconcileAsync(session)); });
                    Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired, session.Owner!.State);
                    Assert.Equal(0, session.Tavern!.ActiveMs);
                    Assert.Equal(1200000, JsonSerializer.Deserialize<TavernService.Meter>(host.Service<RoleStore>().LoadTavern(role)!)!.ActiveMs);
                    Assert.Equal(1200, DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role)!).Infos[2000].CompleteNum[0]);
                    Assert.Equal(beforeGoods, Goods(host, role));
                }
                finally { accounts.BeforeOwnedApplyForTest = null; }
            }
            await host.RestartAsync();
            await using var reloaded = await host.InWorldAsync("TavernApply", create: false);
            Assert.Equal(beforeGoods + 5, Goods(host, role));
            Assert.Equal(1, DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role)!).CompleteTaskNum[2000]);
            var current = Session(host, role); await Loop(host, () => host.Service<TavernService>().ReconcileAsync(current));
            Assert.Equal(beforeGoods + 5, Goods(host, role));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task BeginWithoutRoleDoesNotKeepAStaleTavernCutoffAfterLogin()
    {
        string fixture = Fixture(); var clock = new Clock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture, clock);
            await using var bot = host.NewBot();
            await bot.LoginAsync("tavernempty", "pw"); await bot.EnterGateAsync();
            var before = host.Service<SessionRegistry>().All().Single(s => s.Account == "tavernempty");
            Assert.Null(before.Role);
            await Loop(host, () => host.Service<Ro3Emu.Server.Game.Ownership.RoleHandover>().Begin(before));
            clock.Advance(600000);
            await bot.EnterWorldAsync((await bot.CreateRoleAsync("TavernEmpty")).RoleBaseInfo.Id);
            var session = Session(host, (long)bot.Self!.Uuid); await Join(host, session);
            await Loop(host, async () => { clock.Advance(1000); await host.Service<TavernService>().ReconcileAsync(session); });
            Assert.Equal(1000, session.Tavern!.ActiveMs);
        }
        finally { Directory.Delete(fixture, true); }
    }

    private static long Goods(SyntheticHost host, long role) => host.Service<ItemStore>().Load(role).Where(i => i.Entry == 1000000).Sum(i => (long)i.Stack);

    private static string Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-tavern-fixture-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
        void Write(string table, object rows) => File.WriteAllText(Path.Combine(root, "config", table + ".json"), JsonSerializer.Serialize(rows));
        Write("TaskConfig", new Dictionary<string, object>
        {
            [TaskService.FirstTask.ToString(CultureInfo.InvariantCulture)] = new { _iTaskID = TaskService.FirstTask, _iTaskType = 1, _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 1 }, _kProgressTarget = new[] { new[] { 101002, 1 } } },
            ["2000"] = new { _iTaskID = 2000, _iTaskType = 3, _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 1 }, _kProgressTarget = new[] { new[] { 106010, 1200 } }, _iAward = 990801, _kEXP = new[] { 1, 7 } }
        });
        Write("DailyActivityConfig", new Dictionary<string, object> { ["2000"] = new { _iID = 2000, _iCount = 1, _iResetTimeType = 5, _iTaskReceiveAction = new[] { new[] { 31, 1, 2000 } } } });
        Write("BarroomTypeConfig", new Dictionary<string, object> { ["1"] = new { _iType = 0 }, ["21"] = new { _iType = 1 } });
        Write("PublicParamConfig", new Dictionary<string, object> { ["BarroomTaskID"] = 2000, ["BarRoomNameLen"] = 18, ["BarroomPersonNum"] = 1000 });
        foreach (var (table, key, row) in new (string, int, object)[]
        {
            ("MultiverseGroupConfig", 990801, new { _iID = 990801, _itype = 0, _iValue_default = 990802 }),
            ("AwardConfig", 990802, new[] { new { _iID = 990802, _kAward = new[] { new[] { 1000000, 5, 0 } } } })
        })
        {
            string path = Path.Combine(root, "config", table + ".json");
            var content = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); content[key.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(row); File.WriteAllText(path, content.ToJsonString());
        }
        return root;
    }
}
