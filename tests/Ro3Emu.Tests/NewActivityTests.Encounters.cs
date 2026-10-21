using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    private sealed class FestivalErrors : ILoggerProvider, ILogger
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string _) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> format)
        {
            if (level >= LogLevel.Error) Messages.Enqueue(format(state, error) + " " + error);
        }
        public void Dispose() { }
    }

    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    [ClientDataFact]
    public async Task SharedEventRejoinKeepsOneParticipantAndCurrentReturnPoint()
    {
        await using var alice = await InWorld("RoomAlice");
        await using var bob = await InWorld("RoomBob");
        await OnLoop(async () =>
        {
            var instances = Service<InstanceService>();
            var a = Session(alice); var b = Session(bob);
            var room = instances.Create(GuildCampfireService.Dungeon, [a, b]);
            await instances.AbandonAsync(room, a);
            Assert.False(room.Closed);
            Assert.Equal(b, Assert.Single(room.ActiveMembers).Session);
            a.Position = new Vec3 { X = a.Position.X + 2, Y = a.Position.Y, Z = a.Position.Z + 3 };
            Assert.True(instances.AddParticipant(room, a));
            Assert.Equal(2, room.ActiveMembers.Count);
            var joined = Assert.Single(room.ActiveMembers, member => member.Session == a);
            Assert.Equal(a.Position.X, joined.ReturnPoint.X);
            Assert.Equal(a.Position.Z, joined.ReturnPoint.Z);
            Assert.True(instances.AddParticipant(room, a));
            Assert.Equal(2, room.ActiveMembers.Count);
            await instances.AbandonAsync(room, a);
            await instances.AbandonAsync(room, b);
            Assert.True(room.Closed);
            Assert.Null(instances.Find(room.Id));
        });
    }

    private async Task<long> LoadPrivate(BotClient bot, long map)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == map);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = map });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == map)).Result);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, r => r.DungeonId == sw.SceneID);
        return sw.SceneID;
    }

    [ClientDataFact]
    public async Task TrainingMeasuresRealHitsAndRewardClaimsAreAtomicAndDurable()
    {
        var bot = await InWorld("Training");
        await Next(bot, TrainingService.Sync, DBTrainingGroundInfoIncrement.Parser);
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 2100009 });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await Gm(bot, "!lvl 99"); await Gm(bot, "!job 1100");
        var claim = new TrainingAimAwardReq { AimAwardIds = { 1 } };
        await bot.SendGameAsync(84062, claim);
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, 84062, TrainingAimAwardResp.Parser)).Result);
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 2100009 });
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        long practice = await LoadPrivate(bot, 2100009);
        await bot.SendGameAsync(84064, new TrainingUpdateWoodNpcAttrReq { Race = 3, Element = 4, BodyType = 1 });
        Assert.Equal(0, (await Next(bot, 84064, TrainingUpdateWoodNpcAttrResp.Parser)).Result);
        await OnLoop(() =>
        {
            var dummy = Service<World>().AllUnits().OfType<MonsterUnit>().Single(m => m.InstanceId == practice);
            Assert.Equal(3, dummy.Template.Race); Assert.Equal(4, dummy.Template.Element); Assert.Equal(1, dummy.Template.Size);
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(84064, new TrainingUpdateWoodNpcAttrReq { Race = -1, Element = 4, BodyType = 1 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, 84064, TrainingUpdateWoodNpcAttrResp.Parser)).Result);
        await bot.SendGameAsync(84061, new NullArg());
        Assert.Equal(0, (await Next(bot, 84061, TrainingChallengeModeResp.Parser)).Result);
        long challenge = await LoadPrivate(bot, 2100011);
        await bot.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "OnPileChallengeStart" });
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            r => r.DungeonId == challenge && r.Vars.Any(v => v.VarId == 3500000 && v.Vi64.Any(t => t > 0)));
        await bot.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "OnPileChallengeEnterOrExit" });
        var actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
            .OfType<MonsterUnit>().Single(m => m.InstanceId == challenge)));
        Assert.Equal(2, actor.Template.Race); Assert.Equal(1, actor.Template.Element);
        await bot.SendGameAsync(84064, new TrainingUpdateWoodNpcAttrReq { Race = 3, Element = 4, BodyType = 1 });
        Assert.NotEqual(0, (await Next(bot, 84064, TrainingUpdateWoodNpcAttrResp.Parser)).Result);
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = actor.Pos, BStop = true, SpeedRate = 1 });
        int normal = await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Service<SkillService>().NormalAttack(Session(bot))));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == actor.Uid && d.Damage > 0));
        await bot.SendGameAsync(CombatService.SkillInterrupt, new NullArg());
        await bot.SendGameAsync(84066, new NullArg());
        var measured = await Next(bot, 84066, TrainingChallengeReconnectRes.Parser);
        Assert.True(measured.TotalDamage > 0);
        _clock.AdvanceSeconds(61);
        var result = await Next(bot, DungeonService.Result, DungeonResNotify.Parser, r => r.Data?.TrainingGroundData != null);
        Assert.True(result.Data.TrainingGroundData.SumDamage > 0);
        Assert.Equal(result.Data.TrainingGroundData.SumDamage / 60, result.Data.TrainingGroundData.MaxDps);
        Assert.False(actor.Dead);
        // Seed the qualification threshold independently of hit strength; the RPC still validates and settles the claim.
        await OnLoop(() => { Session(bot).Training!.MaxDps = Math.Max(100, Session(bot).Training!.MaxDps); return Task.CompletedTask; });
        long moneyBefore = Session(bot).Progress!.Currency.GetValueOrDefault(1);
        long Receipts()
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/training-claim'";
            return (long)command.ExecuteScalar()!;
        }
        long receiptsBefore = Receipts();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/event/training-claim") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            await bot.SendGameAsync(84062, claim);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(1L, Session(bot).Training!.ClaimedAwardIds);
            Assert.Equal(moneyBefore, Session(bot).Progress!.Currency.GetValueOrDefault(1));
            Assert.Equal(receiptsBefore, Receipts());
        }
        finally { release.TrySetResult(); Service<AccountStore>().BeforeOwnedCommitForTest = null; }
        Assert.Equal(0, (await Next(bot, 84062, TrainingAimAwardResp.Parser)).Result);
        Assert.Equal(moneyBefore + 1000, Session(bot).Progress!.Currency.GetValueOrDefault(1));
        Assert.Equal(receiptsBefore + 1, Receipts());
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}"))
        {
            db.Open();
            using var source = db.CreateCommand();
            source.CommandText = "SELECT source_key,leg_count FROM operation_receipt WHERE command_kind='event/training-claim' ORDER BY commit_seq DESC LIMIT 1";
            using var row = source.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal($"training/claim/{Session(bot).Role!.Id}/1", row.GetString(0));
            Assert.True(row.GetInt64(1) > 0);
        }
        await bot.SendGameAsync(84062, claim);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(bot, 84062, TrainingAimAwardResp.Parser)).Result);
        Assert.Equal(receiptsBefore + 1, Receipts());
        await bot.DisposeAsync();
        await WaitOffline("training");
        await using var again = await InWorld("Training", false);
        Assert.Contains(1L, Session(again).Training!.ClaimedAwardIds);
        Assert.True(Session(again).Training!.MaxDps >= 100);
        Assert.Equal(0, Session(again).InstanceId);
    }

    /// <summary>84067 restarts a running challenge and 84063 returns to practice; both refuse outside their scene.</summary>
    [ClientDataFact]
    public async Task TrainingChallengeResetAndExitAnswerTheirReplies()
    {
        await using var bot = await InWorld("TrainingExit");
        await Next(bot, TrainingService.Sync, DBTrainingGroundInfoIncrement.Parser);
        await bot.SendGameAsync(TrainingService.ExitChallengeMode, new NullArg());
        Assert.NotEqual(0, (await Next(bot, TrainingService.ExitChallengeMode, TrainingExitChallengeModeResp.Parser)).Result);
        await Gm(bot, "!lvl 99"); await Gm(bot, "!job 1100");
        await bot.SendGameAsync(TrainingService.ExitChallengeMode, new NullArg());
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(bot, TrainingService.ExitChallengeMode, TrainingExitChallengeModeResp.Parser)).Result);
        await bot.SendGameAsync(TrainingService.ChallengeReset, new NullArg());
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(bot, TrainingService.ChallengeReset, TrainingChallengeResetRes.Parser)).Result);

        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 2100009 });
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await LoadPrivate(bot, 2100009);
        // Practice has no challenge to reset.
        await bot.SendGameAsync(TrainingService.ChallengeReset, new NullArg());
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(bot, TrainingService.ChallengeReset, TrainingChallengeResetRes.Parser)).Result);
        await bot.SendGameAsync(TrainingService.ChallengeMode, new NullArg());
        Assert.Equal(0, (await Next(bot, TrainingService.ChallengeMode, TrainingChallengeModeResp.Parser)).Result);
        long challenge = await LoadPrivate(bot, 2100011);
        await bot.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "OnPileChallengeStart" });
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            r => r.DungeonId == challenge && r.Vars.Any(v => v.VarId == 3500000 && v.Vi64.Any(t => t > 0)));
        await bot.SendGameAsync(TrainingService.ChallengeReconnect, new NullArg());
        Assert.True((await Next(bot, TrainingService.ChallengeReconnect, TrainingChallengeReconnectRes.Parser)).EndTime > 0);

        await bot.SendGameAsync(TrainingService.ChallengeReset, new NullArg());
        Assert.Equal(0, (await Next(bot, TrainingService.ChallengeReset, TrainingChallengeResetRes.Parser)).Result);
        await bot.SendGameAsync(TrainingService.ChallengeReconnect, new NullArg());
        var stopped = await Next(bot, TrainingService.ChallengeReconnect, TrainingChallengeReconnectRes.Parser);
        Assert.Equal(0, stopped.EndTime);
        Assert.Equal(0, stopped.TotalDamage);

        await bot.SendGameAsync(TrainingService.ExitChallengeMode, new NullArg());
        Assert.Equal(0, (await Next(bot, TrainingService.ExitChallengeMode, TrainingExitChallengeModeResp.Parser)).Result);
        long practice = await LoadPrivate(bot, 2100009);
        Assert.Equal(practice, Session(bot).InstanceId);
    }

    [ClientDataFact]
    public async Task FestivalUsesAuthoredAnswerZonesAndPaysEachEntitlementOnce()
    {
        var bot = await InWorld("Festival");
        var failures = new FestivalErrors();
        Service<ILoggerFactory>().AddProvider(failures);
        Task ReconcileFestival() => OnLoop(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>()
            .RunRuntimeAsync("test/festival reconcile", () => Service<FestivalService>().ReconcileAsync()));
        try
        {
            await Gm(bot, "!lvl 99");
            await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = FestivalService.Dungeon });
            Assert.Equal(ErrorCodes.ActivityPeriod, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
            _clock.SetLocal(2026, 10, 9, 18, 30, 10);
            // The jump crosses the daily boundary: a heartbeat's daily reset clears DailyReady while it commits, and the
            // one candidate pass below would skip the role (a live server re-selects on the next second's tick). Settle
            // the reset first; it serialises with any heartbeat reset already running.
            await OnLoop(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>()
                .RunRuntimeAsync("test/daily reconcile", () => Service<DailyActivityService>().ReconcileAsync(Session(bot))));
            await ReconcileFestival();
            _clock.SetLocal(2026, 10, 9, 18, 32);
            var bet = new CommonDailyActivityReq
            {
                ActivityId = FestivalService.ActivityId, RequestId = 701,
                RainbowKoiBet = new RainbowKoiBetReq { TargetPlayerId = (long)bot.Self!.Uuid + 900 }
            };
            await bot.SendGameAsync(DailyActivityService.RingAccept, bet);
            Assert.Equal(ErrorCodes.Parameter, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser, r => r.RequestId == 701)).Result);
            bet.RequestId++; bet.RainbowKoiBet.TargetPlayerId = (long)bot.Self.Uuid;
            await bot.SendGameAsync(DailyActivityService.RingAccept, bet);
            Assert.Equal(0, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser, r => r.RequestId == 702)).Result);
            bet.RequestId++;
            await bot.SendGameAsync(DailyActivityService.RingAccept, bet);
            Assert.Equal(ErrorCodes.RewardClaimed, (await Next(bot, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser, r => r.RequestId == 703)).Result);
            await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = FestivalService.Dungeon });
            Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
            await LoadPrivate(bot, FestivalService.Dungeon);
            int answer = (int)GameData.Int(Service<GameData>().Table("RainbowKoiQuestionConfig").Values
                .OrderBy(r => GameData.Int(r, "_iID")).First(), "_iQuestionAnswer");
            using var editor = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "data", "sceneeditor", "sc_prontera_001_dg01_sceneeditor.json")));
            var center = editor.RootElement.GetProperty("m_kRegins").GetProperty(answer == 1 ? "27" : "28").GetProperty("center");
            var answerPosition = new Vec3
            { X = center.GetProperty("x").GetSingle(), Y = center.GetProperty("y").GetSingle(), Z = center.GetProperty("z").GetSingle() };
            await bot.SendGameAsync(MsgId.Walk, new WalkReq { BStop = true, SpeedRate = 1, Pos = answerPosition });
            // Sending a frame only queues it. The direct ReconcileAsync call below can otherwise
            // overtake WalkAsync on the game loop and judge the old position at the answer deadline.
            bool arrived = false;
            for (int i = 0; i < 400 && !arrived; i++)
            {
                await OnLoop(() =>
                {
                    var position = Session(bot).Position;
                    arrived = Math.Abs(position.X - answerPosition.X) < 0.01f
                        && Math.Abs(position.Z - answerPosition.Z) < 0.01f;
                    return Task.CompletedTask;
                });
                if (!arrived) await Task.Delay(20);
            }
            Assert.True(arrived, "The authoritative player position did not reach the authored answer zone.");
            var plan = Service<AwardService>().Resolve(Session(bot), 85001, 101);
            var item = plan.Grants.First();
            long before = Service<CharacterService>().CountOf(Session(bot), item.Entry);
            _clock.SetLocal(2026, 10, 9, 18, 35, 1);
            await ReconcileFestival();
            var store = Service<AccountStore>();
            long roleId = (long)bot.Self.Uuid;
            long CountRows(string sql, string? source = null)
            {
                using var db = store.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                if (sql.Contains("$role", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$role", roleId);
                if (sql.Contains("$source", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$source", source ?? "");
                return (long)cmd.ExecuteScalar()!;
            }
            long cycle;
            using (var db = store.Open())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT cycle FROM festival_round ORDER BY cycle DESC LIMIT 1";
                cycle = (long)cmd.ExecuteScalar()!;
            }
            string completionSource = $"festival/{cycle}/complete";
            const string rewardCount = "SELECT count(*) FROM festival_reward WHERE role_id=$role";
            const string completionCount = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/festival-complete' AND source_key=$source";
            Assert.Equal(0, CountRows(rewardCount));
            Assert.Equal(0, CountRows(completionCount, completionSource));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/event/festival-complete") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            };
            Task completion;
            Task retry;
            try
            {
                _clock.AdvanceSeconds(30);
                completion = ReconcileFestival();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(before, Service<CharacterService>().CountOf(Session(bot), item.Entry));
                Assert.Equal(0, CountRows(rewardCount));
                Assert.Equal(0, CountRows(completionCount, completionSource));
                retry = ReconcileFestival();
                await Task.Delay(30);
                Assert.False(retry.IsCompleted);
                Assert.Equal(0, CountRows(completionCount, completionSource));
            }
            finally { release.TrySetResult(); store.BeforeOwnedCommitForTest = null; }
            await Task.WhenAll(completion, retry).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(before + item.Count * 2, Service<CharacterService>().CountOf(Session(bot), item.Entry));
            Assert.Equal(2, CountRows(rewardCount));
            Assert.Equal(1, CountRows(completionCount, completionSource));
            await ReconcileFestival();
            Assert.Equal(before + item.Count * 2, Service<CharacterService>().CountOf(Session(bot), item.Entry));
            Assert.Equal(1, CountRows(completionCount, completionSource));
            using (var db = Service<AccountStore>().Open())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT grants FROM festival_reward WHERE role_id=$r AND claimed=1 ORDER BY source";
                cmd.Parameters.AddWithValue("$r", (long)bot.Self.Uuid);
                using var frozen = cmd.ExecuteReader();
                int rows = 0;
                while (frozen.Read())
                {
                    var grants = System.Text.Json.JsonSerializer.Deserialize<AwardGrant[]>((byte[])frozen.GetValue(0));
                    Assert.NotNull(grants);
                    Assert.Contains(grants, grant => grant.Entry == item.Entry && grant.Count == item.Count);
                    rows++;
                }
                Assert.Equal(2, rows);
            }
            await bot.DisposeAsync(); await WaitOffline("festival");
            await using var again = await InWorld("Festival", false);
            Assert.Equal(before + item.Count * 2, Service<CharacterService>().CountOf(Session(again), item.Entry));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Festival error logs: " + string.Join(" | ", failures.Messages)
                + "; recent bot messages: " + string.Join(",", bot.Inbox.Snapshot().TakeLast(12)
                    .Select(message => $"{message.Kind}:{message.MsgId}:{message.Name}")), error);
        }
    }

    [Fact]
    public async Task CashOrdersAndClientPaymentReportsAreRefused()
    {
        await using var bot = await InWorld("CashRefused");
        await bot.SendGameAsync(2001, new CreateOrderReq { ChargeType = 2, ChargeId = 199, Amount = 1 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, 2001, CreateOrderResp.Parser)).Result);
        await bot.SendGameAsync(2002, new PayResultReq { OrderId = "client-forged" });
        var reply = await Next(bot, 2002, PayResultResp.Parser);
        Assert.Equal(ErrorCodes.FeatureUnavailable, reply.Result); Assert.Equal("client-forged", reply.OrderId);
    }

    private async Task WaitOffline(string account)
    {
        for (int i = 0; i < 100 && Service<SessionRegistry>().All().Any(s => s.Account == account); i++) await Task.Delay(20);
    }
}
