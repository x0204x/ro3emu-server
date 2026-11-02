namespace Ro3Emu.Server.Game;

/// <summary>
/// Adventure Group quest targets (AdventureGroupService). ProgressConfig 322001 "leader has not selected a sapling",
/// 322002 SetAdventureGroupDeclaration, 322003 WaterAdventureGroupSapling (LC_ConditionDefine.lua:427-432). The creation
/// quests 2000500-2000502 (PublicParam AdventureGroupSwornTask, dungeon 1010001) show their total from the dungeon variable
/// CCurPlayerCount (LC_InteractCondition.lua:5136-5198), not from the configured 10, so the ritual sets their counters
/// itself and completes a step when every member is done. The daily watering quest 2000000 is [322003, 1, 1]; the third
/// value tells it from the ritual's [322003, 10, 0] (inferred).
/// </summary>
public sealed partial class TaskService
{
    internal const int TSaplingChosen = 322001, TDeclaration = 322002, TWaterSapling = 322003;

    /// <summary>After a committed delivery: the task and the award it granted (AdventureGroupService counts the group
    /// quests' Adventure Bond Points as group EXP). Runs inside the quest lock: listeners post their work.</summary>
    public List<Action<GameSession, long, AwardPlan>> QuestDelivered { get; } = [];

    /// <summary>TaskConfig._iTaskType of a quest (23 = enum_TaskType.AdventureGroup), 0 when unknown.</summary>
    public int TypeOf(long taskId) => Defs().TryGetValue(taskId, out var def) ? def.Type : 0;

    /// <summary>One watering of the group's tree outside the creation instance.</summary>
    public Task OnSaplingWateredAsync(GameSession session) =>
        ProgressAsync(session, TWaterSapling, t => t.Length >= 3 && t[2] == 1 ? 1 : 0);

    /// <summary>Restart the creation chain: the earlier run's quests are dropped, the first one is accepted.</summary>
    internal async Task StartAdventureRitualAsync(GameSession session, IReadOnlyList<long> chain)
    {
        using var gate = await LockTasksAsync(session);
        if (session.Tasks == null || chain.Count == 0 || !Defs().ContainsKey(chain[0])) return;
        foreach (long id in chain) { session.Tasks.Infos.Remove(id); session.Tasks.CompleteTaskNum.Remove(id); }
        Accept(session, chain[0]);
        await FlushAsync(session);
    }

    /// <summary>
    /// Show <paramref name="value"/> on a running creation quest; <paramref name="complete"/> fills its target so the
    /// Auto delivery settles it, then <paramref name="next"/> (when given) is accepted.
    /// </summary>
    internal async Task SetAdventureRitualAsync(GameSession session, long taskId, long value, bool complete, long next = 0)
    {
        using var gate = await LockTasksAsync(session);
        if (session.Tasks == null || !Defs().TryGetValue(taskId, out var def)) return;
        if (!Active(session, taskId))
        {
            if (IsDelivered(session, taskId) && !complete) return;
            if (!IsDelivered(session, taskId)) Accept(session, taskId);
        }
        if (Active(session, taskId) && def.Targets.Length > 0)
        {
            var info = session.Tasks.Infos[taskId];
            long required = Math.Max(1, Required(session, def.Targets[0]));
            info.CompleteNum[0] = complete ? required : Math.Clamp(value, 0, required - 1);
        }
        if (complete && next != 0 && Defs().ContainsKey(next) && !Active(session, next) && !IsDelivered(session, next))
            Accept(session, next);
        await SettleAsync(session);
    }
}
