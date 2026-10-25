using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle weekly tasks (TaskConfig 2501-2507, _iTaskType 10 = enum_TaskType.Chicken). The lobby's task button
/// lists every 5027 task of that type (LC_ChickenHelper.GetChickenTaskList -> UI_Sys_Chicken_SelectTaskPanel) and
/// claims a finished one with 31205 (_kDeliverTask [3], UI). The rows have no accept mode or pre-task; _iCondition is a
/// 6508 Survivor rank bound. LC_DAL_Task raises the red dot ChickenTaskMondayRefresh when one turns Running again, so
/// (inferred) the set is offered while Survivor Battle (FunctionConfig 65) is open and starts over each game week
/// (Monday 05:00, DailyActivityService). A task that turns Finished pops UI_Sys_Chicken_TaskFinishPanel through 31211
/// TaskRewardNotify{rewardType 1 Chicken, taskId} (LC_DAL_Task._dealTaskRewardNotify).
/// </summary>
public sealed partial class TaskService
{
    internal const int ChickenTaskType = 10;
    private const long ChickenFunction = 65;
    private const int RewardTypeChicken = 1; // ETaskRewardNum_Chicken

    private long[]? _chickenTasks;

    private long[] ChickenTasks() => _chickenTasks ??= [.. _data.Table("TaskConfig").Values
        .Where(r => r.ValueKind == JsonValueKind.Object).Select(r => new TaskConfig(r))
        .Where(t => t.TaskType == ChickenTaskType && Defs().ContainsKey(t.TaskId)).Select(t => t.TaskId).Order()];

    /// <summary>Accepts the week's Survivor tasks the rank allows; returns how many.</summary>
    private int AcceptChickenTasks(GameSession session)
    {
        if (ChickenTasks().Length == 0 || !_functions.IsOpen(session, ChickenFunction)) return 0;
        long week = (session.Daily?.WeeklyEpoch ?? 0) * 1000;
        int accepted = 0;
        foreach (long id in ChickenTasks())
        {
            if (week > 0 && session.Tasks!.Infos.TryGetValue(id, out var old) && old.StartTime < week)
            {
                session.Tasks.Infos.Remove(id);
                session.Tasks.CompleteTaskNum.Remove(id);
            }
            if (Active(session, id) || IsDelivered(session, id) || !Ready(session, Defs()[id])) continue;
            Accept(session, id);
            accepted++;
        }
        return accepted;
    }

    /// <summary>31211 after the save that made these Survivor tasks Finished.</summary>
    private static async Task NotifyChickenFinishedAsync(GameSession session, IEnumerable<long> finished)
    {
        foreach (long id in finished)
            await session.SendGameAsync(TaskRewardNotice,
                new Romsg.TaskRewardNotify { RewardType = (Romsg.ETaskRewardNum)RewardTypeChicken, TaskId = (int)id });
    }
}
