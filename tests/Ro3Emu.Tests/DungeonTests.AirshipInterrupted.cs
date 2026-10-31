using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // 原服重啟政策 UNKNOWN；此案驗模擬器的 runtime 房間不持久化，未完成QTE不會變成已得通關。
    [ClientDataFact]
    public async Task AirshipArmedWithoutEndingRestartCannotReplayItsOldRoomIntoAClear()
    {
        long role, previousInstance, outside;
        var alice = await InWorld("Alice");
        try
        {
            outside = Session(alice).SceneEntry;
            await AirshipLifecycleAtCaptain(alice);
            await AirshipLifecycleCaptainOption(alice);
            role = (long)alice.Self!.Uuid;
            previousInstance = Session(alice).InstanceId;
            await AirshipLifecycleNotSettled(alice, outro: true);
            await AirshipInterruptedNoClear(alice, role);
            // 角色仍在線、QTE仍armed時停止host；不是先完成登出再重開的替代情境。
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        finally { await alice.DisposeAsync(); }

        using (var ownPool = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}"))
            SqliteConnection.ClearPool(ownPool);
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Equal(role, (long)recovered.Self!.Uuid);
        Assert.Equal(outside, Session(recovered).SceneEntry);
        Assert.Equal(0, Session(recovered).InstanceId);
        Assert.Null(Session(recovered).DungeonAttempt);
        Assert.Null(Service<InstanceService>().Find(previousInstance));
        await AirshipInterruptedLateEndings(recovered, role);

        Assert.Equal(0, await Enter(recovered, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long fresh = await Load(recovered, LifecycleAirship);
        Assert.NotEqual(previousInstance, fresh);
        await Stage(recovered, 1, quest: 1);
        await AirshipInterruptedLateEndings(recovered, role);
        var attempt = Session(recovered).DungeonAttempt!;
        Assert.Equal(fresh, attempt.Room.Id);
        Assert.Equal(0, attempt.Wave);
        Assert.False(attempt.StepOutroPending);
        Assert.False(attempt.Settled);
    }

    [ClientDataTheory]
    [InlineData("giveup")]
    [InlineData("leave30402")]
    [InlineData("deadline-ending-race")]
    public async Task AirshipAbandonOrDeadlineEndingRaceCannotBeTurnedIntoSuccess(string interruption)
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        long outside = Session(alice).SceneEntry;
        await AirshipLifecycleAtCaptain(alice);
        await AirshipLifecycleCaptainOption(alice);
        long role = (long)alice.Self!.Uuid;
        await AirshipLifecycleNotSettled(alice, outro: true);
        switch (interruption)
        {
            case "giveup":
                await alice.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "DungeonGiveUp" });
                Assert.Equal(DungeonRes.Fail, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
                break;
            case "leave30402":
                await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
                Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
                await AirshipInterruptedLoadOutside(alice, outside);
                Assert.Equal(0, Session(alice).InstanceId);
                Assert.Null(Session(alice).DungeonAttempt);
                break;
            case "deadline-ending-race":
                // GameLoop.Now是Stopwatch，ManualClock只改UTC。對齊此場次的elapsed deadline，再讓正式84011
                // 進Advance的逾時優先分支；不宣稱測過無輸入的真實長時間timer自動逾時。
                await OnLoop(() =>
                {
                    var attempt = Session(alice).DungeonAttempt!;
                    attempt.StartedAt = Service<GameLoop>().Now - TimeSpan.FromMilliseconds(attempt.Plan.DurationMs + 1L);
                    return Task.CompletedTask;
                });
                await PlotEnd(alice, LifecycleSuccess);
                Assert.Equal(DungeonRes.Fail, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(interruption));
        }
        await AirshipInterruptedLateEndings(alice, role);
        var saved = DBTaskInfo.Parser.ParseFrom(Service<RoleStore>().LoadTasks(role)!);
        Assert.Equal(TaskState.Running, saved.Infos[LifecycleClearTask].Status);
        Assert.Equal(0, saved.Infos[LifecycleClearTask].CompleteNum[0]);
        Assert.Equal(0, saved.CompleteTaskNum.GetValueOrDefault(LifecycleClearTask));
    }

    private async Task AirshipInterruptedLateEndings(BotClient bot, long role)
    {
        await PlotEnd(bot, LifecycleSuccess);
        await PlotEnd(bot, LifecycleFailure);
        await AirshipLifecycleJumpRefused(bot, AirshipLifecycleJump());
        await AirshipInterruptedNoClear(bot, role);
    }

    private async Task AirshipInterruptedNoClear(BotClient bot, long role)
    {
        await bot.HeartbeatAsync();
        await OnLoop(() =>
        {
            var tasks = Session(bot).Tasks!;
            Assert.Equal(TaskState.Running, tasks.Infos[LifecycleClearTask].Status);
            Assert.Equal(0, tasks.Infos[LifecycleClearTask].CompleteNum[0]);
            Assert.Equal(0, tasks.CompleteTaskNum.GetValueOrDefault(LifecycleClearTask));
            Assert.False(tasks.Infos.ContainsKey(LifecycleBridgeTask));
            return Task.CompletedTask;
        });
        Assert.Equal(0, AirshipQuestSourceCount(role, LifecycleClearTask));
        Assert.DoesNotContain(Service<DungeonObligationStore>().Pending(role), obligation => obligation.Success);
        // 失敗可能有自己配置的結果／獎勵；只禁止被翻成Success，不猜所有失敗獎勵皆為零。
        Assert.DoesNotContain(bot.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.ResultNotify)
            && DungeonResultNotify.Parser.ParseFrom(packet.Body).ResultInfo?.WinCamp == 1);
    }

    private static async Task AirshipInterruptedLoadOutside(BotClient bot, long outside)
    {
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, notice => notice.SceneEntry == outside);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = outside });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, response => response.SceneId == outside)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await bot.HeartbeatAsync();
    }
}
