using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season activity tasks (TaskConfig type 13, enum_TaskType.SeasonTreasure). The client lists an open activity's tasks
/// from NewActivityConfig._kTaskRenew [refresh, type, sonType, ?] (Lua_CFG_NewActivityConfig.GetActivityTaskList ->
/// Lua_CFG_TaskConfig.GetTaskListByType; LC_System_ActivityRedDot) and reads their state from 5027; their panels claim
/// with 31205 (LC_TaskHelper.SendTaskGetward from UI_Sys_Season_TreasureTaskWidgetCtrl, _SoulActivityListWidgetCtrl,
/// _MovementRewardListWidgetCtrl). The rows carry no usable accept or delivery mode (_kAcceptTask [0]/[1]/{},
/// _kDeliverTask {}/[0]), so the server offers them while their activity is open (Lost Treasure 106, Realm Campaign
/// 107, Genesis Movement 111, Ode to Echoes 115, Pass II 9201) and lets the panel claim a finished one (inferred: the
/// original server's accept and claim rules are not in the client). The first _kTaskRenew value is a TimeLimitConfig
/// _iTimeLimitId: 1013 is row 18 "refreshTaskDaily" (_iDayStart 18000, 05:00), so the tasks of such a group start over
/// each game day (Lost Treasure's login and treasure hunt, Pass II's daily activity); 0 never renews. The last value
/// (11300, 1108, ...) is not interpreted.
/// </summary>
public sealed partial class TaskService
{
    private const int SeasonTreasureType = 13;

    /// <summary>The (type, sonType) task groups of the session's open NewActivityConfig rows and whether each renews daily
    /// (NewActivityService).</summary>
    public Func<GameSession, IReadOnlyDictionary<(int Type, int Son), bool>>? OpenActivityTaskGroups { get; set; }

    private Dictionary<long, int>? _activityTasks;

    /// <summary>Type 13 task id → its _iTaskSonType.</summary>
    private Dictionary<long, int> ActivityTasks() => _activityTasks ??= _data.Table("TaskConfig").Values
        .Where(r => r.ValueKind == JsonValueKind.Object && GameData.Int(r, "_iTaskType") == SeasonTreasureType
            && Defs().ContainsKey(GameData.Int(r, "_iTaskID")))
        .ToDictionary(r => GameData.Int(r, "_iTaskID"), r => (int)GameData.Int(r, "_iTaskSonType"));

    private bool ActivityTaskOpen(GameSession session, long id, IReadOnlyDictionary<(int Type, int Son), bool>? groups = null) =>
        ActivityTasks().TryGetValue(id, out int son)
        && (groups ?? OpenActivityTaskGroups?.Invoke(session))?.ContainsKey((SeasonTreasureType, son)) == true;

    /// <summary>Accepts the open activities' tasks; returns how many.</summary>
    private int AcceptActivityTasks(GameSession session)
    {
        if (OpenActivityTaskGroups?.Invoke(session) is not { Count: > 0 } groups) return 0;
        int accepted = 0;
        // The current game day began at the daily epoch (05:00, DailyActivityService); 0 before the first reconcile.
        // TaskInfo.startTime is the server clock's acceptance time.
        long today = (session.Daily?.DailyEpoch ?? 0) * 1000;
        foreach (var (id, son) in ActivityTasks())
        {
            if (!groups.TryGetValue((SeasonTreasureType, son), out bool daily)) continue;
            if (daily && today > 0 && session.Tasks!.Infos.TryGetValue(id, out var old) && old.StartTime < today)
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

    /// <summary>A finished activity task without a delivery mode is claimed in its activity's panel (31205).</summary>
    private bool ActivityClaim(GameSession session, TaskDef def) =>
        def.Type == SeasonTreasureType && def.Deliver == 0 && ActivityTaskOpen(session, def.Id);

    /// <summary>106801 at login: every running login target counts this login.</summary>
    private void CountLogin(GameSession session)
    {
        foreach (var info in session.Tasks!.Infos.Values.Where(i => i.Status == Running))
            if (Defs().TryGetValue(info.TaskId, out var def)) StepLoginTargets(session, def, info);
    }

    /// <summary>A newly accepted login target starts at the login it was accepted in (inferred).</summary>
    private void StartLoginTargets(GameSession session, long id)
    {
        if (session.Tasks!.Infos.TryGetValue(id, out var info)) StepLoginTargets(session, Defs()[id], info);
    }

    private void StepLoginTargets(GameSession session, TaskDef def, Romsg.TaskInfo info)
    {
        for (int i = 0; i < def.Targets.Length && i < info.CompleteNum.Count; i++)
            if (def.Targets[i] is [TLogin, > 0] t)
                info.CompleteNum[i] = Math.Min(Required(session, t), Math.Max(0, info.CompleteNum[i]) + 1);
    }
}
