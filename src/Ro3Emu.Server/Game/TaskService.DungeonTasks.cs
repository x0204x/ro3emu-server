using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Dungeon tasks: TaskConfig enum_TaskType Dungeon (4) and RingTask (24) son types 2 and 3 that name an _iDungeonID.
/// The client tracks them only inside that dungeon (LC_TaskHelper.lua:892-940: in a Dungeon scene a Dungeon task, or a
/// RingTask of son type 2/3, shows when its _iDungeonID is the scene; elsewhere neither does). They carry no award, no
/// EXP and _iIfRepeat 1. G 的200003與210004有客戶端formatter；其餘另參照ProgressConfig：200000 "Reach Dungeon Stage
/// ${1}", 200001 "Rating: ${1}", 200003 "Armacia Weak Value", 210003 "${1} Assists", 210004 "Defeat ${1}" and, for 210005,
/// only the unmet tip "Quest requirements not met" (the step is the task's own text: "Use the Grappling Hook to Restrain
/// Almakia!", "Defeat the Marionette", ...). They are the run's tracker, which the original server's dungeon scripts drove.
/// (inferred) Each scene's tasks form one chain in _kPreTask order, kept out of the role's quest state (nothing is
/// delivered, so a run never writes the database): a run shows the first step it has not met through
/// <see cref="ExtraTasks"/>, as a Running 5027 entry with its counter, as the Dracula lair did. The run reports how
/// many of its scripted steps it has passed (DungeonService: the Dracula lair's waves; a Guild Challenge stage, rebuilt
/// as its boss fight, is at its last step; a settled run is over) and 200000/200003/210004/210005 of a passed step are
/// met; 200001 and 210003 read the member's battle score and assists (<see cref="DungeonRunValue"/>, the siege).
/// Without a run, or once the member leaves its instance, nothing is shown.
/// </summary>
public sealed partial class TaskService
{
    private const int TDungeonStage = 200000, TDungeonRating = 200001, TWeakValue = 200003, TDungeonAssists = 210003,
        TDungeonBoss = 210004, TDungeonStep = 210005;
    private const int DungeonTaskType = 4, RingTaskType = 24;

    /// <summary><see cref="OnDungeonRun"/> step: the run is at its chain's last step (a stage rebuilt as one fight).</summary>
    public const int FinalStep = int.MaxValue - 1;
    /// <summary><see cref="OnDungeonRun"/> step: the run is settled, its tracker leaves.</summary>
    public const int RunOver = int.MaxValue;

    private sealed class DungeonRun(long scene, long instance)
    {
        public long Scene { get; } = scene;
        public long Instance { get; } = instance;
        public int Step { get; set; }
    }

    private Dictionary<long, TaskDef[]>? _dungeonChains;
    private readonly ConditionalWeakTable<Ownership.AttachmentLease, DungeonRun> _dungeonRuns = [];

    /// <summary>200001 / 210003: the member's score and assists in the battle it is loaded into, -1 when none.</summary>
    public Func<GameSession, int, long>? DungeonRunValue { get; set; }

    /// <summary>
    /// A member's run of <paramref name="scene"/> (instance <paramref name="instance"/>) has passed <paramref name="step"/>
    /// of its scripted steps (<see cref="FinalStep"/>, <see cref="RunOver"/>); a new instance starts the chain over. Also
    /// called when a battle value changed. Re-sends 5027 on the role's owner loop.
    /// </summary>
    public void OnDungeonRun(GameSession s, long scene, long instance, int step)
    {
        if (!DungeonChains().ContainsKey(scene)) return;
        _loop.PostOwned(s, async () =>
        {
            using var gate = await LockTasksAsync(s);
            if (s.Tasks == null || s.Attachment is not { } attachment) return;
            if (!_dungeonRuns.TryGetValue(attachment, out var run) || run.Scene != scene || run.Instance != instance)
                _dungeonRuns.AddOrUpdate(attachment, run = new DungeonRun(scene, instance));
            run.Step = Math.Max(run.Step, step);
            await SyncTasksAsync(s);
        }, "quest dungeon run");
    }

    /// <summary>ExtraTasks: the live run's current step, if any is left.</summary>
    internal IEnumerable<TaskInfo> DungeonRunTasks(GameSession s)
    {
        if (s.Attachment is not { } attachment || !_dungeonRuns.TryGetValue(attachment, out var run)
            || run.Step == RunOver || s.InstanceId != run.Instance || !DungeonChains().TryGetValue(run.Scene, out var chain))
            return [];
        int passed = run.Step >= FinalStep ? chain.Length - 1 : run.Step;
        for (int step = 0; step < chain.Length; step++)
        {
            // R is each target's last parameter (the ${1} of its text).
            var targets = chain[step].Targets;
            var counts = targets.Select(t => t[0] is TDungeonRating or TDungeonAssists
                ? Math.Clamp(DungeonRunValue?.Invoke(s, (int)t[0]) ?? 0, 0, t[^1])
                : step < passed ? t[^1] : 0).ToArray();
            if (counts.Zip(targets, (n, t) => n == t[^1]).All(met => met)) continue;
            return [new TaskInfo { TaskId = chain[step].Id, Status = TaskState.Running, CompleteNum = { counts } }];
        }
        return [];
    }

    /// <summary>The dungeon task ids of <paramref name="scene"/> in chain order.</summary>
    internal long[] DungeonTaskChain(long scene) => DungeonChains().TryGetValue(scene, out var chain) ? [.. chain.Select(d => d.Id)] : [];

    /// <summary>Scene → its dungeon tasks in chain order (each after its _kPreTask, ties by id).</summary>
    private Dictionary<long, TaskDef[]> DungeonChains()
    {
        if (_dungeonChains != null) return _dungeonChains;
        var defs = Defs();
        var tasks = _data.Table("TaskConfig").Values.Where(r => r.ValueKind == JsonValueKind.Object).Select(r => new TaskConfig(r))
            .Where(t => t.DungeonId > 0 && (t.TaskType == DungeonTaskType || t.TaskType == RingTaskType && t.TaskSonType is 2 or 3)
                && defs.TryGetValue(t.TaskId, out var def) && def.Targets.Length > 0 && def.Targets.All(DungeonTarget));
        return _dungeonChains = tasks.GroupBy(t => t.DungeonId).ToDictionary(g => g.Key, g => ChainOrder(g.Select(t => defs[t.TaskId])));
    }

    private static TaskDef[] ChainOrder(IEnumerable<TaskDef> tasks)
    {
        var left = tasks.OrderBy(d => d.Id).ToList();
        var chain = new List<TaskDef>();
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(d => d.Pre.All(p => chain.Any(c => c.Id == p) || left.All(o => o.Id != p))) ?? left[0];
            chain.Add(next);
            left.Remove(next);
        }
        return [.. chain];
    }

    private static bool DungeonTarget(long[] t) => t switch
    {
        [TDungeonStage or TDungeonRating or TWeakValue or TDungeonAssists or TDungeonStep, > 0] => true,
        [TDungeonBoss, _, > 0] => true,
        _ => false,
    };
}
