using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class SiegeTests : IAsyncLifetime
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-siege-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    private sealed class Clock(long start) : TimeProvider
    {
        private long seconds = start;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public override TimeZoneInfo LocalTimeZone => Zone;
        public void Set(long value) => Interlocked.Exchange(ref seconds, value);
    }
    // The exported siege occurrence (DailyActivityConfig 115) in the week of 12 Oct 2026, and the following one.
    private readonly ScheduleRows.Window siege, nextSiege;
    private readonly Clock clock;
    private WebApplication app = null!;
    private string http = "";
    public SiegeTests()
    {
        siege = ScheduleRows.Weekly(115, new DateTime(2026, 10, 12), Zone);
        nextSiege = ScheduleRows.Weekly(115, siege.LocalStart.Date.AddDays(1), Zone);
        clock = new(siege.Start - 1);
    }
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private async Task SeedWarsOnLoopAsync(TerritoryWarsService.WorldState seeded)
    {
        var accounts = Service<AccountStore>();
        using var scope = await Service<GameLoop>().Owners!.EnterAsync("siege/fixture seed",
            ReservationKey.Entity("realm", nameof(RealmStateKind.TerritoryWars)),
            ReservationKey.Entity("territory_season", seeded.Epoch));
        var transaction = accounts.NewTransaction();
        transaction.Add(new RealmStatePlan(RealmStateKind.TerritoryWars,
            JsonSerializer.SerializeToUtf8Bytes(seeded)));
        await transaction.CommitAsync(accounts, "siege/fixture seed");
        typeof(TerritoryWarsService).GetProperty("state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Service<TerritoryWarsService>(), seeded);
    }
    public Task InitializeAsync() => StartAsync();
    private async Task StartAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game; o.PlayBook = 101; o.SeasonStart = "2026-05-19";
            o.GmAccounts = ["siegegm"];
        }, clock);
        SureHitRolls.Install(app); // lethal hits are prepared; a FLEE miss would only be noise
        await app.StartAsync(); http = $"http://127.0.0.1:{web}";
    }
    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }
    private static string RepoRoot()
    {
        for (var p = new DirectoryInfo(AppContext.BaseDirectory); p != null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "Ro3Emu.slnx"))) return p.FullName;
        throw new DirectoryNotFoundException();
    }
    private async Task<BotClient> Bot(string name, bool create = true)
    {
        var b = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single(r => r.Name == name).Id;
        await b.EnterWorldAsync(id); return b;
    }
    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var envelope = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(envelope.Body);
    }
    private async Task<long> Guild(BotClient bot, string name)
    {
        await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(bot), 99, null));
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name,
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 }
        });
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, g => g.Guid > 0)).Guid;
    }
    private async Task<GetSiegeBaseInfoRsp> Base(BotClient bot, int? result = null, SiegeCityActivityState? state = null,
        long? starts = null, int? currentCity = null)
    {
        await bot.SendGameAsync(TerritoryWarsService.SiegeBaseQuery,
            new GetSiegeBaseInfoReq
            {
                PlayerId = Session(bot).Role!.Id,
                GuildId = Service<GuildStore>().ForRole(Session(bot).Role!.Id)?.Id ?? 0
            });
        return await Next(bot, TerritoryWarsService.SiegeBaseQuery, GetSiegeBaseInfoRsp.Parser,
            r => (result == null || r.Result == result) && (state == null || r.SiegeBaseInfo.BattleInfos[1001].ActivityState == state)
                && (starts == null || r.SiegeBaseInfo.BattleInfos[1001].StartTime == starts)
                && (currentCity == null || r.SiegeBaseInfo.CurrentCityId == currentCity));
    }

    [ClientDataFact]
    public async Task PanelUsesTheExportedWindowAndNoHistoryRefusesWithoutFabrication()
    {
        await using var bot = await Bot("SiegePlain");
        var before = await Base(bot);
        Assert.Equal(0, before.Result);
        Assert.Equal(40, before.SiegeBaseInfo.BattleInfos.Count);
        Assert.Equal(0, before.SiegeBaseInfo.CurrentCityId);
        var city = before.SiegeBaseInfo.BattleInfos[1001];
        Assert.Equal(siege.Start, city.StartTime);
        Assert.Equal(siege.End, city.EndTime);
        Assert.Equal(SiegeCityActivityState.NotOpen, city.ActivityState);
        Assert.Equal(0, city.AttackGuildInfo.Guid);
        Assert.Equal(0, Assert.Single(city.AttackGuildInfo.GuildManagerData).PlayerID);
        Assert.Equal("", Assert.Single(city.AttackGuildInfo.HistoryNames));
        clock.Set(siege.Start);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        var open = await Base(bot, state: SiegeCityActivityState.Fighting);
        Assert.Equal(SiegeCityActivityState.Fighting, open.SiegeBaseInfo.BattleInfos[1001].ActivityState);
        clock.Set(siege.End);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        var closed = await Base(bot, state: SiegeCityActivityState.NotOpen,
            starts: nextSiege.Start);
        Assert.Equal(SiegeCityActivityState.NotOpen, closed.SiegeBaseInfo.BattleInfos[1001].ActivityState);
        Assert.Equal(nextSiege.Start, closed.SiegeBaseInfo.BattleInfos[1001].StartTime);
        await bot.SendGameAsync(TerritoryWarsService.SiegeHistoryQuery, new SiegeCityHistoryInfoReq { CityIds = { 1001 } });
        Assert.Equal(ErrorCodes.DataNotFound, (await Next(bot, TerritoryWarsService.SiegeHistoryQuery, SiegeCityHistoryInfoResp.Parser)).Result);
        await bot.SendGameAsync(TerritoryWarsService.SiegeToolList, new GetGuildWartimeToolBelongListReq());
        Assert.Equal(ErrorCodes.TerritoryGuild, (await Next(bot, TerritoryWarsService.SiegeToolList, GetGuildWartimeToolBelongListRsp.Parser)).Result);
        await bot.SendGameAsync(TerritoryWarsService.SiegeToolBuild, new GuildAttackCityBuildReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, TerritoryWarsService.SiegeToolBuild, GuildAttackCityBuildResp.Parser)).Result);
        await bot.SendGameAsync(TerritoryWarsService.SiegeBaseQuery, new GetSiegeBaseInfoReq { PlayerId = Session(bot).Role!.Id + 1 });
        Assert.Equal(ErrorCodes.NoPermission, (await Next(bot, TerritoryWarsService.SiegeBaseQuery, GetSiegeBaseInfoRsp.Parser,
            r => r.Result != 0)).Result);
    }

    [ClientDataFact]
    public async Task GmSeedQualifiesTwoGuildsForALoadTestSiege()
    {
        await using var gm = await Bot("SiegeGm");
        await using var defense = await Bot("SiegeSeedDef");
        long ga = await Guild(gm, "SgSeedA"), gd = await Guild(defense, "SgSeedD");
        await gm.JoinChatAsync("4_1");
        clock.Set(siege.Start + 25);
        await gm.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(gm, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        var panel = await Base(gm, currentCity: 1001);
        Assert.Equal(ga, panel.SiegeBaseInfo.BattleInfos[1001].AttackGuildInfo.Guid);
        Assert.Equal(gd, panel.SiegeBaseInfo.BattleInfos[1001].DefenseGuildInfo.Guid);
        await Enter(gm, 1); await Enter(defense, 2);
        Assert.True(Service<TerritoryWarsService>().Hostile(Session(gm), Session(defense)));
    }

    [ClientDataFact]
    public async Task GmOverrideIsAuthorizedIdempotentAndCloseDeniesEntry()
    {
        await using var gm = await Bot("SiegeGm");
        await using var guest = await Bot("SiegeGuest");
        await gm.JoinChatAsync("4_1"); await guest.JoinChatAsync("4_1");
        clock.Set(siege.Start + 25);
        Assert.Equal(siege.Start, (await Base(gm, state: SiegeCityActivityState.Fighting)).SiegeBaseInfo.BattleInfos[1001].StartTime);
        await guest.SendChatAsync("4_1", 2, "!siege open", channelIndex: 4);
        Assert.Contains("not enabled", (await Next(guest, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        await gm.SendChatAsync("4_1", 2, "!siege open", channelIndex: 4);
        Assert.Contains("Fighting", (await Next(gm, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        Assert.Equal(siege.Start, (await Base(gm, state: SiegeCityActivityState.Fighting)).SiegeBaseInfo.BattleInfos[1001].StartTime);
        clock.Set(siege.Start + 26);
        await gm.SendChatAsync("4_1", 2, "!siege open", channelIndex: 4);
        Assert.Contains("Fighting", (await Next(gm, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        Assert.Equal(siege.Start, (await Base(gm, state: SiegeCityActivityState.Fighting)).SiegeBaseInfo.BattleInfos[1001].StartTime);
        await gm.SendChatAsync("4_1", 2, "!siege close", channelIndex: 4);
        Assert.Contains("NotOpen", (await Next(gm, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        Assert.Equal(nextSiege.Start, (await Base(gm, state: SiegeCityActivityState.NotOpen,
            starts: nextSiege.Start)).SiegeBaseInfo.BattleInfos[1001].StartTime);
        await gm.SendGameAsync(30400, new EnterDungeonReq
        {
            SceneEntry = 2230101,
            InitData = new DungeonInitData { SiegeInitData = new SiegeInitData { CityId = 1001 } }
        });
        Assert.Equal(ErrorCodes.TerritoryWarClosed, (await Next(gm, 30400, EnterDungeonRsp.Parser)).Result);
        clock.Set(siege.End);
        await gm.SendChatAsync("4_1", 2, "!siege close", channelIndex: 4);
        Assert.Contains("NotOpen", (await Next(gm, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        Assert.Equal(nextSiege.Start, (await Base(gm, state: SiegeCityActivityState.NotOpen,
            starts: nextSiege.Start)).SiegeBaseInfo.BattleInfos[1001].StartTime);
        clock.Set(nextSiege.Start);
        Assert.Equal(SiegeCityActivityState.Fighting, (await Base(gm, state: SiegeCityActivityState.Fighting,
            starts: nextSiege.Start)).SiegeBaseInfo.BattleInfos[1001].ActivityState);
    }

    [ClientDataFact]
    public async Task SeniorCitySettlementTransfersConnectingLandsButNotIndependentPoint()
    {
        await using var attack = await Bot("SiegeSeniorAtk");
        await using var defense = await Bot("SiegeSeniorDef");
        long ga = await Guild(attack, "SgSAtk"), gd = await Guild(defense, "SgSDef");
        var guilds = Service<GuildStore>();
        long start = siege.Start;
        await OnLoop(async () =>
        {
            var wars = Service<TerritoryWarsService>();
            var twClock = Service<TerritoryWarsClock>();
            var seeded = new TerritoryWarsService.WorldState { Epoch = $"{twClock.SeasonStart}:{twClock.PlayBook}" };
            seeded.Owners[2] = gd; seeded.Owners[6] = gd; seeded.Owners[7] = gd; seeded.Owners[3] = gd;
            seeded.SiegeRuns[$"{start}:1101"] = new TerritoryWarsService.SiegeRunState
            {
                City = 1101, Start = start, End = start + 3600, Attack = ga, Defense = gd, StatisticsId = 777,
                AttackSnapshot = Service<GuildService>().Snapshot(guilds.Get(ga)!).ToByteArray(),
                DefenseSnapshot = Service<GuildService>().Snapshot(guilds.Get(gd)!).ToByteArray(),
            };
            await SeedWarsOnLoopAsync(seeded);
            clock.Set(start);
            await wars.SettleSiegeForTestAsync($"{start}:1101", ga);
        });
        var current = Service<TerritoryWarsService>();
        Assert.Equal(ga, current.OwnerOf(2));
        Assert.Equal(ga, current.OwnerOf(6));
        Assert.Equal(ga, current.OwnerOf(7));
        Assert.Equal(gd, current.OwnerOf(3));
        await attack.SendGameAsync(TerritoryWarsService.SiegeHistoryQuery, new SiegeCityHistoryInfoReq { CityIds = { 1101 } });
        Assert.Equal(ga, Assert.Single((await Next(attack, TerritoryWarsService.SiegeHistoryQuery,
            SiegeCityHistoryInfoResp.Parser)).Infos).GuildInfo.Guid);
    }

    [ClientDataFact]
    public async Task QualifiedAttackCrystalSettlesOnceAndHistorySurvivesRestart()
    {
        long defenseRole;
        long winningGuild;
        await using (var attack = await Bot("SiegeAttacker"))
        await using (var defense = await Bot("SiegeDefender"))
        await using (var outsider = await Bot("SiegeOutsider"))
        {
            long ga = await Guild(attack, "SgAtk"), gd = await Guild(defense, "SgDef");
            defenseRole = Session(defense).Role!.Id;
            winningGuild = ga;
            await OnLoop(async () =>
            {
                var wars = Service<TerritoryWarsService>(); var twClock = Service<TerritoryWarsClock>();
                var seeded = new TerritoryWarsService.WorldState { Epoch = $"{twClock.SeasonStart}:{twClock.PlayBook}" };
                seeded.Owners[1] = gd;
                seeded.Battles[301] = new TerritoryWarsService.BattleState
                {
                    Zone = 1, Round = 3, Settled = true,
                    Guilds = [ga, gd], Scores = new() { [ga] = 10, [gd] = 5 }
                };
                await SeedWarsOnLoopAsync(seeded);
            });
            Assert.Equal(gd, Service<TerritoryWarsService>().OwnerOf(1));
            Assert.Equal(ga, Service<GuildStore>().ForRole(Session(attack).Role!.Id)?.Id);
            Assert.Equal(1, (int)typeof(TerritoryWarsService).GetMethod("SiegeZone", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service<TerritoryWarsService>(), [1001])!);
            var panel = await Base(attack, currentCity: 1001);
            Assert.Equal(1001, panel.SiegeBaseInfo.CurrentCityId);
            Assert.Equal(ga, panel.SiegeBaseInfo.BattleInfos[1001].AttackGuildInfo.Guid);
            Assert.Equal(gd, panel.SiegeBaseInfo.BattleInfos[1001].DefenseGuildInfo.Guid);
            clock.Set(siege.Start);
            await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
            await outsider.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = 2230101 });
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(outsider, 30400, EnterDungeonRsp.Parser)).Result);
            await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(outsider), 99, null));
            await outsider.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = 2230101 });
            Assert.Equal(ErrorCodes.TerritoryGuild, (await Next(outsider, 30400, EnterDungeonRsp.Parser)).Result);
            await Guild(outsider, "SgOut");
            await outsider.SendGameAsync(30400, new EnterDungeonReq
            {
                SceneEntry = 2230101,
                InitData = new DungeonInitData { SiegeInitData = new SiegeInitData { CityId = 1001 } }
            });
            Assert.Equal(ErrorCodes.TerritoryNotQualified, (await Next(outsider, 30400, EnterDungeonRsp.Parser)).Result);
            await Enter(attack, 1); await Enter(defense, 2);
            Assert.True(Service<TerritoryWarsService>().Hostile(Session(attack), Session(defense)));
            await OnLoop(async () =>
            {
                await Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(153.5f, 27.15f, 119.5f, 0));
                await Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(154.5f, 27.15f, 119.5f, 0));
            });
            await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = defenseRole });
            Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(attack, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.Dsts.Any(d => d.DstID == defenseRole && d.Damage > 0));
            await defense.SendGameAsync(TerritoryWarsService.SiegeResultNotify,
                new SiegeDungeonResultNtf
                {
                    CityId = 1001, History = new SiegeHistoryBattleInfo
                    {
                        ResultInfo = new SiegeResultInfo { Result = 2 }
                    }
                });
            Assert.Equal(gd, Service<TerritoryWarsService>().OwnerOf(1));
            var run = Session(attack).SiegeBattle!;
            await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(defense),
                Service<Ro3Emu.Server.Maps.WorldData>().Map(2230101)!.Points[44]));
            await defense.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = run.Crystal!.Uid });
            Assert.Equal(ErrorCodes.SkillTargetInvalid, (await Next(defense, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            Assert.False(run.Crystal!.Dead);
            await defense.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
            Assert.Equal(0, (await Next(defense, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
            await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
            Assert.Null(Session(defense).SiegeBattle);
            Assert.Equal(0, Session(defense).EventFaction);
            Assert.False(Service<TerritoryWarsService>().Hostile(Session(attack), Session(defense)));
            // Guild departure sends a real return handoff; finish it before advancing the load deadline clock.
            var returned = await Next(defense, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            await defense.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = returned.SceneEntry });
            Assert.Equal(0, (await Next(defense, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(defense, MsgId.EntitySelf, EntitySelf.Parser);
            // CombatService's action tail uses MonsterService.NowMs (real time), not this test's window clock.
            await Task.Delay(1200);
            clock.Set(siege.Start + 60);
            await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
            var point = Service<WorldData>().Map(2230101)!.Points[44];
            await OnLoop(async () =>
            {
                run.Crystal!.Hp = 1;
                await Service<SceneService>().TeleportInMapAsync(Session(attack), point);
            });
            await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = run.Crystal!.Uid });
            Assert.Equal(ErrorCodes.SkillBlocked, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(150.5f, point.Y, 119.5f, 0)));
            await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = run.Crystal!.Uid });
            Assert.Equal(ErrorCodes.SkillBlocked, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(153.5f, point.Y, 119.5f, 0)));
            await OnLoop(() => { Session(attack).Progress!.Skills[11104] = 1; return Task.CompletedTask; });
            await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            {
                SkillId = 1110401, TargetId = run.Crystal!.Uid,
                TargetPos = new Romsg.Vec3 { X = 151.5f, Y = point.Y, Z = 119.5f }
            });
            Assert.Equal(ErrorCodes.SkillBlocked, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var decoy = await Service<GameLoop>().InvokeAsync(() => Service<InstanceService>().SpawnAsync(run.Room, 4900009,
                Service<WorldData>().Map(2230101)!.Grounded(point)));
            await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = decoy!.Uid });
            Assert.Equal(ErrorCodes.SkillBlocked, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var combat = Service<CombatService>();
            var attackerSession = Session(attack);
            var crystal = run.Crystal!;
            ActiveSkillCast? initiatingCast = null;
            Func<GameSession, MonsterUnit, long, Task> captureLethalCast = (killer, target, _) =>
            {
                if (ReferenceEquals(killer, attackerSession) && ReferenceEquals(target, crystal))
                    initiatingCast = killer.SkillCast;
                return Task.CompletedTask;
            };
            var cancelled = new TaskCompletionSource<(ActiveSkillCast? Cast, bool Dead)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<GameSession, MonsterUnit, Task> cancelAfterLethal = (killer, target) =>
            {
                if (!ReferenceEquals(killer, attackerSession) || !ReferenceEquals(target, crystal)) return Task.CompletedTask;
                var cast = initiatingCast ?? killer.SkillCast;
                if (cast != null) cast.Cancelled = true; // dead-target auto attack can cancel while kill callbacks yield.
                cancelled.TrySetResult((cast, target.Dead));
                return Task.CompletedTask;
            };
            await OnLoop(() =>
            {
                combat.BeforeLethalPublication.Insert(0, captureLethalCast);
                combat.Killed.Insert(0, cancelAfterLethal);
                return Task.CompletedTask;
            });
            SiegeDungeonResultNtf result;
            try
            {
                await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = crystal.Uid });
                Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await Next(attack, CombatService.SkillHit, SkillHitInfo.Parser,
                    h => h.Dsts.Any(d => d.DstID == crystal.Uid && d.IsDead));
                result = await Next(attack, TerritoryWarsService.SiegeResultNotify, SiegeDungeonResultNtf.Parser);
            }
            finally
            {
                await OnLoop(() =>
            {
                combat.Killed.Remove(cancelAfterLethal);
                combat.BeforeLethalPublication.Remove(captureLethalCast);
                return Task.CompletedTask;
            });
            }
            var (cancelledCast, crystalWasDead) = await cancelled.Task.WaitAsync(Wait);
            Assert.NotNull(cancelledCast);
            Assert.True(crystalWasDead);
            Assert.True(cancelledCast.Cancelled);
            Assert.Equal(1001, result.CityId);
            Assert.Equal(1, result.History.ResultInfo.Result);
            Assert.Equal(ga, result.History.ResultInfo.WinGuildInfo.Guid);
            var honorSync = await Next(attack, 5020, DBRoleSiegeDataIncrement.Parser, d => d.HonorPoints == 1);
            Assert.Equal(1, honorSync.HonorPoints);
            var notifiedStats = result.History.CommonResultInfo.SceneStatistics.Values.Single().RoleStatistics[Session(attack).Role!.Id];
            Assert.Equal(1, notifiedStats.ExtraData[1021007].LongValue); // CurrentScore
            Assert.True(notifiedStats.ExtraData[1001001].LongValue > 0); // Damage
            Assert.Equal(0, notifiedStats.ExtraData[1001004].LongValue); // KillPlayer
            Assert.Contains(1001002, notifiedStats.ExtraData.Keys); // TakeDamage
            Assert.Contains(1001003, notifiedStats.ExtraData.Keys); // heal
            Assert.Equal(ga, Service<TerritoryWarsService>().OwnerOf(1));
            await defense.SendGameAsync(TerritoryWarsService.SiegeHistoryQuery, new SiegeCityHistoryInfoReq { CityIds = { 1001 } });
            var history = await Next(defense, TerritoryWarsService.SiegeHistoryQuery, SiegeCityHistoryInfoResp.Parser);
            Assert.Equal(0, history.Result);
            Assert.Equal(ga, Assert.Single(history.Infos).GuildInfo.Guid);
            var storedStats = Assert.Single(history.Infos).BattleInfo.CommonResultInfo.SceneStatistics.Values.Single()
                .RoleStatistics[Session(attack).Role!.Id];
            Assert.Equal(notifiedStats.ExtraData[1021007].LongValue, storedStats.ExtraData[1021007].LongValue);
            var box = JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(defenseRole)!)!;
            var loserMail = Assert.Single(box.Letters, m => m.DeliveryKey.StartsWith("siege:"));
            Assert.Equal(10501, loserMail.TemplateId);
            Assert.Equal(new[] { new MailService.Attachment(1250709, 10), new MailService.Attachment(1, 5000) }, loserMail.Attachments);
            var winnerBox = JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(Session(attack).Role!.Id)!)!;
            var winnerMail = Assert.Single(winnerBox.Letters, m => m.DeliveryKey.StartsWith("siege:"));
            Assert.Equal(10500, winnerMail.TemplateId);
            Assert.Equal(new[] { new MailService.Attachment(1250709, 20), new MailService.Attachment(1, 10000) }, winnerMail.Attachments);
            await OnLoop(() => Task.CompletedTask); // settlement fanout has finished before inspecting the departed client.
            Assert.DoesNotContain(defense.Inbox.Snapshot(), e => e.IsGame(TerritoryWarsService.SiegeResultNotify));
            await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
            var after = JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(defenseRole)!)!;
            Assert.Single(after.Letters, m => m.DeliveryKey.StartsWith("siege:"));
        }
        await app.StopAsync(); await app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
        await using var returning = await Bot("SiegeDefender", create: false);
        Assert.Equal(winningGuild, Service<TerritoryWarsService>().OwnerOf(1));
        var fullSiege = await Next(returning, 5020, DBRoleSiegeDataIncrement.Parser, d => d.SyncAllMark);
        Assert.Equal(0, fullSiege.ValorPoints);
        await returning.SendGameAsync(TerritoryWarsService.SiegeHistoryQuery, new SiegeCityHistoryInfoReq { CityIds = { 1001 } });
        var persisted = await Next(returning, TerritoryWarsService.SiegeHistoryQuery, SiegeCityHistoryInfoResp.Parser);
        Assert.Equal(winningGuild, Assert.Single(persisted.Infos).GuildInfo.Guid);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        var mailbox = JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(defenseRole)!)!;
        Assert.Single(mailbox.Letters, m => m.DeliveryKey.StartsWith("siege:"));
    }

    private async Task Enter(BotClient bot, int expectedCamp)
    {
        await bot.SendGameAsync(30400, new EnterDungeonReq { SceneEntry = 2230101 });
        Assert.Equal(0, (await Next(bot, 30400, EnterDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 2230101);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, s => s.SceneId == 2230101);
        var self = await Next(bot, MsgId.EntitySelf, EntitySelf.Parser,
            e => e.Base.Uuid == (ulong)Session(bot).Role!.Id && e.Base.EntityCamp == expectedCamp);
        Assert.Equal(expectedCamp, self.Base.EntityCamp);
        var info = await Next(bot, 30401, DungeonBaseInfoNotify.Parser, i => i.DungeonEntry == 2230101);
        Assert.Equal(1001, info.InitData.SiegeInitData.CityId);
    }
}
