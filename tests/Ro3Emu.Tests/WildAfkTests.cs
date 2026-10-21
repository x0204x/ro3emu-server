using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>OnHook requests and 5035/5027/5004 through the real game gate; clock/HP fixtures shorten long fights/days.</summary>
public sealed class WildAfkTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-afk-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        Service<SeasonService>().SetPlayBook(101);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name = "Alice", bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
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
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private Task OnOwnedLoop(BotClient bot, string origin, Func<GameSession, Task> work)
    {
        var session = Session(bot);
        return OnLoop(() => Service<OwnerRegistry>().RunSessionAsync(origin,
            [ReservationKey.Role(session.Role!.Id)], session, () => work(session)));
    }
    private Task<T> State<T>(BotClient bot, Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session(bot))));

    private async Task Eligible(BotClient bot)
    {
        await Gm(bot, "!lvl 37");
        await Gm(bot, "!quest done 10105090");
    }

    private async Task Warp(BotClient bot, int scene)
    {
        await Gm(bot, $"!warp {scene}");
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(scene, sw.SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
    }

    private static async Task<long> Query(BotClient bot)
    {
        await bot.SendGameAsync(WildAfkService.Info, new WildAFKInfoReq());
        return (await Next(bot, WildAfkService.Info, WildAFKInfoRsp.Parser)).CurAfkTime;
    }

    private async Task Auto(BotClient bot, bool enabled)
    {
        await bot.SendGameAsync(31054, new SetAutoBattleReq { IsAutoBattle = enabled });
        await Query(bot); // request/response barrier for NullArg 31054
    }

    [ClientDataFact]
    public async Task DirectRoleSelectionPersistsOldAfkAllowanceAndFraction()
    {
        await using var bot = await InWorld();
        var oldSession = Session(bot);
        long alice = oldSession.Role!.Id;
        var oldState = oldSession.WildAfk!;
        var bob = (await bot.CreateRoleAsync("Bob")).RoleBaseInfo.Id;

        await OnOwnedLoop(bot, "AFK role-switch fixture", _ =>
        {
            // Leave this state pending until role selection; the periodic ticker must not save it first.
            Service<GameLoop>().Remove(Service<WildAfkService>());
            oldState.Info.HighMultiplierTimeSeconds = 1234;
            oldState.RemainderMs = 427;
            oldState.ConsumedTodaySeconds = 17;
            return Task.CompletedTask;
        });
        await bot.EnterWorldAsync(bob); // direct LoginRoleSelect while Alice still owns the session

        var saved = JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk(alice)!)!;
        Assert.Equal(1234, saved.Info.HighMultiplierTimeSeconds);
        Assert.Equal(427, saved.RemainderMs);
        Assert.Equal(17, saved.ConsumedTodaySeconds);
        Assert.NotSame(oldState, oldSession.WildAfk);
        Assert.Equal(bob, oldSession.Role!.Id);
        Assert.Equal(3600, oldSession.WildAfk!.Info.HighMultiplierTimeSeconds);
        Assert.Equal(0, oldSession.WildAfk.RemainderMs);
        Assert.False(oldSession.AutoBattle);
    }

    private async Task<int> Kill(BotClient bot)
    {
        long uid = 0;
        int npc = 0;
        Vec3 position = null!;
        await OnLoop(() =>
        {
            var s = Session(bot);
            var m = Service<World>().Map(s.WorldScene).Units.OfType<MonsterUnit>()
                .First(m => !m.Dead && m.Template.NpcId == 5000001);
            m.Hp = 1; m.Moving = false; m.NextRoamMs = long.MaxValue;
            uid = m.Uid; npc = m.Template.NpcId; position = m.Pos;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, x => x.Dsts.Any(d => d.DstID == uid && d.IsDead));
        // LC_DAL_OnHook applies optional increment scalars: resuming must explicitly carry false.
        var resumed = await Next(bot, WildAfkService.Sync, DBWildAFKInfoIncrement.Parser,
            x => x.HasTodayKillCount && x.TodayKillCount > 0);
        Assert.True(resumed.HasIsSuspended);
        Assert.False(resumed.IsSuspended);
        return npc;
    }

    [ClientDataFact]
    public async Task LoginInitializes5035AndLockedOrDungeonRequestsAreRefused()
    {
        await using var bot = await InWorld();
        var info = await Next(bot, WildAfkService.Sync, DBWildAFKInfoIncrement.Parser);
        Assert.True(info.SyncAllMark);
        Assert.True(info.IsInit);
        Assert.Equal(3600, info.HighMultiplierTimeSeconds);
        Assert.True(info.IsSuspended);
        await bot.SendGameAsync(WildAfkService.Start, new StartHighMultiplierTimeReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, WildAfkService.Start, StartHighMultiplierTimeRsp.Parser)).Result);
        await bot.SendGameAsync(WildAfkService.Collection, new SetMonsterCollectionReq { MonsterId = 5000001, IsAdd = true });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, WildAfkService.Collection, SetMonsterCollectionRsp.Parser)).Result);
        await Eligible(bot);
        await Warp(bot, 10141); // MapConfig._iSceneType=6, sc_iz_dun01_001_sceneeditor
        await bot.SendGameAsync(WildAfkService.Start, new StartHighMultiplierTimeReq());
        Assert.Equal(ErrorCodes.AfkDungeon, (await Next(bot, WildAfkService.Start, StartHighMultiplierTimeRsp.Parser)).Result);
        await bot.SendGameAsync(WildAfkService.OfflineReward, new ReceiveOfflineRewardReq());
        Assert.Equal(ErrorCodes.AfkNoSummary, (await Next(bot, WildAfkService.OfflineReward, ReceiveOfflineRewardRsp.Parser)).Result);
        Assert.Equal(3600, await Query(bot));
    }

    [ClientDataFact]
    public async Task FavoritesValidateCapDuplicatesRemovalAndSurviveReconnect()
    {
        long role;
        await using (var bot = await InWorld())
        {
            await Eligible(bot);
            role = (long)bot.Self!.Uuid;
            async Task<int> Collect(int id, bool add)
            {
                await bot.SendGameAsync(WildAfkService.Collection, new SetMonsterCollectionReq { MonsterId = id, IsAdd = add });
                return (await Next(bot, WildAfkService.Collection, SetMonsterCollectionRsp.Parser)).Result;
            }
            Assert.Equal(ErrorCodes.AfkCollectionInvalid, await Collect(-9, true));
            Assert.Equal(0, await Collect(5000001, true));
            Assert.Equal(ErrorCodes.AfkCollectionExists, await Collect(5000001, true));
            Assert.Equal(ErrorCodes.AfkCollectionMissing, await Collect(5000002, false));
            for (int i = 2; i <= 7; i++) Assert.Equal(0, await Collect(5000000 + i, true));
            Assert.Equal(ErrorCodes.AfkCollectionFull, await Collect(5000008, true));
            Assert.Equal(0, await Collect(5000001, false));
            Assert.Equal(0, await Collect(5000008, true));
        }
        await using var again = await InWorld(create: false);
        var info = await Next(again, WildAfkService.Sync, DBWildAFKInfoIncrement.Parser);
        Assert.Equal(Enumerable.Range(5000002, 7), info.MonsterCollection.All.Value);
        var saved = JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk(role)!)!;
        Assert.Equal(info.MonsterCollection.All.Value, saved.Info.MonsterCollection);
        Assert.Equal(3600, info.HighMultiplierTimeSeconds);
    }

    [ClientDataFact]
    public async Task RealFieldKillResumesTimerAndProducesPersistentDropsAndSnapshots()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        await Warp(bot, 10111);
        await Auto(bot, true);
        Assert.True(await State(bot, s => s.WildAfk!.Info.IsSuspended));
        int npc = await Kill(bot);
        Assert.False(await State(bot, s => s.WildAfk!.Info.IsSuspended));
        await Task.Delay(1200);
        Assert.InRange(await Query(bot), 3597, 3599);
        var progress = await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser,
            x => x.Infos.TryGetValue(500130, out var q) && q.Inc?.CompleteNum?.All?.Value.Any(n => n > 0) == true);
        Assert.NotNull(progress);

        await OnOwnedLoop(bot, "AFK snapshot fixture", _ =>
        {
            Session(bot).WildAfk!.Info.CurrentSnapshot.Timestamp -= 6;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(WildAfkService.Snapshot, new GenerateAFKDropSnapshotReq());
        Assert.Equal(0, (await Next(bot, WildAfkService.Snapshot, GenerateAFKDropSnapshotRsp.Parser)).Result);
        await bot.SendGameAsync(WildAfkService.Pause, new PauseHighMultiplierTimeReq());
        var paused = await Next(bot, WildAfkService.Pause, PauseHighMultiplierTimeRsp.Parser);
        Assert.Equal(0, paused.Result);
        Assert.True(paused.ConsumedTimeSeconds >= 1);
        var info = await Next(bot, WildAfkService.Sync, DBWildAFKInfoIncrement.Parser,
            x => x.SyncAllMark && !x.IsHighMagnification && x.CurrentSnapshot != null);
        var record = Assert.Single(info.DailyDropRecords).Value.All;
        Assert.Equal(1, record.MonsterKillCount[npc]);
        Assert.True(record.ItemDropCount[100] > 0);
        Assert.Equal(record.ItemDropCount[100], info.CurrentSnapshot.All.ItemDropCount[100]);
        Assert.Empty(info.PreviousSnapshot.All.ItemDropCount);
        Assert.Equal(paused.RemainingTimeSeconds, await Query(bot));
        await bot.SendGameAsync(WildAfkService.Snapshot, new GenerateAFKDropSnapshotReq());
        Assert.Equal(ErrorCodes.Cooldown, (await Next(bot, WildAfkService.Snapshot, GenerateAFKDropSnapshotRsp.Parser)).Result);
        var saved = JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk((long)bot.Self!.Uuid)!)!;
        Assert.Equal(info.CurrentSnapshot.All, saved.Info.CurrentSnapshot);
    }

    [ClientDataFact]
    public async Task ManualFieldKillUsesReducedExpAndBonusKillUsesConfiguredHigherTable()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        await Warp(bot, 10113); // Yoyo is level 30; player level 37 gives a 70% correction for both tables.

        async Task<int> KillYoyoAndReadExp(int killCount)
        {
            long uid = 0;
            Vec3 position = null!;
            await OnLoop(() =>
            {
                var s = Session(bot);
                var monster = Service<World>().Map(s.WorldScene).Units.OfType<MonsterUnit>()
                    .First(m => !m.Dead && m.Template.NpcId == 5000021);
                monster.Hp = 1; monster.Moving = false; monster.NextRoamMs = long.MaxValue;
                uid = monster.Uid; position = monster.Pos;
                return Task.CompletedTask;
            });
            await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, x => x.Dsts.Any(d => d.DstID == uid && d.IsDead));
            var reward = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
                x => x.RewardType.Contains(1) && x.Rewards.Any(i => i.ItemEntry == 100 && i.Stack < 1000));
            await Next(bot, WildAfkService.Sync, DBWildAFKInfoIncrement.Parser,
                x => x.HasTodayKillCount && x.TodayKillCount == killCount);
            return reward.Rewards.Single(i => i.ItemEntry == 100).Stack;
        }

        Assert.Equal(37, await State(bot, s => s.Progress!.Level));
        int reduced = await KillYoyoAndReadExp(1);
        Assert.Equal(21, reduced); // NPCDropConfig env 0: item 100 x30, corrected by 70%.
        await Auto(bot, true);
        int bonus = await KillYoyoAndReadExp(2);
        Assert.Equal(207, bonus); // NPCDropConfig env 1: item 100 x296, same correction.
        Assert.True(bonus > reduced);
    }

    [ClientDataFact]
    public async Task MonitorCapsIdleChargeAndAutoOffPauseAndSceneExitStopConsumption()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        await Warp(bot, 10111);
        await Auto(bot, true);
        await Kill(bot);
        await OnOwnedLoop(bot, "AFK idle fixture", _ =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var s = Session(bot).WildAfk!;
            s.Info.HighMultiplierTimeSeconds = 3600; s.Info.IsSuspended = false;
            s.AccountedAtMs = now - 60_000; s.LastKillAtMs = now - 60_000; s.RemainderMs = 0;
            return Task.CompletedTask;
        });
        Assert.Equal(3590, await Query(bot)); // exactly the ten-second grace, never a full idle minute
        Assert.True(await State(bot, s => s.WildAfk!.Info.IsSuspended));
        Assert.Equal("NoKillTimeout", (await Next(bot, WildAfkService.SuspendReason, SuspendReasonRes.Parser)).Reason);
        Assert.Equal(3590, await Query(bot));
        await Auto(bot, false);
        Assert.Equal(3590, await Query(bot));
        await Warp(bot, 10100);
        Assert.Equal(3590, await Query(bot));
        await bot.SendGameAsync(WildAfkService.Pause, new PauseHighMultiplierTimeReq());
        Assert.Equal(0, (await Next(bot, WildAfkService.Pause, PauseHighMultiplierTimeRsp.Parser)).Result);
        await bot.SendGameAsync(WildAfkService.Pause, new PauseHighMultiplierTimeReq());
        Assert.Equal(ErrorCodes.AfkNotActive, (await Next(bot, WildAfkService.Pause, PauseHighMultiplierTimeRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task ConsumedHourCompletesDailyRowAndAwardsLivenessExactlyOnce()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        await Warp(bot, 10111);
        await Auto(bot, true);
        await Kill(bot);
        await OnOwnedLoop(bot, "AFK final-second fixture", _ =>
        {
            var s = Session(bot).WildAfk!;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s.ConsumedTodaySeconds = 3599;
            s.Info.HighMultiplierTimeSeconds = 1; s.Info.IsSuspended = false;
            s.LastKillAtMs = now; s.AccountedAtMs = now - 1500; s.RemainderMs = 0;
            return Task.CompletedTask;
        });
        Assert.Equal(0, await Query(bot));
        await Next(bot, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser,
            x => x.ActivityData.TryGetValue(106, out var row) && row.Inc?.TaskCompleteTimes == 1);
        Assert.Equal(40, await State(bot, s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Equal(1, await State(bot, s => s.Tasks!.CompleteTaskNum[500130]));
        Assert.False(await State(bot, s => s.WildAfk!.Info.IsHighMagnification));
        await bot.SendGameAsync(WildAfkService.Start, new StartHighMultiplierTimeReq());
        Assert.Equal(ErrorCodes.AfkTimeEmpty, (await Next(bot, WildAfkService.Start, StartHighMultiplierTimeRsp.Parser)).Result);
        await Query(bot);
        await OnLoop(() => Service<TaskService>().RefreshAsync(Session(bot)));
        Assert.Equal(40, await State(bot, s => s.Progress!.Currency.GetValueOrDefault(5)));
    }

    [ClientDataFact]
    public async Task DailyRestoreAddsSixtyMinutesCapsBankAndDoesNotReplayOnRelog()
    {
        long remaining;
        await using (var bot = await InWorld())
        {
            await Eligible(bot);
            await OnOwnedLoop(bot, "AFK daily restore fixture", _ =>
            {
                var state = Session(bot).WildAfk!;
                state.Info.HighMultiplierTimeSeconds = 1200;
                state.Info.IsHighMagnification = false;
                state.Info.TodayKillCount = 7;
                state.ConsumedTodaySeconds = 2400;
                state.DailyEpoch = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(state.DailyEpoch).LocalDateTime.AddDays(-1)).ToUnixTimeSeconds();
                return Task.CompletedTask;
            });
            Assert.Equal(4800, await Query(bot));
            Assert.Equal(0, await State(bot, s => s.WildAfk!.Info.TodayKillCount));
            Assert.Equal(0, await State(bot, s => s.WildAfk!.ConsumedTodaySeconds));
            Assert.True(await State(bot, s => s.WildAfk!.Info.IsHighMagnification));
            await OnOwnedLoop(bot, "AFK bank cap fixture", _ =>
            {
                var state = Session(bot).WildAfk!;
                state.DailyEpoch = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(state.DailyEpoch).LocalDateTime.AddDays(-7)).ToUnixTimeSeconds();
                return Task.CompletedTask;
            });
            remaining = await Query(bot);
            Assert.Equal(10800, remaining);
        }
        await using var again = await InWorld(create: false);
        var info = await Next(again, WildAfkService.Sync, DBWildAFKInfoIncrement.Parser);
        Assert.Equal(remaining, info.HighMultiplierTimeSeconds);
        Assert.True(info.IsSuspended);
        Assert.False(await State(again, s => s.AutoBattle));
        Assert.Equal(remaining, await Query(again));
    }

    [ClientDataFact]
    public async Task FinalSecondBeforeDailyBoundarySettlesBeforeQuestReset()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        var s = Session(bot);
        // Match the daily reconciler's ownership while observing the old-day transition;
        // the next ordinary AFK tick may legitimately restore this same object for the new day.
        await OnLoop(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunSessionAsync("AFK boundary test",
            [Ro3Emu.Server.Game.Ownership.ReservationKey.Role(s.Role!.Id)], s, async () =>
        {
            long boundary = s.Daily!.DailyEpoch;
            long old = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(boundary).LocalDateTime.AddDays(-1)).ToUnixTimeSeconds();
            s.Daily.DailyEpoch = old;
            Service<RoleStore>().SaveDaily(s.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
            var afk = s.WildAfk!;
            afk.DailyEpoch = old;
            afk.ConsumedTodaySeconds = 3599;
            afk.Info.HighMultiplierTimeSeconds = 1;
            afk.Info.IsHighMagnification = true;
            afk.Info.IsSuspended = false;
            afk.RemainderMs = 800;
            afk.AccountedAtMs = boundary * 1000 - 500;
            afk.LastKillAtMs = boundary * 1000 - 1000;
            await Service<DailyActivityService>().ReconcileAsync(s);
            // The final second reached the 3600 s threshold before the old epoch was removed (its 40 liveness is
            // paid below), and the same reset command restored the new day's allowance and persisted it with the
            // daily rows in one transaction (C/D 09), instead of a later separate AFK restore.
            Assert.Equal(boundary, afk.DailyEpoch);
            Assert.Equal(0, afk.ConsumedTodaySeconds);
            Assert.Equal(3600, afk.Info.HighMultiplierTimeSeconds);
            var stored = JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk(s.Role!.Id)!)!;
            Assert.Equal(boundary, stored.DailyEpoch);
            Assert.Equal(3600, stored.Info.HighMultiplierTimeSeconds);
            Assert.Equal(0, afk.RemainderMs); // the 300ms beyond the exhausted old bank cannot debit tomorrow
            Assert.Equal(0, s.DailyRows!.ActivityData[106].TaskCompleteTimes);
        }));
        var paid = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
            x => x.Rewards.Any(i => i.ItemEntry == 5 && i.Stack == 40));
        Assert.Single(paid.Rewards, i => i.ItemEntry == 5);
        await Query(bot);
        Assert.InRange(await State(bot, s => s.WildAfk!.ConsumedTodaySeconds), 0, 10);
        Assert.Equal(0, await State(bot, s => s.DailyRows!.ActivityData[106].TaskCompleteTimes));
    }

    /// <summary>Puts the role's daily and AFK periods one day back (session and stored rows), AFK paused with a
    /// 1200 s bank and 2400 s used, and the given liveness. Returns (old epoch, current boundary).</summary>
    private async Task<(long Old, long Boundary)> YesterdayPeriod(BotClient bot, long liveness, Action? sameReservation = null)
    {
        // Under the role's reservation like any game operation: an in-flight award from Eligible's quest
        // completion must not stage a copy of the old progress and overwrite this fixture's values after it.
        var session = Session(bot);
        return await Service<GameLoop>().InvokeAsync(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunAsync(
            "afk test period", [Ro3Emu.Server.Game.Ownership.ReservationKey.Role(session.Role!.Id)], () =>
            Task.FromResult(SetYesterdayPeriod(session, liveness))));
    }

    private (long Old, long Boundary) SetYesterdayPeriod(GameSession s, long liveness)
    {
        long boundary = s.Daily!.DailyEpoch;
        long old = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(boundary).LocalDateTime.AddDays(-1)).ToUnixTimeSeconds();
        s.Daily.DailyEpoch = old;
        var afk = s.WildAfk!;
        afk.DailyEpoch = old;
        afk.Info.HighMultiplierTimeSeconds = 1200;
        afk.Info.IsHighMagnification = false;
        afk.ConsumedTodaySeconds = 2400;
        s.Progress!.Currency[5] = liveness;
        Service<RoleStore>().SaveDaily(s.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
        Service<RoleStore>().SaveWildAfk(s.Role.Id, JsonSerializer.SerializeToUtf8Bytes(afk));
        Service<RoleStore>().SaveProgress(s.Role.Id, s.Progress);
        return (old, boundary);
    }

    private Task<T> Stored<T>(BotClient bot, Func<long, RoleStore, T> read) =>
        State(bot, s => read(s.Role!.Id, Service<RoleStore>()));

    [ClientDataFact]
    public async Task FailedDailyResetCommitKeepsTheWholeOldPeriodAndRetriesAsOneTransaction()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        var s = Session(bot);
        long boundary = 0;
        await OnLoop(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunSessionAsync("daily reset fault test",
            [Ro3Emu.Server.Game.Ownership.ReservationKey.Role(s.Role!.Id)], s, async () =>
        {
            // Keep setup, fault admission and retry under one owner so periodic resets cannot win the gaps.
            var period = SetYesterdayPeriod(s, liveness: 25);
            long old = period.Old;
            boundary = period.Boundary;
            FixtureSql.Execute(Service<AccountStore>(),
                "CREATE TRIGGER fail_daily_reset BEFORE UPDATE OF daily ON role BEGIN SELECT RAISE(ABORT, 'daily reset fixture'); END");
            await Assert.ThrowsAnyAsync<Exception>(() => Service<DailyActivityService>().ReconcileAsync(s));
            // Nothing of the new period is visible: not the daily epoch, not the AFK allowance, not the currency.
            Assert.Equal(old, s.Daily!.DailyEpoch);
            Assert.Equal(old, s.WildAfk!.DailyEpoch);
            Assert.Equal(1200, s.WildAfk.Info.HighMultiplierTimeSeconds);
            Assert.Equal(2400, s.WildAfk.ConsumedTodaySeconds);
            Assert.Equal(25, s.Progress!.Currency.GetValueOrDefault(5));
            Assert.Equal(old, JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk(s.Role!.Id)!)!.DailyEpoch);
            Assert.Equal(old, JsonSerializer.Deserialize<DailyActivityService.DailyState>(Service<RoleStore>().LoadDaily(s.Role.Id)!)!.DailyEpoch);
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_daily_reset");
            await Service<DailyActivityService>().ReconcileAsync(s);
        }));
        Assert.Equal(boundary, await State(bot, x => x.Daily!.DailyEpoch));
        Assert.Equal(boundary, await State(bot, x => x.WildAfk!.DailyEpoch));
        Assert.Equal(4800, await State(bot, x => x.WildAfk!.Info.HighMultiplierTimeSeconds));
        Assert.Equal(0, await State(bot, x => x.Progress!.Currency.GetValueOrDefault(5)));
        var afkRow = await Stored(bot, (id, r) => JsonSerializer.Deserialize<WildAfkService.State>(r.LoadWildAfk(id)!)!);
        Assert.Equal(boundary, afkRow.DailyEpoch);
        Assert.Equal(4800, afkRow.Info.HighMultiplierTimeSeconds);
        Assert.Equal(0, afkRow.ConsumedTodaySeconds);
        Assert.Equal(4800, await Query(bot)); // no second restore after the committed one
    }

    [ClientDataFact]
    public async Task DailyTickAfkTickAndClaimAtTheBoundaryResetThePeriodExactlyOnce()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        var (_, boundary) = await YesterdayPeriod(bot, liveness: 1000);
        var s = Session(bot);
        var accounts = Service<AccountStore>();
        int resets = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13e/daily-reset") return Task.CompletedTask;
            if (Interlocked.Increment(ref resets) != 1) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            // The daily ticker's reset holds its commit; the AFK timer query, a stage claim and a second daily
            // reconcile arrive while it is held and must all observe the new period, not reset it again.
            var first = OnLoop(() => Service<DailyActivityService>().ReconcileAsync(s));
            await entered.Task.WaitAsync(Wait);
            await bot.SendGameAsync(WildAfkService.Info, new WildAFKInfoReq());
            await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
            var second = OnLoop(() => Service<DailyActivityService>().ReconcileAsync(s));
            Assert.False(first.IsCompleted);
            release.TrySetResult();
            await first;
            await second;
            Assert.Equal(4800, (await Next(bot, WildAfkService.Info, WildAFKInfoRsp.Parser)).CurAfkTime);
            var claim = await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser);
            Assert.NotEqual(0, claim.Result); // yesterday's 1000 liveness was reset before the claim was decided
            Assert.Equal(1, Volatile.Read(ref resets));
            Assert.Equal(boundary, await State(bot, x => x.Daily!.DailyEpoch));
            Assert.Equal(0, await State(bot, x => x.Progress!.Currency.GetValueOrDefault(5)));
            Assert.Equal(4800, await Query(bot));
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
    }

    [ClientDataFact]
    public async Task DailyQuestResetCannotReplayYesterdayBeforeAfkTickerRestores()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        await OnOwnedLoop(bot, "AFK daily quest reset fixture", async s =>
        {
            long old = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(s.Daily!.DailyEpoch).LocalDateTime.AddDays(-1)).ToUnixTimeSeconds();
            s.Daily.DailyEpoch = old;
            s.WildAfk!.DailyEpoch = old;
            s.WildAfk.ConsumedTodaySeconds = 3600;
            s.Progress!.Currency[5] = 40;
            s.DailyRows!.ActivityData[106].TaskCompleteTimes = 1;
            s.Daily.Rows = s.DailyRows.ToByteArray();
            s.Tasks!.CompleteTaskNum[500130] = 1;
            Service<RoleStore>().SaveDaily(s.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
            Service<RoleStore>().SaveTasks(s.Role.Id, s.Tasks.ToByteArray());
            Service<RoleStore>().SaveProgress(s.Role.Id, s.Progress);
            // Daily's ticker is registered first; exercise that exact ordering before the AFK owner's tick.
            await Service<DailyActivityService>().ReconcileAsync(s);
            await Service<TaskService>().RefreshAsync(s);
            Assert.Equal(0, s.DailyRows!.ActivityData[106].TaskCompleteTimes);
            Assert.Equal(0, s.Progress.Currency.GetValueOrDefault(5));
            Assert.Equal(0, s.Tasks!.Infos[500130].CompleteNum[0]);
        });
        Assert.Equal(7200, await Query(bot));
        Assert.Equal(0, await State(bot, s => s.Progress!.Currency.GetValueOrDefault(5)));
    }

    [ClientDataFact]
    public async Task PendingOldDayAwardSurvivesLogoutAndSettlesOnLoginExactlyOnce()
    {
        long role, old = 0;
        await using (var bot = await InWorld())
        {
            await Eligible(bot);
            role = (long)bot.Self!.Uuid;
            await OnOwnedLoop(bot, "AFK pending old-day fixture", s =>
            {
                long epoch = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(s.Daily!.DailyEpoch).LocalDateTime.AddDays(-1)).ToUnixTimeSeconds();
                old = epoch;
                s.Daily.DailyEpoch = epoch;
                s.WildAfk!.DailyEpoch = epoch;
                s.WildAfk.ConsumedTodaySeconds = 3600;
                s.WildAfk.Info.HighMultiplierTimeSeconds = 0;
                s.WildAfk.Info.IsHighMagnification = false;
                Service<RoleStore>().SaveDaily(role, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
                Service<RoleStore>().SaveWildAfk(role, JsonSerializer.SerializeToUtf8Bytes(s.WildAfk));
                FixtureSql.Execute(Service<AccountStore>(),
                    "CREATE TRIGGER fail_afk_award BEFORE UPDATE OF currency ON role BEGIN SELECT RAISE(ABORT, 'afk award fixture'); END");
                return Task.CompletedTask;
            });
            await bot.SendGameAsync(MsgId.Logout, new LogoutReq());
            await Next(bot, MsgId.Logout, LogoutResp.Parser);
            var saved = JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk(role)!)!;
            Assert.Equal(old, saved.DailyEpoch);
            Assert.Equal(3600, saved.ConsumedTodaySeconds);
            var dailySaved = JsonSerializer.Deserialize<DailyActivityService.DailyState>(Service<RoleStore>().LoadDaily(role)!)!;
            Assert.Equal(old, dailySaved.DailyEpoch);
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_afk_award");
        }
        await using (var again = await InWorld(create: false))
        {
            await Next(again, CharacterService.GetRewardNotify, GetRewardNotify.Parser,
                x => x.Rewards.Any(i => i.ItemEntry == 5 && i.Stack == 40));
            Assert.Equal(3600, await Query(again));
            Assert.Equal(0, await State(again, s => s.WildAfk!.ConsumedTodaySeconds));
            Assert.Equal(0, await State(again, s => s.DailyRows!.ActivityData[106].TaskCompleteTimes));
            Assert.Equal(0, await State(again, s => s.Progress!.Currency.GetValueOrDefault(5)));
            await again.SendGameAsync(MsgId.Logout, new LogoutReq());
            await Next(again, MsgId.Logout, LogoutResp.Parser);
        }
        await using var third = await InWorld(create: false);
        Assert.Equal(3600, await Query(third));
        Assert.Equal(0, await State(third, s => s.Progress!.Currency.GetValueOrDefault(5)));

    }

    [ClientDataFact]
    public async Task FailedDailyAwardRetriesWhileIdleAndBeforePeriodReset()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        await OnOwnedLoop(bot, "AFK failed award fixture", s =>
        {
            s.WildAfk!.ConsumedTodaySeconds = 3600;
            s.WildAfk.Info.HighMultiplierTimeSeconds = 0;
            s.WildAfk.Info.IsHighMagnification = false;
            FixtureSql.Execute(Service<AccountStore>(),
                "CREATE TRIGGER fail_afk_award BEFORE UPDATE OF currency ON role BEGIN SELECT RAISE(ABORT, 'afk award fixture'); END");
            return Task.CompletedTask;
        });
        Assert.Equal(0, await Query(bot));
        Assert.Equal(0, await State(bot, s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(500130)));
        await OnOwnedLoop(bot, "AFK failed award reset fixture", async s =>
        {
            long old = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(s.Daily!.DailyEpoch).LocalDateTime.AddDays(-1)).ToUnixTimeSeconds();
            s.Daily.DailyEpoch = old;
            s.WildAfk!.DailyEpoch = old;
            Service<RoleStore>().SaveDaily(s.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(s.Daily));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service<DailyActivityService>().ReconcileAsync(s));
            Assert.Equal(old, s.Daily.DailyEpoch); // reset is held, so the earned old-day award remains retryable
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_afk_award");
        });
        Assert.Equal(3600, await Query(bot)); // recovery settles once, then restores the new day
        await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser, x => x.Rewards.Any(i => i.ItemEntry == 5 && i.Stack == 40));
        Assert.Equal(0, await State(bot, s => s.DailyRows!.ActivityData[106].TaskCompleteTimes));
        Assert.Equal(0, await State(bot, s => s.WildAfk!.ConsumedTodaySeconds));
    }

    [ClientDataFact]
    public async Task SettingsCanClearFalseAndPowerSavingDoesNotPauseServerState()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        foreach (bool disabled in new[] { true, false })
        {
            // Lua encodes the changed scalar even when false. A generated proto3 object drops zero-valued fields.
            byte flag = disabled ? (byte)1 : (byte)0;
            await bot.SendGameBytesAsync(MsgId.SaveSetting, [0x0a, 1, 5, 0x12, 6, 0x2a, 4, 8, flag, 16, flag]);
            Assert.Equal(0, (await Next(bot, MsgId.SaveSetting, SaveSettingResp.Parser)).Result);
            var stored = DBSettingInfo.Parser.ParseFrom(Service<RoleStore>().LoadSettings((long)bot.Self!.Uuid)!);
            Assert.Equal(disabled, stored.WildAFKInfo.DisAbleConsumeAFKTime);
            Assert.Equal(disabled, stored.WildAFKInfo.AutoBack);
        }
        await bot.SendGameBytesAsync(MsgId.SaveSetting, [0x0a, 1, 5, 0x12, 4, 0x2a, 2, 16, 1]); // autoBack=true only
        await Next(bot, MsgId.SaveSetting, SaveSettingResp.Parser);
        await bot.SendGameBytesAsync(MsgId.SaveSetting, [0x0a, 1, 5, 0x12, 4, 0x2a, 2, 8, 0]); // disable=false only
        await Next(bot, MsgId.SaveSetting, SaveSettingResp.Parser);
        var merged = DBSettingInfo.Parser.ParseFrom(Service<RoleStore>().LoadSettings((long)bot.Self!.Uuid)!);
        Assert.True(merged.WildAFKInfo.AutoBack);
        Assert.False(merged.WildAFKInfo.DisAbleConsumeAFKTime);
        await Warp(bot, 10111);
        await bot.SendGameAsync(WildAfkService.Start, new StartHighMultiplierTimeReq());
        Assert.Equal(0, (await Next(bot, WildAfkService.Start, StartHighMultiplierTimeRsp.Parser)).Result);
        await bot.SendGameAsync(WildAfkService.SaveCharge, new WildAfkSaveChargeReq { IsSaveCharge = true });
        await Query(bot);
        Assert.True(await State(bot, s => s.WildAfk!.PowerSaving));
        Assert.True(await State(bot, s => s.WildAfk!.Info.IsHighMagnification));
    }

    [ClientDataFact]
    public async Task QueuedReconcileAfterLogoutDoesNotReadOrRestoreOldRoleState()
    {
        await using var bot = await InWorld();
        var session = Session(bot);
        var state = session.WildAfk!;
        var afk = Service<WildAfkService>();
        await state.Gate.WaitAsync();
        Task leaving, queued;
        try
        {
            leaving = OnLoop(() => afk.LeaveAsync(session));
            queued = OnLoop(() => afk.ReconcileAsync(session)); // waits behind LeaveAsync on the old role's gate
            await OnLoop(() => Task.CompletedTask);
        }
        finally { state.Gate.Release(); }
        await Task.WhenAll(leaving, queued);
        Assert.Null(session.WildAfk);
        Assert.False(session.AutoBattle);
        Assert.NotNull(Service<RoleStore>().LoadWildAfk(session.Role!.Id));
    }

    [ClientDataFact]
    public async Task KillWaitingOnOldAfkGateCannotRewardNextRole()
    {
        await using var bot = await InWorld();
        await Eligible(bot);
        var session = Session(bot);
        var oldRole = session.Role!;
        var oldProgress = session.Progress!;
        var oldAfk = session.WildAfk!;
        var afk = Service<WildAfkService>();
        var rewards = Service<RewardService>();
        var combat = Service<CombatService>();
        var originalEligibility = rewards.QualifiesForBonus!;
        var eligibilityReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rewardSettled = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailingCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rewardIndex = combat.BeforeKilled.FindIndex(h => h.Target is RewardService);
        Assert.True(rewardIndex >= 0);
        var originalRewardHandler = combat.BeforeKilled[rewardIndex];
        combat.BeforeKilled[rewardIndex] = async (owner, monster) =>
        {
            try { await originalRewardHandler(owner, monster); rewardSettled.TrySetResult(null); }
            catch (Exception error) { rewardSettled.TrySetResult(error); throw; }
        };
        Func<GameSession, MonsterUnit, Task> afterKill = (_, _) => { trailingCalled.TrySetResult(); return Task.CompletedTask; };
        rewards.QualifiesForBonus = (owner, monster) =>
        {
            var decision = originalEligibility(owner, monster); // queues behind the held old-state gate
            eligibilityReached.TrySetResult();
            return decision;
        };
        combat.Killed.Add(afterKill);

        var killCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enterOldGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldGateWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        afk.BeforeKillGateForTest = () => { killCaptured.TrySetResult(); return enterOldGate.Task; };
        afk.KillGateWaitingForTest = () => oldGateWaiting.TrySetResult();
        await oldAfk.Gate.WaitAsync();
        bool initialGateHeld = true, blockerAcquired = false, blockerReleased = false;
        try
        {
            var leaving = OnLoop(() => afk.LeaveAsync(session)); // first waiter; clears the old AFK state
            await OnLoop(() => Task.CompletedTask);
            long uid = 0;
            MonsterUnit target = null!;
            Vec3 position = null!;
            await OnLoop(() =>
            {
                var monster = Service<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>()
                    .First(m => !m.Dead && m.Template.NpcId == 5000001);
                monster.Hp = 1; monster.Moving = false; monster.NextRoamMs = long.MaxValue;
                uid = monster.Uid; position = monster.Pos;
                target = monster;
                return Task.CompletedTask;
            });
            await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = position, BStop = true, SpeedRate = 1 });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            // E04 waits for eligibility before admitting the durable kill: no lethal packet can precede this gate.
            await eligibilityReached.Task.WaitAsync(Wait);
            await killCaptured.Task.WaitAsync(Wait);
            string source = target.RewardSource!.Key;
            void AssertNoLethalPresentation()
            {
                Assert.DoesNotContain(bot.Inbox.Snapshot(), p => p.IsGame(CombatService.SkillHit)
                    && SkillHitInfo.Parser.ParseFrom(p.Body).Dsts.Any(d => d.DstID == uid && d.IsDead));
                Assert.DoesNotContain(bot.Inbox.Snapshot(), p => p.IsGame(CombatService.EntityBaseChange)
                    && EntityBase.Parser.ParseFrom(p.Body) is { State: EntityState.Dead } dead && dead.Uuid == (ulong)uid);
            }
            AssertNoLethalPresentation();
            Assert.False(await Service<CharacterService>().RewardSourceCommittedAsync(source));

            oldAfk.Gate.Release(); initialGateHeld = false;
            await leaving.WaitAsync(Wait);
            Assert.Null(session.WildAfk);
            // Scope admission may release/retry a semaphore on an owner conflict. Explicit phases
            // avoid relying on FIFO across that release while retaining the real stale-gate wait.
            await oldAfk.Gate.WaitAsync(); blockerAcquired = true;
            enterOldGate.TrySetResult();
            await oldGateWaiting.Task.WaitAsync(Wait);
            Assert.False(rewardSettled.Task.IsCompleted);
            await bot.SwitchRoleAsync();
            long bob = (await bot.CreateRoleAsync("Bob")).RoleBaseInfo.Id;
            await bot.EnterWorldAsync(bob);
            Assert.NotEqual(oldRole.Id, session.Role!.Id);
            Assert.NotSame(oldProgress, session.Progress);
            Assert.NotSame(oldAfk, session.WildAfk);
            long baseExp = session.Progress!.Exp, jobExp = session.Progress.JobExp;
            var currency = session.Progress.Currency.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToArray();
            var items = session.Items.Select(i => (i.Entry, i.Stack)).Order().ToArray();

            oldAfk.Gate.Release(); blockerReleased = true;
            // A detached recipient is rejected before admission, while the original negative role-switch oracle remains.
            Assert.IsType<OperationCanceledException>(await rewardSettled.Task.WaitAsync(Wait));
            // The child reward permit completes before the parent combat catch restores the staged HP.
            using (var restored = new CancellationTokenSource(Wait))
                while (!await Service<GameLoop>().InvokeAsync(() => Task.FromResult(target.Hp > 0)))
                    await Task.Delay(10, restored.Token);
            await OnLoop(() => Task.CompletedTask); // let CombatService finish the remaining kill-handler dispatch
            Assert.False(await Service<CharacterService>().RewardSourceCommittedAsync(source));
            Assert.False(target.Dead);
            Assert.False(target.DeathDurable);
            Assert.True(target.Hp > 0);
            AssertNoLethalPresentation();
            Assert.False(trailingCalled.Task.IsCompleted); // Bob must not receive old kill quest progress
            Assert.Equal(baseExp, session.Progress.Exp);
            Assert.Equal(jobExp, session.Progress.JobExp);
            Assert.Equal(currency, session.Progress.Currency.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToArray());
            Assert.Equal(items, session.Items.Select(i => (i.Entry, i.Stack)).Order().ToArray());
        }
        finally
        {
            afk.BeforeKillGateForTest = null;
            afk.KillGateWaitingForTest = null;
            enterOldGate.TrySetResult();
            if (initialGateHeld) oldAfk.Gate.Release();
            if (blockerAcquired && !blockerReleased) oldAfk.Gate.Release();
            rewards.QualifiesForBonus = originalEligibility;
            combat.BeforeKilled[rewardIndex] = originalRewardHandler;
            combat.Killed.Remove(afterKill);
        }
    }

    [ClientDataFact]
    public async Task KillGrantsQueuedOnStateLockAbortAfterWireRoleSwitch()
    {
        await using var bot = await InWorld();
        // EntitySelf precedes SceneLoaded's remaining handlers; drain StallsService's market lock first.
        await Query(bot);
        var session = Session(bot);
        var oldRole = session.Role!;
        var oldAfk = session.WildAfk!;
        var oldProgress = session.Progress!;
        var oldExp = oldProgress.Exp;
        var oldJobExp = oldProgress.JobExp;
        var oldCurrency = oldProgress.Currency.GetValueOrDefault(5);
        bool SameRole() => ReferenceEquals(session.Role, oldRole) && ReferenceEquals(session.WildAfk, oldAfk)
            && session.Progress != null;

        await session.StateLock.WaitAsync();
        bool lockHeld = true;
        Task? grant = null;
        Task<string>? exp = null;
        try
        {
            // Both methods pass their entry check, then queue behind StateLock before role ownership changes.
            grant = Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(session, [(5, 40)], SameRole));
            exp = Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().AddExpAsync(session, 100, 100, SameRole));
            await OnLoop(() => Task.CompletedTask);
            Assert.False(grant.IsCompleted);
            Assert.False(exp.IsCompleted);
            await bot.SwitchRoleAsync();
            Assert.Null(session.WildAfk);

            session.StateLock.Release(); lockHeld = false;
            await Task.WhenAll(grant, exp).WaitAsync(Wait);
            long bob = (await bot.CreateRoleAsync("Bob")).RoleBaseInfo.Id;
            await bot.EnterWorldAsync(bob);
            Assert.NotEqual(oldRole.Id, session.Role!.Id);
            Assert.Equal(oldExp, oldProgress.Exp);
            Assert.Equal(oldJobExp, oldProgress.JobExp);
            Assert.Equal(oldCurrency, oldProgress.Currency.GetValueOrDefault(5));
            Assert.Equal(0, session.Progress!.Exp);
            Assert.Equal(0, session.Progress.JobExp);
            Assert.Equal(0, session.Progress.Currency.GetValueOrDefault(5));
        }
        finally
        {
            if (lockHeld) session.StateLock.Release();
            if (grant != null) await grant.WaitAsync(Wait);
            if (exp != null) await exp.WaitAsync(Wait);
        }
    }


    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }
}
