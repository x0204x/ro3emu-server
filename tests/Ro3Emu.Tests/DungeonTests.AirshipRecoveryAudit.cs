using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataTheory]
    [InlineData("economy/dungeon-reward", false)]
    [InlineData("economy/dungeon-reward", true)]
    [InlineData("task/dungeon-clear", false)]
    [InlineData("task/dungeon-clear", true)]
    [InlineData("economy/cd13e/quest-award", false)]
    [InlineData("economy/cd13e/quest-award", true)]
    public async Task AirshipClearTaskSurvivesDungeonDeliveryFaultAndRestart(string boundary, bool afterCommit)
    {
        const long scene = 1000501, task = 10103210;
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm(alice, "!lvl 99");
            await Gm(alice, $"!quest accept {task}");
            role = (long)alice.Self!.Uuid;
            var session = Session(alice);
            Assert.Equal(TaskState.Running, session.Tasks!.Infos[task].Status);
            Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
            long instance = await Load(alice, scene);
            await ClearReconstructedStoryRun(alice, instance);
            await Story(alice, 130161);

            // 鎖定真正進入交付交易的時點，不能在凍結義務剛出現時就撤掉故障。
            var accounts = Service<AccountStore>();
            var deliveryAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Fault(string label)
            {
                if (label != boundary) return Task.CompletedTask;
                deliveryAttempted.TrySetResult();
                throw new IOException("airship delivery fault");
            }
            if (afterCommit) accounts.BeforeOwnedApplyForTest = Fault;
            else accounts.BeforeOwnedCommitForTest = Fault;
            try
            {
                await PlotEnd(alice, 130163);
                await deliveryAttempted.Task.WaitAsync(Wait);
                if (boundary == "economy/dungeon-reward" && !afterCommit)
                {
                    var pending = Assert.Single(Service<DungeonObligationStore>().Pending(role));
                    Assert.True(pending.Success);
                    Assert.Equal(scene, pending.SceneEntry);
                }
                else Assert.Empty(Service<DungeonObligationStore>().Pending(role));
                Assert.False(TaskService.IsDelivered(session, task));
                Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.Result));
            }
            finally { accounts.BeforeOwnedCommitForTest = null; accounts.BeforeOwnedApplyForTest = null; }
            if (boundary == "economy/cd13e/quest-award" && !afterCommit)
            {
                // 角色仍在線時也能重試已確認目標後的發獎債務，不需要再增加一次目標。
                await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(session));
                AssertAirshipTaskRecovered(alice, role);
            }
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();

        await using (var recovered = await InWorld("Alice", create: false))
        {
            AssertAirshipTaskRecovered(recovered, role);
            await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(recovered)));
            await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(recovered)));
            await PlotEnd(recovered, 130163);
            await PlotEnd(recovered, 130162);
            await recovered.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = TaskService.FirstTask });
            await Next(recovered, TaskService.TaskFollow, TaskFollowResp.Parser);
            AssertAirshipTaskRecovered(recovered, role);
        }
        await using var again = await InWorld("Alice", create: false);
        AssertAirshipTaskRecovered(again, role);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AirshipClearDoesNotCreditATaskAcceptedOrReacceptedAfterItsFreeze(bool replaceOriginal)
    {
        const long scene = 1000501, task = 10103210;
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm(alice, "!lvl 99");
            if (replaceOriginal) await Gm(alice, $"!quest accept {task}");
            role = (long)alice.Self!.Uuid;
            Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
            long instance = await Load(alice, scene);
            await ClearReconstructedStoryRun(alice, instance);
            await Story(alice, 130161);
            var accounts = Service<AccountStore>();
            var deliveryAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/dungeon-reward") return Task.CompletedTask;
                deliveryAttempted.TrySetResult();
                throw new IOException("airship later acceptance fault");
            };
            try
            {
                await PlotEnd(alice, 130163);
                await deliveryAttempted.Task.WaitAsync(Wait);
                var clear = Assert.Single(Service<DungeonObligationStore>().PendingTaskClears(role, earnedOnly: false));
                Assert.Equal(replaceOriginal ? 1 : 0, clear.Targets.Count);
            }
            finally { accounts.BeforeOwnedCommitForTest = null; }
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            await Gm(alice, $"!quest accept {task}");
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Empty(Service<DungeonObligationStore>().Pending(role));
        Assert.Empty(Service<DungeonObligationStore>().PendingTaskClears(role));
        Assert.Equal(TaskState.Running, Session(recovered).Tasks!.Infos[task].Status);
        Assert.Equal(0, Session(recovered).Tasks!.CompleteTaskNum.GetValueOrDefault(task));
        Assert.Equal(0, AirshipQuestSourceCount(role, task));
    }

    private void AssertAirshipTaskRecovered(Ro3Emu.Bot.BotClient recovered, long role)
    {
        Assert.Empty(Service<DungeonObligationStore>().Pending(role));
        Assert.Empty(Service<DungeonObligationStore>().PendingTaskClears(role));
        Assert.Equal(1, Session(recovered).Tasks!.CompleteTaskNum.GetValueOrDefault(10103210));
        Assert.Equal(1, Session(recovered).Tasks!.CompleteTaskNum.GetValueOrDefault(10103220));
        Assert.Equal(TaskState.Running, Session(recovered).Tasks!.Infos[10104010].Status);
        Assert.Equal(1, AirshipQuestSourceCount(role, 10103210));
        Assert.Equal(1, AirshipQuestSourceCount(role, 10103220));
    }

    private long AirshipQuestSourceCount(long role, long task)
    {
        using var db = Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key LIKE $source AND r.command_kind='cd13e/quest-award'
            """;
        command.Parameters.AddWithValue("$source", $"task/{role}/{task}/1/%");
        return (long)command.ExecuteScalar()!;
    }
}
