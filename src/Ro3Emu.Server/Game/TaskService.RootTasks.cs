namespace Ro3Emu.Server.Game;

/// <summary>
/// Main, Side and Guide quests that follow no other quest (TaskConfig rows with an empty _kPreTask and _kAcceptTask
/// [1] Auto; enum_TaskType Main 1, Side 2, Guide 3). Their only gate is _iCondition: 19991050 Third Class Advancement
/// [88000107: base level 50], the Side quests such as 10200010 and the Guide rows 306070/306080. The client never asks for
/// an Auto quest; LC_DAL_Task only shows the 5027 task list it is sent, so the server starts each one once its condition
/// holds (inferred: the only other root, FirstTask, is given to a new role). The hidden Connect quests (type 19,
/// TaskService.Connect.cs) start the same way. Without this a role that finished the
/// main story (10108262 "Time to Explore Freely", target base level 999, the end of the current main quests) never
/// got the Third Class Advancement chain.
/// </summary>
public sealed partial class TaskService
{
    private const int MainTaskType = 1, SideTaskType = 2, GuideTaskType = 3;

    private TaskDef[]? _rootTasks;

    private TaskDef[] RootTasks() => _rootTasks ??= [.. Defs().Values
        .Where(d => d.Pre.Length == 0 && d.Accept == ReceiveAuto && d.Id != FirstTask
            && d.Type is MainTaskType or SideTaskType or GuideTaskType or ConnectTaskType)
        .OrderBy(d => d.Id)];

    private int AcceptRootTasks(GameSession session)
    {
        int accepted = 0;
        foreach (var def in RootTasks())
        {
            if (Active(session, def.Id) || IsDelivered(session, def.Id) || !Ready(session, def)) continue;
            Accept(session, def.Id);
            accepted++;
        }
        return accepted;
    }
}
