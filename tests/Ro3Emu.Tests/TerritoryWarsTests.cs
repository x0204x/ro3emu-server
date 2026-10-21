using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class TerritoryWarsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-territory-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private sealed class Clock : TimeProvider
    {
        private long _seconds = 1779535900; // round 2 free auction, exported absolute calendar.
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private long SourceCount(string prefix)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Service<AccountStore>().DatabasePath};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_source WHERE source_key LIKE $prefix";
        command.Parameters.AddWithValue("$prefix", prefix + "%");
        return (long)command.ExecuteScalar()!;
    }
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private async Task WaitSceneOnlinePublicationAsync(params BotClient[] bots)
    {
        var loop = Service<GameLoop>();
        var sessions = Service<SessionRegistry>();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!await loop.InvokeAsync(() => Task.FromResult(bots.All(bot => sessions.All().Any(s =>
                   s.Account == bot.Account && s.Role != null && s.Handoff == null && s.GuildOnlinePublished)))))
        {
            if (elapsed.Elapsed >= Wait) throw new TimeoutException("Scene online publication did not start for every test role.");
            await Task.Delay(10);
        }
        // SceneLoaded sends the bot's ready packets before the route takes Realm for guild publication.
        // The flag is set inside that scope; acquiring it once drains the remaining route work.
        await OnLoop(async () =>
        {
            using var scope = await loop.Owners!.EnterAsync(
            "territory test scene online barrier", ReservationKey.Realm);
        }).WaitAsync(Wait);
    }
    public Task InitializeAsync() => StartAsync();
    private async Task StartAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game; o.PlayBook = 101; o.SeasonStart = "2026-05-19";
        }, _clock);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
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
    private async Task<BotClient> Bot(string name, bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single(r => r.Name == name).Id;
        await b.EnterWorldAsync(id); return b;
    }
    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var envelope = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(envelope.Body);
    }
    private async Task<long> Guild(BotClient bot, string name, int members = 20)
    {
        await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(bot), 99, null));
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name,
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 }
        });
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        long id = (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, g => g.Guid > 0)).Guid;
        await OnLoop(() =>
        {
            var store = Service<GuildStore>(); var g = store.Get(id)!;
            // Real persisted offline members satisfy the exported minimum; three leaders are live wire bots.
            for (int i = 1; i < members; i++)
            {
                var r = Service<RoleStore>().Create(bot.Account!, 1, $"G{id}Member{i}", 1, 1000, 1)!;
                g.Members[r.Id] = new GuildStore.Member { RoleId = r.Id, Name = r.Name, Official = 20, Level = 99, Career = 1000 };
            }
            g.AuctionMaterials = 5000; g.Materials = 5000;
            Assert.True(store.Save(g)); return Task.CompletedTask;
        });
        return id;
    }
    private async Task Bid(BotClient bot, long amount, long current = 0, int zone = 2)
    {
        await bot.SendGameAsync(TerritoryWarsService.Bid, new TerritoryAuctionBidReq { ZoneId = zone, TargetBid = amount, CurrentBid = current });
        Assert.Equal(amount, (await Next(bot, TerritoryWarsService.Bid, TerritoryAuctionBidRes.Parser)).ActualBid);
    }
    private async Task Enter(BotClient bot, long scene)
    {
        await bot.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = scene });
        Assert.Equal(0, (await Next(bot, 30400, EnterDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == scene);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        var info = await Next(bot, 30401, DungeonBaseInfoNotify.Parser, i => i.DungeonEntry == scene);
        Assert.NotNull(info.InitData.TerritoryWarInitData);
        Assert.True(info.InitData.TerritoryWarInitData.GuildInfos.Count >= 2);
    }

    [ClientDataFact]
    public async Task ThreeGuildWireAuctionRefundsThenSharedBattleCapturesSettlesAndReplaysSafely()
    {
        await using var a = await Bot("TerrAlpha"); await using var b = await Bot("TerrBravo"); await using var c = await Bot("TerrCharlie");
        long ga = await Guild(a, "TwAlpha"), gb = await Guild(b, "TwBravo"), gc = await Guild(c, "TwChar");
        var store = Service<GuildStore>(); var wars = Service<TerritoryWarsService>();
        var catalog = EventEconomy.Catalog(Service<GameData>());
        var database = Service<AccountStore>().DatabasePath;
        var auctionOpening = EconomySnapshotReader.Read(database, catalog, "auto");
        await a.SendGameAsync(TerritoryWarsService.LegacyBid, new TerritoryWarsAuctionReq { StateId = 2, AuctionPrice = 100 });
        Assert.Equal(0, (await Next(a, TerritoryWarsService.LegacyBid, TerritoryWarsAuctionResp.Parser)).Result);
        Assert.Equal(4900, store.Get(ga)!.Materials);
        await Bid(b, 1100); await Bid(c, 1200);
        var auctionClosing = EconomySnapshotReader.Read(database, catalog, "auto");
        var auctionLedger = EconomyLedgerReader.Read(database, catalog, "auto", auctionOpening.Cut.CommitSequence!.Value,
            auctionClosing.Cut.CommitSequence!.Value);
        Assert.True(auctionLedger.CompleteHistory, string.Join("; ", auctionLedger.Issues));
        var auctionExactness = EconomyExactnessChecker.Check(auctionOpening, auctionClosing, auctionLedger);
        Assert.All(auctionExactness.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(auctionExactness.Locations, location => Assert.Equal("0", location.Residual));
        Assert.DoesNotContain(auctionExactness.Issues, issue => issue.Kind is EconomyIssueKind.Corruption or EconomyIssueKind.UnknownSchema);
        Assert.Equal(5000, store.Get(ga)!.AuctionMaterials); // evicted seat refunds the entire escrow.
        Assert.Equal(5000, store.Get(ga)!.Materials); // mixed protocol eviction refunds the original currency.
        Assert.Equal(3900, store.Get(gb)!.AuctionMaterials); Assert.Equal(3800, store.Get(gc)!.AuctionMaterials);
        await a.SendGameAsync(TerritoryWarsService.AuctionQuery, new TerritoryAuctionQueryReq());
        var rank = await Next(a, TerritoryWarsService.RankNotify, TerritoryAuctionRankDataNtf.Parser,
            x => x.Zones.Any(z => z.ZoneId == 2 && z.Entries.Count == 2 && z.Entries.All(e => e.GuildId != ga)));
        Assert.True(rank.Zones.Single(z => z.ZoneId == 2).SelfGuildBidded);
        await b.SendGameAsync(TerritoryWarsService.Bid, new TerritoryAuctionBidReq { ZoneId = 2, TargetBid = 1400, CurrentBid = 0 });
        Assert.Equal(ErrorCodes.TerritoryBidChanged, (await Next(b, 2, CommonErrorNtf.Parser)).ErrorCode);
        Assert.Equal(3900, store.Get(gb)!.AuctionMaterials);
        await b.SendGameAsync(TerritoryWarsService.Bid, new TerritoryAuctionBidReq { ZoneId = 13, TargetBid = 1000 });
        Assert.Equal(ErrorCodes.TerritoryOtherRegion, (await Next(b, 2, CommonErrorNtf.Parser)).ErrorCode);
        _clock.Set(1779622201); await OnLoop(wars.ReconcileAsync); // round 2 capital battle.
        await a.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = 3030401 });
        Assert.Equal(ErrorCodes.TerritoryNotQualified, (await Next(a, 30400, EnterDungeonRsp.Parser)).Result);
        long outsideB = Session(b).SceneEntry, outsideC = Session(c).SceneEntry;
        long roleB = Session(b).Role!.Id;
        int campBeforeRefusal = Session(b).TerritoryWars!.Camp;
        byte[]? roleBeforeRefusal = Service<RoleStore>().LoadTerritoryWars(roleB);
        var accounts = Service<AccountStore>();
        int refusedAdmissions = 0;
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "territory/battle-admit") return;
            Interlocked.Increment(ref refusedAdmissions);
            throw new IOException("injected battle admission commit refusal");
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => OnLoop(() =>
                wars.EnterAsync(Session(b), new EnterDungeonReq { SceneEntry = 3030401 })));
        }
        finally { accounts.BeforeCommitForTest = null; }
        Assert.Equal(1, Volatile.Read(ref refusedAdmissions));
        Assert.Null(Session(b).TerritoryBattle);
        Assert.Equal(0, Session(b).InstanceId);
        Assert.Equal(campBeforeRefusal, Session(b).TerritoryWars!.Camp);
        Assert.Equal(roleBeforeRefusal, Service<RoleStore>().LoadTerritoryWars(roleB));
        await Enter(b, 3030401); await Enter(c, 3030401);
        var battle = Session(b).TerritoryBattle!;
        Assert.Same(battle, Session(c).TerritoryBattle); Assert.Equal(Session(b).InstanceId, Session(c).InstanceId);
        Assert.True(wars.Hostile(Session(b), Session(c)));
        MonsterUnit objective = null!;
        await OnLoop(async () =>
        {
            objective = battle.Room.Monsters.Single(m => !m.Dead);
            objective.Hp = 1; // fixture weakens HP; the real 31102/31101 path performs the capture.
            await Service<SceneService>().TeleportInMapAsync(Session(b), new MapPoint(objective.X, objective.Y, objective.Z, 0));
        });
        await b.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = objective.Uid });
        Assert.Equal(0, (await Next(b, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(b, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == objective.Uid && d.IsDead));
        var captured = await Next(c, 30401, DungeonBaseInfoNotify.Parser,
            x => x.InitData?.TerritoryWarInitData?.PointWarZoneIdGuild.GetValueOrDefault(2) == gb);
        Assert.Equal(gb, battle.State.Points[0].Owner);
        // A forged result and script event cannot settle or change ownership.
        await c.SendGameAsync(TerritoryWarsService.ResultNotify, new TerritoryWarsDungeonResultNotify { CommonResultInfo = new DungeonResultInfo { WinEntityId = gc } });
        await c.SendGameAsync(30106, new ClientDungeonEvent { StrCmd = "win" });
        Assert.Equal(0, wars.OwnerOf(2));
        _clock.Set(1779625801); await OnLoop(wars.ReconcileAsync);
        var br = await Next(b, TerritoryWarsService.ResultNotify, TerritoryWarsDungeonResultNotify.Parser);
        var cr = await Next(c, TerritoryWarsService.ResultNotify, TerritoryWarsDungeonResultNotify.Parser);
        Assert.Equal(gb, br.CommonResultInfo.WinEntityId); Assert.Equal(br, cr); Assert.Equal(gb, wars.OwnerOf(2));
        var bytes = Service<RoleStore>().LoadTerritoryWars(Session(b).Role!.Id)!;
        var paid = JsonSerializer.Deserialize<TerritoryWarsService.RoleState>(bytes)!;
        Assert.Single(paid.Paid); Assert.Empty(paid.Pending);
        Assert.Equal(1, SourceCount("territory-battle-settle:"));
        await OnLoop(wars.ReconcileAsync);
        Assert.Equal(paid.Paid, JsonSerializer.Deserialize<TerritoryWarsService.RoleState>(Service<RoleStore>().LoadTerritoryWars(Session(b).Role!.Id)!)!.Paid);
        Assert.Equal(1, SourceCount("territory-battle-settle:"));
        await b.SendGameAsync(TerritoryWarsService.History, new GetTerritoryWarsHistoryStatisticsReq { StateId = { 2 } });
        var history = await Next(b, TerritoryWarsService.History, GetTerritoryWarsHistoryStatisticsResp.Parser);
        Assert.Contains(br.CommonResultInfo.StatisticsId, history.History.GroupStatistic[2].StatisticIds.Keys);
        await c.SendGameAsync(30114, new DungeonStatisticsInfoWithDungeonReq { StaisticsIds = { br.CommonResultInfo.StatisticsId } });
        var report = await Next(c, 30114, DungeonStatisticsInfoWithDungeonResp.Parser);
        Assert.Equal(0, report.Result); Assert.Equal(2, Assert.Single(report.DungeonStatistics).SceneStatistics.Values.Single().RoleStatistics.Count);
        _clock.Set(1779625810); await OnLoop(wars.ReconcileAsync);
        await Next(b, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, sw => sw.SceneEntry == outsideB);
        await Next(c, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, sw => sw.SceneEntry == outsideC);
        Assert.True(battle.Room.Closed);
        var afterLeave = JsonSerializer.Deserialize<TerritoryWarsService.RoleState>(Service<RoleStore>().LoadTerritoryWars(Session(b).Role!.Id)!)!;
        Assert.Single(afterLeave.Paid); Assert.Empty(afterLeave.Pending);
        Assert.Equal(1, br.CommonResultInfo.FirstResultType); Assert.Equal(1, br.CommonResultInfo.SecondResultType);
    }

    [ClientDataFact]
    public async Task StrongholdBattleSendsGameplayPointsOnEveryLoadAndPublishesChangedPoints()
    {
        await using var bot = await Bot("TerrPoints");
        long guild = await Guild(bot, "TwPnts");
        await bot.JoinChatAsync("4_1");
        var wars = Service<TerritoryWarsService>();
        _clock.Set(1779276700); await OnLoop(wars.ReconcileAsync); // TerritoryWarStageConfig 5: round 1 auction.
        await Bid(bot, 1000);
        _clock.Set(1779362200); await OnLoop(wars.ReconcileAsync); // stage 7: round 1 preparation, auction settled.
        long[] expected = [34, 41, 49, 51, 55, 56, 57, 59, 63, 66, 74, 81, 83, 85, 86]; // DungeonLua_3030101 UIModule_NpcData
        async Task<(GlobalScenePointDataNotify Points, Dictionary<int, GlobalObjectData> Markers)> EnterStronghold()
        {
            await bot.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = 3030101 });
            Assert.Equal(0, (await Next(bot, 30400, EnterDungeonRsp.Parser)).Result);
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 3030101);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 3030101);
            await Next(bot, 30401, DungeonBaseInfoNotify.Parser, i => i.DungeonEntry == 3030101);
            var points = await Next(bot, TerritoryWarsService.ScenePointNotify, GlobalScenePointDataNotify.Parser);
            // The markers reach a loaded member either with its load or right after they spawn.
            var markers = new Dictionary<int, GlobalObjectData>();
            while (markers.Count < expected.Length)
            {
                var objects = await Next(bot, 30115, GlobalObjectDataNotify.Parser,
                    n => n.GlobalDatas.Values.Any(d => d.ConfigId == 4905010 && !markers.ContainsKey(d.DungeonPointId)));
                foreach (var d in objects.GlobalDatas.Values.Where(d => d.ConfigId == 4905010)) markers[d.DungeonPointId] = d;
            }
            return (points, markers);
        }
        var (first, markers) = await EnterStronghold();
        Assert.Equal(expected, first.GlobalPoints.Keys.Order().ToArray());
        Assert.All(first.GlobalPoints, p =>
        {
            Assert.Equal(p.Key, p.Value.PointId);
            Assert.Equal(EnumGlobalScenePoint.Ready, p.Value.Status);
            Assert.Equal(0, p.Value.ReadyTime);
            Assert.Equal(0, p.Value.BelongGuildId);
        });
        Assert.Equal(expected, markers.Keys.Select(k => (long)k).Order().ToArray());
        var editor = Service<WorldData>().Map(3030101)!;
        Assert.All(markers, m =>
        {
            Assert.True(m.Value.Hp > 0); // hp 0 would make the client drop the entry.
            Assert.Equal(0, m.Value.BelongGuild);
            Assert.Equal(editor.Points[m.Key].X, m.Value.Pos.X); Assert.Equal(editor.Points[m.Key].Z, m.Value.Pos.Z);
        });
        var battle = Session(bot).TerritoryBattle!;
        Assert.Equal(3030101, battle.State.Scene);
        await OnLoop(() =>
        {
            Assert.Equal(expected.Length, battle.Room.Props.Count(p => p.Entity().EntityTemplateID == 4905010));
            Assert.DoesNotContain(battle.Room.Monsters, m => m.Template.NpcId == 4905010); // markers are never targets.
            return Task.CompletedTask;
        });

        // A change publishes only that point; readyTime is epoch milliseconds (the map countdown divides by 1000).
        await bot.SendChatAsync("4_1", 2, "!twpoint 34 unready 90 self", channelIndex: 4);
        Assert.Contains("Point 34", (await Next(bot, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        var delta = await Next(bot, TerritoryWarsService.ScenePointNotify, GlobalScenePointDataNotify.Parser);
        var barrier = Assert.Single(delta.GlobalPoints);
        Assert.Equal(34, barrier.Key);
        Assert.Equal(EnumGlobalScenePoint.UnReady, barrier.Value.Status);
        Assert.Equal((1779362200L + 90) * 1000, barrier.Value.ReadyTime);
        Assert.Equal(guild, barrier.Value.BelongGuildId);
        await bot.SendChatAsync("4_1", 2, "!twpoint 55 ready 0 self", channelIndex: 4);
        await Next(bot, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        Assert.Equal(55, Assert.Single((await Next(bot, TerritoryWarsService.ScenePointNotify, GlobalScenePointDataNotify.Parser)).GlobalPoints).Key);
        // Revival points read their owner from the marker's GlobalObjectData (GetRevivalPointData).
        var revival = await Next(bot, 30115, GlobalObjectDataNotify.Parser, n => n.GlobalDatas.Values.Any(d => d.DungeonPointId == 55));
        Assert.Equal(guild, revival.GlobalDatas.Values.Single(d => d.DungeonPointId == 55).BelongGuild);
        await bot.SendChatAsync("4_1", 2, "!twpoint 99 ready", channelIndex: 4);
        Assert.Contains("No gameplay point 99", (await Next(bot, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);

        // Every load of the battle scene repeats the full, persisted set (the client clears it on scene switch).
        await bot.SendGameAsync(30402, new LeaveDungeonReq());
        await Next(bot, 30402, LeaveDungeonRsp.Parser);
        var back = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, sw => sw.SceneEntry != 3030101);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = back.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == back.SceneEntry);
        Assert.Null(Session(bot).TerritoryBattle);
        var (again, remarkers) = await EnterStronghold();
        Assert.Equal(expected, again.GlobalPoints.Keys.Order().ToArray());
        Assert.Equal(EnumGlobalScenePoint.UnReady, again.GlobalPoints[34].Status);
        Assert.Equal(guild, again.GlobalPoints[34].BelongGuildId);
        Assert.Equal(guild, again.GlobalPoints[55].BelongGuildId);
        Assert.Equal(guild, remarkers[55].BelongGuild);
        Assert.Equal(EnumGlobalScenePoint.Ready, again.GlobalPoints[41].Status);
    }

    [ClientDataFact]
    public async Task AutoBidRechecksOfficerRightsAndSeasonRefundsEscrowOnce()
    {
        await using var bot = await Bot("TerrEpoch");
        await using var peer = await Bot("TerrEpochPeer");
        long guild = await Guild(bot, "TwEpoch");
        var store = Service<GuildStore>(); var wars = Service<TerritoryWarsService>();
        await bot.SendGameAsync(TerritoryWarsService.AutoSet, new TerritoryAutoBidSetReq
        {
            ZoneId = 2, InitialBid = 1000, MaxBid = 3000, Style = (TerritoryAutoBidStyle)1
        });
        await Next(bot, TerritoryWarsService.AutoSet, TerritoryAutoBidSetRes.Parser);
        await OnLoop(() =>
        {
            var g = store.Get(guild)!; g.Members[Session(bot).Role!.Id].Official = 20; g.Rights[25] = [];
            Assert.True(store.Save(g)); return Task.CompletedTask;
        });
        _clock.Set(1779535920); await OnLoop(wars.ReconcileAsync);
        Assert.Equal(5000, store.Get(guild)!.AuctionMaterials);
        await Next(bot, TerritoryWarsService.AutoNotify, TerritoryAutoBidSettingChangeNtf.Parser, n => (int)n.IsActive == 2);
        await OnLoop(() => { var g = store.Get(guild)!; g.Members[Session(bot).Role!.Id].Official = 1; Assert.True(store.Save(g)); return Task.CompletedTask; });
        await Bid(bot, 1000);
        Assert.Equal(4000, store.Get(guild)!.AuctionMaterials);
        await OnLoop(async () =>
        {
            // This phase observes exactly one refused projection before explicitly retrying it.
            // Drain existing passes and drive the remaining clock transitions from this test.
            typeof(TerritoryWarsService).GetField("_lastTick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(wars, TimeSpan.MaxValue);
            await wars.ReconcileAsync();
        });
        var accounts = Service<AccountStore>();
        long seasonSources = SourceCount("territory-season-rollover:");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int refusedProjection = 0;
        accounts.BeforeCommitForTest = label =>
        {
            if (label == "territory/role" && Interlocked.CompareExchange(ref refusedProjection, 1, 0) == 0)
                throw new IOException("injected one-role season projection refusal");
            if (label != "economy/event/territory-season-rollover") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("territory rollover commit barrier");
        };
        try
        {
            var rollover = OnLoop(async () => { Service<SeasonService>().SetPlayBook(102); await wars.ReconcileAsync(); });
            Assert.True(await Task.Run(() => entered.Wait(Wait)));
            Assert.False(rollover.IsCompleted);
            Assert.Equal(4000, store.Get(guild)!.AuctionMaterials);
            Assert.Equal(seasonSources, SourceCount("territory-season-rollover:"));
            release.Set();
            await rollover;
        }
        finally { release.Set(); accounts.BeforeCommitForTest = null; }
        Assert.Equal(5000, store.Get(guild)!.AuctionMaterials);
        Assert.Equal(seasonSources + 1, SourceCount("territory-season-rollover:"));
        Assert.Equal(1, Volatile.Read(ref refusedProjection));
        var currentEpoch = $"{Service<TerritoryWarsClock>().SeasonStart}:102";
        var projectionEpochs = new[] { bot, peer }.Select(player =>
            JsonSerializer.Deserialize<TerritoryWarsService.RoleState>(Service<RoleStore>().LoadTerritoryWars(Session(player).Role!.Id)!)!.Epoch).ToArray();
        Assert.Single(projectionEpochs, epoch => epoch == currentEpoch);
        await OnLoop(wars.ReconcileAsync);
        Assert.All(new[] { bot, peer }, player => Assert.Equal(currentEpoch,
            JsonSerializer.Deserialize<TerritoryWarsService.RoleState>(Service<RoleStore>().LoadTerritoryWars(Session(player).Role!.Id)!)!.Epoch));
        await OnLoop(async () => { Service<SeasonService>().SetPlayBook(101); await wars.ReconcileAsync(); });
        await OnLoop(async () => { Service<SeasonService>().SetPlayBook(102); await wars.ReconcileAsync(); });
        Assert.Equal(5000, store.Get(guild)!.AuctionMaterials);
    }

    [ClientDataFact]
    public async Task CommittedSeasonRoleApplyFaultLeavesOnlyThatRolePending()
    {
        await using var first = await Bot("TerrApplyOne");
        await using var second = await Bot("TerrApplyTwo");
        await WaitSceneOnlinePublicationAsync(first, second);
        var wars = Service<TerritoryWarsService>();
        var accounts = Service<AccountStore>();
        // Recovery may detach a session after the fault; retain the original projections to inspect.
        var players = new[] { first, second }.Select(Session).ToArray();
        var priorActivity = players.ToDictionary(player => player.Role!.Id,
            player => player.Synced[NewActivityService.ActivitySync]);
        int injected = 0;
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label == "territory/role" && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
                throw new IOException("injected committed territory role apply fault");
            return Task.CompletedTask;
        };
        try
        {
            await OnLoop(async () => { Service<SeasonService>().SetPlayBook(102); await wars.ReconcileAsync(); });
        }
        finally { accounts.BeforeOwnedApplyForTest = null; }
        Assert.Equal(1, Volatile.Read(ref injected));
        var failed = Assert.Single(players, s => s.Owner?.State ==
            Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired);
        var healthy = Assert.Single(players, s => !ReferenceEquals(s, failed));
        string epoch = $"{Service<TerritoryWarsClock>().SeasonStart}:102";
        Assert.NotEqual(epoch, failed.TerritoryWars!.Epoch);
        Assert.Equal(epoch, healthy.TerritoryWars!.Epoch);
        Assert.NotSame(priorActivity[healthy.Role!.Id], healthy.Synced[NewActivityService.ActivitySync]);
        var activity = Assert.IsType<DBNewActivityInfo>(healthy.Synced[NewActivityService.ActivitySync]);
        Assert.Equal(Service<TerritoryWarsClock>().Current.Type,
            (int)activity.ActivityInfo[2000].ActivityParam.TerritoryWar.TerritoryWarStage.StageType);
        Assert.All(players, player => Assert.Equal(epoch,
            JsonSerializer.Deserialize<TerritoryWarsService.RoleState>(
                Service<RoleStore>().LoadTerritoryWars(player.Role!.Id)!)!.Epoch));

        // The committed fault fences one role, while another reconciliation still runs.
        await OnLoop(wars.ReconcileAsync);
        Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired, failed.Owner!.State);
        Assert.NotEqual(epoch, failed.TerritoryWars!.Epoch);
        Assert.Equal(epoch, healthy.TerritoryWars!.Epoch);
    }

    [Fact]
    public async Task SiegeRefreshUsesPublishedRoleAfterLoginSnapshotRelease()
    {
        await using var bot = await Bot("TerrSiegeSnap");
        await WaitSceneOnlinePublicationAsync(bot);
        var session = Session(bot);
        var published = Assert.IsType<TerritoryWarsService.RoleState>(session.TerritoryWars);
        var originalPanel = Assert.IsType<DBRoleSiegeData>(session.Synced[5020]).Clone();
        var roles = Service<RoleStore>();
        var accounts = Service<AccountStore>();
        var wars = Service<TerritoryWarsService>();
        roles.ReleaseLoginSnapshot(session.Role!.Id);
        int blobReads = 0;
        accounts.ReadSyncObserved = (label, onLoop) =>
        {
            if (onLoop && label == "role/read-blob") Interlocked.Increment(ref blobReads);
        };
        using var refresh = bot.Inbox.Expect(e => e.IsGame(5020));
        try
        {
            await OnLoop(async () =>
            {
                // Reconcile must publish an actual full panel, even though its values have not changed.
                session.Synced.Remove(5020);
                typeof(TerritoryWarsService).GetField("_siegePublishedState",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(wars, "");
                await wars.ReconcileAsync();
            });
            var panel = DBRoleSiegeDataIncrement.Parser.ParseFrom((await refresh.WaitAsync(Wait)).Body);
            Assert.True(panel.SyncAllMark);
            Assert.Equal(originalPanel.HonorPoints, panel.HonorPoints);
            Assert.Equal(originalPanel.ValorPoints, panel.ValorPoints);
            Assert.Equal(originalPanel, Assert.IsType<DBRoleSiegeData>(session.Synced[5020]));
            Assert.Same(published, session.TerritoryWars);
            Assert.Equal(0, Volatile.Read(ref blobReads));
        }
        finally { accounts.ReadSyncObserved = null; }
    }

    [ClientDataFact]
    public async Task RestartRetainsEscrowAndAutomaticBidScheduleWithoutChargingTwice()
    {
        long guild;
        await using (var b = await Bot("TerrRestart"))
        {
            guild = await Guild(b, "TwRetry");
            await b.SendGameAsync(TerritoryWarsService.AutoSet, new TerritoryAutoBidSetReq { ZoneId = 2, InitialBid = 1000, MaxBid = 3000, Style = (TerritoryAutoBidStyle)1 });
            await Next(b, TerritoryWarsService.AutoSet, TerritoryAutoBidSetRes.Parser);
            _clock.Set(1779535920); await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
            Assert.Equal(4000, Service<GuildStore>().Get(guild)!.AuctionMaterials);
        }
        await _app.StopAsync(); await _app.DisposeAsync(); await StartAsync();
        await using var again = await Bot("TerrRestart", false);
        _clock.Set(1779535940); await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        Assert.Equal(4000, Service<GuildStore>().Get(guild)!.AuctionMaterials);
        await again.SendGameAsync(TerritoryWarsService.AutoQuery, new TerritoryAutoBidQueryReq());
        var setting = await Next(again, TerritoryWarsService.AutoNotify, TerritoryAutoBidSettingChangeNtf.Parser, x => x.ZoneId == 2);
        Assert.Equal(3000, setting.MaxBid);
        await again.SendGameAsync(TerritoryWarsService.AutoCancel, new TerritoryAutoBidCancelReq { ZoneId = 2 });
        await Next(again, TerritoryWarsService.AutoCancel, TerritoryAutoBidCancelRes.Parser);
        Assert.Equal(4000, Service<GuildStore>().Get(guild)!.AuctionMaterials); // cancelling automation does not withdraw a seated bid.
    }

    [ClientDataFact]
    public async Task TerritoryOwnerPublishesActivityStageWithoutActivityPolling()
    {
        await using var bot = await Bot("WarNotify");
        await OnLoop(async () =>
        {
            Service<GameLoop>().Remove(Service<Deadlines>());
            var session = Session(bot);
            var before = Assert.IsType<DBNewActivityInfo>(session.Synced[NewActivityService.ActivitySync]);
            long oldType = (long)before.ActivityInfo[2000].ActivityParam.TerritoryWar.TerritoryWarStage.StageType;
            _clock.Set(1779622200L);
            await Service<TerritoryWarsService>().ReconcileAsync();
            var current = Assert.IsType<DBNewActivityInfo>(session.Synced[NewActivityService.ActivitySync]);
            var projected = current.ActivityInfo[2000].ActivityParam.TerritoryWar.TerritoryWarStage;
            Assert.NotEqual(oldType, (long)projected.StageType);
            Assert.Equal(Service<TerritoryWarsClock>().Current.Type, (int)projected.StageType);
            // Seconds: the client compares territoryWarStage with GetSystemTime (TerritoryWarsService.BuildActivityState).
            Assert.Equal(Service<TerritoryWarsClock>().Current.StartTime, projected.StartTime);
            Assert.Equal(Service<TerritoryWarsClock>().Current.EndTime, projected.EndTime);
            Assert.Equal(projected.StartTime, current.ActivityInfo[2000].ActivityParam.TerritoryWar.TerritoryWarData.Stage.StartTime);
        });
    }

    [ClientDataFact]
    public async Task ActivityCatalogueAndRefreshKeepMasterActivitiesAndTerritoryStateTogether()
    {
        await using var bot = await Bot("TerrActivities");
        await bot.SendGameAsync(NewActivityService.GetList, new GetActivityListReq());
        var list = await Next(bot, NewActivityService.GetList, GetActivityListResp.Parser);
        Assert.Equal(0, list.Result);
        Assert.Contains(list.ActivityList, a => a.Id == 103); // Master's level rewards remain reachable.
        Assert.Contains(list.ActivityList, a => a.Id == 118); // Master's Prontera defense remains reachable.
        Assert.Contains(list.ActivityList, a => a.Id == 2000);
        Assert.Equal(list.ActivityList.Count, list.ActivityList.Select(a => a.Id).Distinct().Count());
        await OnLoop(async () =>
        {
            var session = Session(bot);
            var expected = session.NewActivities!.ActivityInfo.Keys.Order().ToArray();
            foreach (long now in new[] { 1779535900L, 1779622200L })
            {
                _clock.Set(now);
                await Service<TerritoryWarsService>().ReconcileAsync();
                await Service<NewActivityService>().ReconcileAsync(session);
                var snapshot = Assert.IsType<DBNewActivityInfo>(session.Synced[NewActivityService.ActivitySync]);
                Assert.Equal(expected, snapshot.ActivityInfo.Keys.Order().ToArray());
                Assert.NotNull(snapshot.ActivityInfo[103].ActivityParam.LevelAward);
                Assert.NotNull(snapshot.ActivityInfo[118].ActivityParam.PronteraDefense);
                var territory = snapshot.ActivityInfo[2000].ActivityParam.TerritoryWar;
                Assert.NotNull(territory);
                Assert.Equal(Service<TerritoryWarsClock>().Current.Type, (int)territory.TerritoryWarStage.StageType);
                Assert.Equal(36, territory.TerritoryWarData.ZoneData.ZoneInfo.Count);
            }
        });

        _clock.Set(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        await OnLoop(async () =>
        {
            await Service<TerritoryWarsService>().ReconcileAsync();
            await Service<NewActivityService>().ReconcileAsync(Session(bot));
            var snapshot = Assert.IsType<DBNewActivityInfo>(Session(bot).Synced[NewActivityService.ActivitySync]);
            Assert.False(snapshot.ActivityInfo.ContainsKey(2000));
            Assert.NotNull(snapshot.ActivityInfo[103].ActivityParam.LevelAward);
            Assert.NotNull(snapshot.ActivityInfo[118].ActivityParam.PronteraDefense);
        });
        await bot.SendGameAsync(NewActivityService.GetList, new GetActivityListReq());
        var closed = await Next(bot, NewActivityService.GetList, GetActivityListResp.Parser);
        Assert.Equal(0, closed.Result);
        Assert.DoesNotContain(closed.ActivityList, a => a.Id == 2000);
        Assert.Equal(list.ActivityList.Where(a => a.Id != 2000).Select(a => a.Id).Order(),
            closed.ActivityList.Select(a => a.Id).Order());
    }

    [ClientDataFact]
    public void CalendarUsesExportedAbsoluteWindowsAndSeasonOffsets()
    {
        var schedule = Service<TerritoryWarsClock>();
        Assert.Equal(24, schedule.Stages.Count);
        Assert.Equal(8, schedule.At(1779535900).Type);
        Assert.Equal(10, schedule.At(1779622200).Type);
        Assert.Equal(99, schedule.At(1800000000).Type);
        using var server = JsonDocument.Parse("{\"_iServerOpenStart\":123}");
        Assert.Equal(schedule.SeasonStart + 123, schedule.Resolve(server.RootElement, false));
        using var absolute = JsonDocument.Parse("{\"_iGreenWichStart\":1779535800,\"_iServerOpenStart\":123}");
        Assert.Equal(1779535800, schedule.Resolve(absolute.RootElement, false));
        using var season = JsonDocument.Parse("{\"_iSeasonStartStageInfo\":\"1|0\",\"_iSeasonStartStateInfoTime\":42}");
        Assert.Equal(Service<SeasonService>().Times().StageTimeInfo.Values.Min(s => s.StartTime) + 42, schedule.Resolve(season.RootElement, false));
    }
}
