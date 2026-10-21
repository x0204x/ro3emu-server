using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Growth Classroom's quests (GrowthClassService has the ranks and 5045). They are the TaskConfig type 20 rows with a
/// module (_iTaskSonType, a BecomeStrongerConfig id) and a stage (_iStrongerRank 1..6): _kAcceptTask {}, no _kPreTask,
/// _kDeliverTask [1] and no award, so the pre-task graph never offers them. The panel reads them from 5027 like any
/// quest (LC_BecomeStrongerHelper.GetGrowthTaskDataList: done = completeTaskNum, else the running completeNum) and
/// shows a stage's rows only up to the module's moduleUnlockedStage, else "Complete all objectives in the ${stage}
/// stage first" (UI_Sys_BecomeStronger_GrowthGuideTask_ListWidgetCtrl.lua:93-140 and :255-292, word 43035). So a
/// module's next stage opens when every quest of its open stage is delivered, and the open stages' quests are accepted
/// and delivered automatically like the story's.
/// (inferred) A module counts once function 48 (Become Stronger) and its BecomeStrongerConfig._iFunction are open: the
/// client's _checkModuleUnlock reads that function's open condition.
/// </summary>
public sealed partial class TaskService
{
    public const long BecomeStrongerFunction = 48;
    private const int GrowthClassType = 20;

    private Dictionary<long, (long Module, int Stage)>? _growthClass;

    /// <summary>5045 follows every quest flush (GrowthClassService).</summary>
    public Func<GameSession, Task>? SyncGrowthClass { get; set; }

    /// <summary>Growth Classroom quest id → (module, stage).</summary>
    internal IReadOnlyDictionary<long, (long Module, int Stage)> GrowthClassTasks() => _growthClass ??= _data.Table("TaskConfig").Values
        .Where(r => r.ValueKind == JsonValueKind.Object && GameData.Int(r, "_iTaskType") == GrowthClassType
            && GameData.Int(r, "_iStrongerRank") > 0 && _data.Row("BecomeStrongerConfig", GameData.Int(r, "_iTaskSonType")) != null
            && Defs().ContainsKey(GameData.Int(r, "_iTaskID")))
        .ToDictionary(r => GameData.Int(r, "_iTaskID"), r => (GameData.Int(r, "_iTaskSonType"), (int)GameData.Int(r, "_iStrongerRank")));

    internal bool GrowthModuleOpen(GameSession session, long module) =>
        _functions.IsOpen(session, BecomeStrongerFunction) && _data.Row("BecomeStrongerConfig", module) is { } row
        && _functions.IsOpen(session, GameData.Int(row, "_iFunction"));

    /// <summary>The module's open stage: 1, plus each stage in order whose quests are all delivered (at most the last).</summary>
    internal int GrowthStage(GameSession session, long module)
    {
        var stages = GrowthClassTasks().Where(t => t.Value.Module == module)
            .GroupBy(t => t.Value.Stage).OrderBy(g => g.Key).ToList();
        int open = 1;
        foreach (var stage in stages)
        {
            if (stage.Key > open || !stage.All(t => IsDelivered(session, t.Key))) break;
            open = stage.Key + 1;
        }
        return Math.Min(open, stages.Count == 0 ? 1 : stages[^1].Key);
    }

    /// <summary>Accepts the open stages' quests of every open module; returns how many.</summary>
    private int AcceptGrowthClass(GameSession session)
    {
        int accepted = 0;
        foreach (var module in GrowthClassTasks().Values.Select(t => t.Module).Distinct())
        {
            if (!GrowthModuleOpen(session, module)) continue;
            int stage = GrowthStage(session, module);
            foreach (var (id, task) in GrowthClassTasks())
            {
                if (task.Module != module || task.Stage > stage || Active(session, id) || IsDelivered(session, id)) continue;
                Accept(session, id);
                accepted++;
            }
        }
        return accepted;
    }
}
