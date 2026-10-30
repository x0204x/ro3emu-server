using Microsoft.Data.Sqlite;
using Phonest.Proto;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private const long LifecycleAirship = 1000501, LifecycleCamp = 10514, LifecycleCampPoint = 1040401;
    private const long LifecycleClearTask = 10103210, LifecycleBridgeTask = 10103220, LifecycleCampTask = 10104010;
    private const long LifecycleNextTask = 10104020, LifecycleOpening = 130161, LifecycleSuccess = 130162, LifecycleFailure = 130163;
    private const int LifecycleCaptain = 5300038, LifecycleCaptainTalk = 5300035, LifecycleCaptainOption = 5300039;

    // 前七步是受控 LAB 前置；本案從最後船長的正式對話選項起驗協定，不冒充整段實機操作。
    [ClientDataTheory]
    [InlineData(LifecycleSuccess, LifecycleFailure)]
    [InlineData(LifecycleFailure, LifecycleSuccess)]
    public async Task AirshipCaptainEndingAndCampJumpRespectTheirOwnGatesAndPublishTheNextQuest(long ending, long other)
    {
        await using var alice = await InWorld("Alice");
        await AirshipLifecycleAtCaptain(alice);
        await PlotEnd(alice, ending); // 船長選項尚未觸發：片尾不能越過這個前置。
        await AirshipLifecycleJumpRefused(alice, AirshipLifecycleJump());
        await AirshipLifecycleNotSettled(alice, outro: false);

        await AirshipLifecycleCaptainOption(alice);
        await PlotEnd(alice, LifecycleOpening);
        foreach (long id in new[] { ending, other })
            await alice.SendGameAsync(DungeonService.PlotSceneEvent, new PlotActingTriggerReq { PlotId = id });
        await AirshipLifecycleNotSettled(alice, outro: true);
        await AirshipLifecycleJumpRefused(alice, AirshipLifecycleJump());

        await PlotEnd(alice, ending);
        Assert.Equal(DungeonRes.Success, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await AirshipLifecyclePublishedTasks(alice);
        await AirshipLifecycleInvalidDeliveredJumps(alice);
        await AirshipLifecycleLand(alice);
        AssertAirshipTaskRecovered(alice, (long)alice.Self!.Uuid);
        await AirshipLifecycleLand(alice); // 重送已交付任務的141，不得再增加任務或獎勵來源。
        AssertAirshipTaskRecovered(alice, (long)alice.Self.Uuid);
        await AirshipLifecycleCampDialogue(alice);
        await PlotEnd(alice, other);
        await PlotEnd(alice, ending);
        await alice.HeartbeatAsync();
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.Result));
        Assert.Equal(1, AirshipQuestSourceCount((long)alice.Self!.Uuid, LifecycleClearTask));
        Assert.Equal(1, AirshipQuestSourceCount((long)alice.Self.Uuid, LifecycleBridgeTask));
        Assert.Equal(1, AirshipQuestSourceCount((long)alice.Self.Uuid, LifecycleCampTask));
    }

    // 這是模擬器既有 Phonest grace 內保留 session 的契約；原服是否同樣恢復影片仍是 UNKNOWN。
    [ClientDataTheory]
    [InlineData(LifecycleSuccess)]
    [InlineData(LifecycleFailure)]
    public async Task AirshipArmedEndingSurvivesSilentReconnectWithoutASecondAward(long ending)
    {
        await using var alice = await InWorld("Alice");
        Assert.True(await alice.CompleteGateAuthAsync());
        await AirshipLifecycleAtCaptain(alice);
        await AirshipLifecycleCaptainOption(alice);
        var session = Session(alice);
        var attempt = session.DungeonAttempt!;
        await alice.DropGameAsync(BotDrop.Graceful);
        var deadline = DateTime.UtcNow + Wait;
        while (!await Service<GameLoop>().InvokeAsync(() => Task.FromResult(session.TransportLost)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("airship reconnect grace was not entered");
            await Task.Delay(20);
        }
        var result = await alice.ReconnectGameAsync();
        Assert.Equal(ConnectServerResp.Types.RespType.Reconnected, result.Result);
        Assert.Same(session, Session(alice));
        Assert.Same(attempt, Session(alice).DungeonAttempt);
        await AirshipLifecycleNotSettled(alice, outro: true);
        await PlotEnd(alice, ending);
        await Next(alice, DungeonService.Result, DungeonResNotify.Parser, r => r.Res == DungeonRes.Success);
        await AirshipLifecyclePublishedTasks(alice);
        await AirshipLifecycleLand(alice);
        await PlotEnd(alice, LifecycleSuccess);
        await PlotEnd(alice, LifecycleFailure);
        await alice.HeartbeatAsync();
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.Result));
        AssertAirshipTaskRecovered(alice, (long)alice.Self!.Uuid);
    }

    [ClientDataFact]
    public async Task AirshipQuestAwardFaultRelogPublishesItsEarnedJumpAndCampTasksOnlyOnce()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            await AirshipLifecycleAtCaptain(alice);
            await AirshipLifecycleCaptainOption(alice);
            role = (long)alice.Self!.Uuid;
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accounts = Service<AccountStore>();
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/cd13e/quest-award") return Task.CompletedTask;
                attempted.TrySetResult();
                throw new IOException("airship lifecycle quest award fault");
            };
            try
            {
                await PlotEnd(alice, LifecycleFailure);
                await attempted.Task.WaitAsync(Wait);
                Assert.False(TaskService.IsDelivered(Session(alice), LifecycleClearTask));
                Assert.Equal(0, AirshipQuestSourceCount(role, LifecycleClearTask));
                await AirshipLifecycleJumpRefused(alice, AirshipLifecycleJump());
            }
            finally { accounts.BeforeOwnedCommitForTest = null; }
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        using (var ownPool = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}"))
            SqliteConnection.ClearPool(ownPool);
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        AssertAirshipTaskRecovered(recovered, role);
        await AirshipLifecyclePublishedTasks(recovered);
        await AirshipLifecycleLand(recovered);
        await PlotEnd(recovered, LifecycleFailure);
        await PlotEnd(recovered, LifecycleSuccess);
        await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(recovered)));
        await recovered.HeartbeatAsync();
        AssertAirshipTaskRecovered(recovered, role);
        Assert.Equal(LifecycleCamp, Session(recovered).SceneEntry);
    }

    private async Task AirshipLifecycleAtCaptain(BotClient bot)
    {
        await Gm(bot, "!lvl 99");
        await Gm(bot, $"!quest accept {LifecycleClearTask}");
        Assert.Equal(0, await Enter(bot, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(bot, LifecycleAirship);
        var waves = Session(bot).DungeonAttempt!.Plan.Waves;
        Assert.Equal(8, waves.Count);
        for (int index = 0; index < waves.Count - 1; index++)
        {
            var wave = waves[index];
            if (wave.Step is { } step)
            {
                await Stage(bot, index + 1, step.Quest);
                await ActorsIn(bot, step.Actors.Count);
            }
            if (wave.Spawns.Count > 0)
                foreach (var monster in await WaveIn(instance, wave.Spawns.Count)) await Kill(bot, monster);
            if (wave.Step?.Complete.Count > 0) await Gm(bot, "!actors use");
        }
        await Stage(bot, 8, quest: 6);
        var actor = Assert.Single(await ActorsIn(bot, 1));
        Assert.Equal(LifecycleCaptain, actor.NpcId);
        var visible = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == LifecycleCaptain));
        Assert.Equal((ulong)actor.Uid, Assert.Single(visible.Bases, b => b.EntityTemplateID == LifecycleCaptain).Uuid);
        await Beside(bot, actor.X, actor.Y, actor.Z);
    }

    private async Task AirshipLifecycleCaptainOption(BotClient bot)
    {
        // G：5300035開530003802，下一對話530003803的211依NPC設定ID找UUID，再送32700/5300039。
        var data = Service<GameData>();
        Assert.Equal("530003802", data.Row("NPCInteractConfig", LifecycleCaptainTalk)!.Value.GetProperty("_iEXECAction")[0][1].GetString());
        var options = data.Row("DialogueConfig", 530003803)!.Value.EnumerateArray()
            .Where(row => row.TryGetProperty("_iEXECAction", out _)).ToArray();
        Assert.Contains(options, row => row.GetProperty("_iEXECAction").EnumerateArray().Any(a =>
            a.GetArrayLength() == 3 && a[0].GetString() == "211" && a[1].GetString() == "5300038" && a[2].GetString() == "5300039"));
        var captain = Assert.Single(await ActorsIn(bot, 1));
        Assert.Equal(0, await Interact(bot, captain, LifecycleCaptainTalk));
        Assert.Equal(0, await Interact(bot, captain, LifecycleCaptainOption));
        await Story(bot, LifecycleOpening);
    }

    private async Task AirshipLifecycleNotSettled(BotClient bot, bool outro)
    {
        await bot.HeartbeatAsync();
        await OnLoop(() =>
        {
            var session = Session(bot);
            Assert.Equal(LifecycleAirship, session.SceneEntry);
            Assert.False(session.DungeonAttempt!.Settled);
            Assert.Equal(outro, session.DungeonAttempt.StepOutroPending);
            Assert.Equal(0, session.Tasks!.CompleteTaskNum.GetValueOrDefault(LifecycleClearTask));
            return Task.CompletedTask;
        });
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(DungeonService.Result));
        Assert.Equal(0, AirshipQuestSourceCount((long)bot.Self!.Uuid, LifecycleClearTask));
    }

    private static ClientCustomJumpMapReq AirshipLifecycleJump() => new()
    {
        SceneId = LifecycleCamp, PointId = LifecycleCampPoint, CustomType = EClientCustomJumpMapType.TaskDelivery,
        TypeParam1 = LifecycleClearTask, TypeParam2 = 0,
    };

    private async Task AirshipLifecycleJumpRefused(BotClient bot, ClientCustomJumpMapReq request)
    {
        long before = Session(bot).SceneEntry;
        await bot.SendGameAsync(SceneService.CustomJumpMap, request);
        Assert.NotEqual(0, (await Next(bot, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        await bot.HeartbeatAsync();
        Assert.Equal(before, Session(bot).SceneEntry);
    }

    private async Task AirshipLifecycleInvalidDeliveredJumps(BotClient bot)
    {
        var wrongTask = AirshipLifecycleJump(); wrongTask.TypeParam1 = LifecycleBridgeTask;
        var wrongPoint = AirshipLifecycleJump();
        wrongPoint.PointId = Service<WorldData>().Map(LifecycleCamp)!.Points.Keys.First(id => id != LifecycleCampPoint);
        var wrongParameter = AirshipLifecycleJump(); wrongParameter.TypeParam2 = 1;
        foreach (var request in new[] { wrongTask, wrongPoint, wrongParameter })
            await AirshipLifecycleJumpRefused(bot, request);
    }

    private async Task AirshipLifecycleLand(BotClient bot)
    {
        await bot.SendGameAsync(SceneService.CustomJumpMap, AirshipLifecycleJump());
        Assert.Equal(0, (await Next(bot, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == LifecycleCamp);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = LifecycleCamp });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == LifecycleCamp)).Result);
        var self = (await Next(bot, MsgId.EntitySelf, EntitySelf.Parser)).Base;
        var map = Service<WorldData>().Map(LifecycleCamp)!;
        var expected = map.Grounded(map.Points[LifecycleCampPoint]);
        Assert.Equal(expected.X, self.MovementInfo.Pos.X, 2);
        Assert.Equal(expected.Z, self.MovementInfo.Pos.Z, 2);
        await bot.HeartbeatAsync();
        Assert.Equal(0, Session(bot).InstanceId);
        Assert.False(Session(bot).InCutScene);
        Assert.False(Session(bot).SceneLoading);
        Assert.Equal(TaskState.Running, Session(bot).Tasks!.Infos[LifecycleCampTask].Status);
        // 這只驗證控制已可用與任務資料到達；真正HUD、鏡頭、影片結束畫面須另做玩家端驗收。
        await bot.WalkAsync(expected.X + 0.1f, expected.Y, expected.Z, stop: true);
        await bot.HeartbeatAsync();
        Assert.Equal(expected.X + 0.1f, Session(bot).Position.X, 2);
    }

    private static async Task AirshipLifecyclePublishedTasks(BotClient bot)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var frames = bot.Inbox.Snapshot().Where(e => e.IsGame(TaskService.TaskInfoSync))
                .Select(e => DBTaskInfoIncrement.Parser.ParseFrom(e.Body)).ToArray();
            bool Count(long id) => frames.Any(f => f.CompleteTaskNum.TryGetValue(id, out var value) && value.All == 1);
            bool camp = frames.Any(f => f.Infos.TryGetValue(LifecycleCampTask, out var value) && value.All?.Status == TaskState.Running);
            if (Count(LifecycleClearTask) && Count(LifecycleBridgeTask) && camp) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("client did not receive airship award and camp quest projection on 5027");
            await Task.Delay(20);
        }
    }

    private async Task AirshipLifecycleCampDialogue(BotClient bot)
    {
        var target = Service<GameData>().Row("TaskConfig", LifecycleCampTask)!.Value.GetProperty("_kProgressTarget")[0];
        long point = target[1].GetInt64();
        var position = Service<WorldData>().Map(LifecycleCamp)!.Points[point];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), position));
        var finished = new ClientNpcInteractReq { DialogID = target[3].GetInt32(), Areapointid = point };
        await bot.SendGameAsync(SceneService.ClientNpcInteract, finished);
        await StoryMovesOn(bot, LifecycleCampTask, LifecycleNextTask);
        await bot.SendGameAsync(SceneService.ClientNpcInteract, finished);
        await bot.HeartbeatAsync();
        Assert.Equal(1, AirshipQuestSourceCount((long)bot.Self!.Uuid, LifecycleCampTask));
        Assert.Equal(TaskState.Running, Session(bot).Tasks!.Infos[LifecycleNextTask].Status);
    }
}
