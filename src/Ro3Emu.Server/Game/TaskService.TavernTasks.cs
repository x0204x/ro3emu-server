using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Tavern's daily quest (TaskConfig 2000 "Daily Tavern Quests", _iTaskType 9 = enum_TaskType.BarRoom,
/// PublicParamConfig.BarroomTaskID): [106010, 1200] "Spend 20 minutes in any Tavern room". The Tavern window's
/// achievement panel lists it from 5027 and only reads its status (UI_Sys_Chat_BarRoom_AchievementTitleWidget: status
/// above 2 is done); it has no claim button and the row no _kDeliverTask, so (inferred) it delivers itself when complete,
/// granting its _iAward 8000 (MultiverseGroupConfig -> AwardConfig 1008000, three Hot Cocoa). (inferred) It is offered
/// while the Tavern (FunctionConfig 2410) is open and starts over every day at 05:00, as the Survivor weekly tasks do
/// every week. Its target reads today's time in a room (TavernService.TodaySeconds), so a relog loses nothing; the guild
/// To-Do tasks that reuse 106010 as an event counter (TaskService.GuildEvents) are not touched.
/// </summary>
public sealed partial class TaskService
{
    internal const int BarRoomTaskType = 9;
    private const long TavernFunction = 2410;

    private long[]? _tavernTasks;

    /// <summary>Today's seconds in a Tavern room, from TavernService (which depends on this service).</summary>
    public Func<GameSession, long>? TavernSeconds { get; set; }

    private long[] TavernTasks() => _tavernTasks ??= [.. _data.Table("TaskConfig").Values
        .Where(r => r.ValueKind == JsonValueKind.Object).Select(r => new TaskConfig(r))
        .Where(t => t.TaskType == BarRoomTaskType && Defs().ContainsKey(t.TaskId)).Select(t => t.TaskId).Order()];

    /// <summary>Accepts today's Tavern quest once the function is open; a copy from an earlier day starts over.</summary>
    private int AcceptTavernTasks(GameSession session)
    {
        if (TavernTasks().Length == 0 || !_functions.IsOpen(session, TavernFunction)) return 0;
        long day = (session.Daily?.DailyEpoch ?? 0) * 1000;
        int accepted = 0;
        foreach (long id in TavernTasks())
        {
            if (day > 0 && session.Tasks!.Infos.TryGetValue(id, out var old) && old.StartTime < day)
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

    /// <summary>The Tavern quest has no delivery of its own: it is handed in as an Auto quest.</summary>
    private static bool DeliversItself(TaskDef def) => def.Type == BarRoomTaskType && def.Deliver == 0;

    /// <summary>106010 of a Tavern quest: today's time in a room; -1 (not passive) for any other quest's 106010.</summary>
    private long TavernStay(GameSession session, TaskDef def, long[] t) =>
        t[0] == TBarroomActTime && def.Type == BarRoomTaskType ? TavernSeconds?.Invoke(session) ?? 0 : -1;

    /// <summary>Time in a Tavern room was written: today's quest re-reads it on the owner loop.</summary>
    public void OnTavernStay(GameSession session) => QueueRefresh(session);
}
