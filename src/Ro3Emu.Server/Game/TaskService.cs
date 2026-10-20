using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Quests. All task state is the AUTO_SYNC dataset 5027 DBTaskInfo; the client only reports events
/// (31208 dialog finished, 31209 area entered, 31214 client interaction, 84011 cutscene ended) and the server drives
/// the rest (§3.7): Auto tasks (_kAcceptTask [1]) are accepted as soon as their _kPreTask are delivered, targets
/// (_kProgressTarget) count up to exactly the client's required value, Auto delivery (_kDeliverTask [1]) grants _kEXP base
/// exp and the AwardConfig items, marks the task status 4 + completeTaskNum, accepts the follow-ups and reopens functions
/// (condition 4004). Npc/UI quests use 31202 accept / 31205 delivery.
/// </summary>
public sealed partial class TaskService
{
    public const uint TaskInfoSync = 5027;
    public const uint TaskAccept = 31202, TaskGiveUp = 31203, TaskDelivery = 31205, TaskFollow = 31206;
    public const uint TaskReset = 31204, TaskRewardNotice = 31211, TaskNpcChat = 31212, TaskFinishPyd = 31213;
    public const uint TaskSelectQuestBranch = 31215, TaskLuckyTurn = 31217, TaskTrackTeleport = 30016;
    public const uint TriggerTaskEvent = 31214, PlotActingEnd = 84011, ExitCutSceneState = 32709, TaskTrack = 31216;
    public const uint GoodsStack = 83651;
    public const uint ApiError = 99999;

    // enum TaskState (MG_Task.proto)
    private const TaskState Running = TaskState.Running, Finished = TaskState.Finish, Delivered = TaskState.Delivery;

    // LC_ConditionDefine.enum_Progress target types
    private const int TDialog = 104001, TArea = 104003, TBaseLevel = 101002, TClientInteract = 103046, TPlot = 309002,
        TKill = 104002, TKillInvolved = 104014, TKillOnMap = 310001, TJobChange = 104005, TSkillPoints = 104012,
        TStatPoints = 104013, TTaskDone = 104004, TJobRank = 101019;

    private const int ReceiveAuto = 1, ReceiveNpc = 2, ReceiveUi = 3; // enum_TaskReceiveType / enum_TaskRewardType

    /// <summary>The main story root, accepted when a character is created (no _kPreTask).</summary>
    public const long FirstTask = 10101010;

    /// <summary>Main-story points where characters older than the quest system resume (§4: they keep their windows).</summary>
    private const long NoviceResume = 10101150, FirstJobResume = 10101210;

    private sealed record TaskDef(long Id, int Type, long[] Pre, long Condition, int Accept, int Deliver, long[][] Targets,
        long Award, long Exp, long[] AcceptArgs, long[] DeliverArgs, string[][] AcceptActions, string[][] ExecActions, string[][] Track);

    private readonly GameData data;
    private readonly CharacterService character;
    private readonly SkillService skills;
    private readonly FunctionService functions;
    private readonly RoleStore roles;
    private readonly WorldData worldData;
    private readonly GameLoop loop;
    private readonly AwardService awards;
    private readonly SessionRegistry sessions;
    private readonly ILogger<TaskService> log;
    private readonly SeasonTalentService talents;
    private readonly MountService mounts;
    private readonly SceneService scene;
    private readonly TimeProvider clock;
    private Dictionary<long, TaskDef>? _defs;
    private Dictionary<long, List<long>>? _children;

    public Func<GameSession, Task>? BeforeProgress { get; set; }
    internal Func<GameSession, Task>? AfterLoginSettlementForTest { get; set; }
    public Func<GameSession, Task>? RefreshDailyTracking { get; set; }
    public Func<GameSession, long, DailyActivityService.DeliveryPlan?>? PrepareDailyDelivery { get; set; }
    public Func<GameSession, Task>? SyncDailyDelivery { get; set; }

    public TaskService(GameData data, CharacterService character, SkillService skills, FunctionService functions, CombatService combat,
        RoleStore roles, WorldData worldData, GameLoop loop, AwardService awards, SessionRegistry sessions,
        EquipmentService equipment, CardService cards, SeasonTalentService talents, SceneService scene, MountService mounts, TimeProvider clock,
        ILogger<TaskService> log)
    {
        this.data = data;
        this.character = character;
        this.skills = skills;
        this.functions = functions;
        this.roles = roles;
        this.worldData = worldData;
        this.loop = loop;
        this.awards = awards;
        this.sessions = sessions;
        this.log = log;
        this.talents = talents;
        this.mounts = mounts;
        this.scene = scene;
        this.clock = clock;
        combat.Killed.Add(OnKillAsync);
        combat.Dealt.Add((s, m, damage) => { if (damage > 0 && s.Role != null) _participants.GetOrCreateValue(m).Add(s.Role.Id); });
        character.JobChanged.Add(s => { QueueRefresh(s); return Task.CompletedTask; });
        // Level, stat and skill point targets are checked after the change settles (the handlers run inside
        // CharacterService's lock; a delivery grants exp, which takes it again).
        character.ProgressHandlers.Add(s => { QueueRefresh(s); return Task.CompletedTask; });
        skills.Learned.Add(s => { QueueRefresh(s); return Task.CompletedTask; });
        character.StateChanged.Add(QueueRefresh);
        character.ItemUsed.Add((s, entry, count) => loop.PostOwned(s, () => OnItemUsedAsync(s, entry, count)));
        equipment.Crafted.Add(OnCraftedAsync);
        equipment.Refined.Add(s => ProgressAsync(s, TRefineCount, _ => 1));
        // CardService releases the character lock before this callback and awaits it before the 82404 reply.
        cards.RequiredCollectionRefresh = RefreshCollectionRequiredAsync;
        character.GetKillTaskParticipantKeys = GetKillTaskParticipantKeys;
        character.StageKillTaskObligations = StageKillTaskObligations;
    }

    /// <summary>Delivered = completeTaskNum &gt; 0 or status 4 (the client's CheckTaskDeliveryed). Without quest state: true.</summary>
    /// <summary>
    /// Tasks the client shows that are not quests of this service (the pass challenges, NewActivityService.PassPlay):
    /// added to every 5027 so the dataset's diff never drops them. They are not in <see cref="GameSession.Tasks"/>.
    /// </summary>
    public List<Func<GameSession, IEnumerable<TaskInfo>>> ExtraTasks { get; } = new();

    /// <summary>5027: the role's quests plus <see cref="ExtraTasks"/>, as the change since the last send.</summary>
    public Task SyncTasksAsync(GameSession session)
    {
        if (session.Tasks == null) return Task.CompletedTask;
        var view = session.Tasks.Clone();
        foreach (var extra in ExtraTasks)
            foreach (var info in extra(session)) view.Infos[info.TaskId] = info;
        return CharacterService.SyncAsync(session, TaskInfoSync, view);
    }

    public static bool IsDelivered(GameSession session, long taskId) =>
        session.Tasks is not { } t
        || (t.CompleteTaskNum.TryGetValue(taskId, out int n) && n > 0)
        || (t.Infos.TryGetValue(taskId, out var info) && info.Status == Delivered);

    // ---- login ----

    /// <summary>Loads (or starts) the role's quests and sends the full 5027; before 5056, which reads them.</summary>
    public async Task LoginAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        var role = session.Role!;
        if (!Defs().ContainsKey(FirstTask))
        {
            session.Tasks = null; // no client data (TaskConfig) loaded: no quests, nothing quest-gated
            return;
        }
        var saved = roles.LoadTasks(role.Id);
        session.Tasks = saved != null ? DBTaskInfo.Parser.ParseFrom(saved) : new DBTaskInfo();
        session.TaskRuntime = roles.LoadTaskRuntime(role.Id) is { } runtime ? JsonSerializer.Deserialize<TaskRuntimeState>(runtime) ?? new() : new();
        if (saved == null) Start(session);
        // Drop ids the client cannot decode (an unknown id breaks the update it is in).
        foreach (var id in session.Tasks.Infos.Keys.Where(id => !_defs!.ContainsKey(id)).ToList()) session.Tasks.Infos.Remove(id);
        foreach (var id in session.Tasks.CompleteTaskNum.Keys.Where(id => !_defs!.ContainsKey(id)).ToList()) session.Tasks.CompleteTaskNum.Remove(id);
        foreach (var info in session.Tasks.Infos.Values)
        {
            int slots = Math.Max(1, Defs()[info.TaskId].Targets.Length);
            while (info.CompleteNum.Count < slots) info.CompleteNum.Add(0);
            while (info.CompleteNum.Count > slots) info.CompleteNum.RemoveAt(info.CompleteNum.Count - 1);
        }
        CountLogin(session); // 106801, before this login's acceptances (they start at 1)
        AcceptReady(session);
        EvaluatePassive(session);
        // The full sync needs non-empty maps (they decode as nil otherwise and AllTaskInfo stays unset).
        if (session.Tasks.CompleteTaskNum.Count == 0) session.Tasks.CompleteTaskNum[FirstTask] = 0;
        Save(session);
        if (!session.DeferTaskSync) await SyncTasksAsync(session);
        // Deliveries found at login (e.g. a level target already met) are handled like any other change.
        await SettleAsync(session);
        QueueFriendCount(session); // 103019 reads the friend list, which is not loaded here
        if (AfterLoginSettlementForTest is { } afterLogin) await afterLogin(session);
    }

    /// <summary>
    /// First quest state: a new Novice gets the story root; a character made before quests existed resumes the main story
    /// where its job says it is, with the earlier tasks delivered (so its function windows stay open, §4).
    /// </summary>
    private void Start(GameSession session)
    {
        var p = session.Progress!;
        bool fresh = p.Career == 1000 && p.Level <= 1 && p.Exp == 0;
        if (fresh)
        {
            Accept(session, FirstTask);
            return;
        }
        long stop = (data.Row("JobConfig", p.Career) is { } job ? GameData.Int(job, "_iJobRank") : 1) >= 2 ? FirstJobResume : NoviceResume;
        for (long id = FirstTask; id != 0 && id != stop;)
        {
            session.Tasks!.Infos[id] = NewInfo(id, Delivered);
            session.Tasks.CompleteTaskNum[id] = 1;
            id = _children!.GetValueOrDefault(id)?.FirstOrDefault(c => _defs![c].Type == 1) ?? 0;
        }
        log.LogInformation("{Name} predates quests: main story resumes at {Task}", session.Role!.Name, stop);
    }

    // ---- client events ----

    /// <summary>31208 CLIENT_NPCINTERACT: a task dialog ended (target 104001 [npcOrPoint, map, dialog]).</summary>
    public Task OnDialogAsync(GameSession session, ClientNpcInteractReq req)
    {
        long npcId = req.NpcUid != 0 && worldData.Npc(req.NpcUid) is { } npc && npc.SceneEntry == session.SceneEntry
            && NearPoint(session, npc.X, npc.Z) ? npc.NpcId : 0;
        return ProgressAsync(session, TDialog, t =>
            t.Length == 4 && t[3] == req.DialogID && t[2] == session.SceneEntry
            && ((t[1] == req.Areapointid && worldData.Map(session.SceneEntry)?.Points.TryGetValue(t[1], out var point) == true
                    && NearPoint(session, point.X, point.Z))
                || (npcId != 0 && t[1] == npcId)) ? 1 : 0);
    }

    /// <summary>31209 CLIENT_ENTERAREA: target 104003 [map, area] (the region itself is trusted to the client).</summary>
    public Task OnAreaAsync(GameSession session, ClientEnterAreaReq req) =>
        ProgressAsync(session, TArea, t => t.Length >= 3 && t[1] == session.SceneEntry && t[2] == req.AreaId ? 1 : 0);

    /// <summary>31214 TASK_TRIGGER_EVENT: target 103046 [interactId, n]; the request's taskId field carries the interaction id.</summary>
    public async Task OnClientInteractAsync(GameSession session, TriggerTaskEventReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        // C3 reports the final required value, never a delta. Active task + configured map are the available
        // context (inferred); this packet cannot prove that the local interaction animation actually ran.
        bool matches = req.GoalCount > 0 && session.Tasks?.Infos.Values.Any(i => i.Status == Running
            && Defs().TryGetValue(i.TaskId, out var d) && ContextMapMatches(session, d)
            && d.Targets.Any(t => t is [TClientInteract, var id, var required] && id == req.TaskId && required == req.GoalCount)) == true;
        if (matches) await ProgressCoreAsync(session, TClientInteract,
            t => t is [TClientInteract, var id, var required] && id == req.TaskId && required == req.GoalCount ? long.MaxValue : 0,
            d => ContextMapMatches(session, d));
        await session.SendGameAsync(TriggerTaskEvent, new TriggerTaskEventResp
        { Result = matches ? 0 : ErrorCodes.TaskRequirements, TaskId = req.TaskId, GoalCount = req.GoalCount });
    }

    /// <summary>
    /// Target 101018 [scene, n] "dungeon pass": a server-settled successful clear of
    /// that dungeon scene, from DungeonService.Completed. Entering is not a pass.
    /// </summary>
    public Task OnDungeonClearedAsync(GameSession session, long scene) =>
        ProgressAsync(session, TDungeonPass, t => t is [TDungeonPass, var cleared, _] && cleared == scene ? 1 : 0);

    /// <summary>
    /// Target 106903 CheckAreaAction [map, region, emojiId]: an emote really started playing (state 14) on that map,
    /// inside that scene-editor region (EmojiService decides the region; the client).
    /// </summary>
    public Task OnEmotePlayedAsync(GameSession session, int emojiId, Func<long, long, bool> insideRegion) =>
        ProgressAsync(session, TAreaAction, t => t is [TAreaAction, var map, var region, var emoji]
            && emoji == emojiId && insideRegion(map, region) ? 1 : 0);

    /// <summary>84011 PLOT_ACTING_END: target 309002 [plotId] (required = the plot id, the client's describe quirk).</summary>
    public Task OnPlotEndAsync(GameSession session, PlotActingEndReq req) =>
        ProgressAsync(session, TPlot, t => t.Length >= 2 && t[1] == req.PlotId ? long.MaxValue : 0);

    /// <summary>103022 follows MvpService's participant settlement, including non-final-killer contributors.</summary>
    public Task OnMvpParticipantAsync(GameSession session) =>
        ProgressAsync(session, null, t => t[0] is TMvp or TMvpOnce ? 1 : 0);

    /// <summary>Stage target 104011 before the caravan transaction; login settles its durable completed target after a crash.</summary>
    /// <remarks>(inferred) Each button departure is one 104011 event for all running quests. Daily 101
    /// caps its own tracker at one, while other configured quests may ask for multiple departures.</remarks>
    public DBTaskInfo? StageCaravanDeparture(GameSession session)
    {
        if (session.Tasks == null) return null;
        var next = session.Tasks.Clone();
        foreach (var info in next.Infos.Values.Where(i => i.Status == Running))
        {
            if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
            for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
            {
                var target = def.Targets[index];
                if (target[0] != 104011 || !Supported(target)) continue;
                info.CompleteNum[index] = Math.Min(Required(session, target), info.CompleteNum[index] + 1);
            }
        }
        return next;
    }

    public async Task PrepareProgressAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
    }

    /// <summary>Initialize only the tracking quests referenced by eligible DailyActivityConfig rows.</summary>
    public async Task EnsureDailyTrackingAsync(GameSession session, IEnumerable<long> ids)
    {
        using var gate = await LockTasksAsync(session);
        await EnsureDailyTrackingCoreAsync(session, ids);
    }

    /// <summary>Called by daily reconciliation while the shared quest transition lock is held.</summary>
    internal async Task EnsureDailyTrackingCoreAsync(GameSession session, IEnumerable<long> ids)
    {
        if (session.Tasks == null) return;
        bool changed = false;
        foreach (long id in ids)
        {
            if (!Defs().TryGetValue(id, out var def) || Active(session, id) || !Ready(session, def)) continue;
            Accept(session, id);
            changed = true;
        }
        if (changed) await FlushAsync(session);
    }

    /// <summary>Assigns one configured chain root through ordinary 5027 ownership.</summary>
    public bool AssignRing(GameSession session, int root)
    {
        if (session.Tasks == null || root is not (3001 or 3011 or 3021) || !Defs().ContainsKey(root)
            || Enumerable.Range(root, 10).Any(id => Active(session, id))) return false;
        foreach (int id in Enumerable.Range(root, 10))
        { session.Tasks.Infos.Remove(id); session.Tasks.CompleteTaskNum.Remove(id); }
        Accept(session, root);
        return true;
    }

    public Task SyncCurrentAsync(GameSession session) => FlushAsync(session);

    /// <summary>Daily reset owns TaskLock and the ending epoch; settle elapsed owner progress without recursively resetting.</summary>
    internal Task SettleOwnerProgressCoreAsync(GameSession session) => session.Tasks == null ? Task.CompletedTask : SettleAsync(session);

    /// <summary>31206: the HUD tracker flag.</summary>
    public async Task FollowAsync(GameSession session, TaskFollowReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        bool ok = session.Tasks?.Infos.TryGetValue(req.Id, out var info) == true;
        if (ok) session.Tasks!.Infos[req.Id].Follow = req.Follow;
        await session.SendGameAsync(TaskFollow, new TaskFollowResp { Result = ok ? 0 : session.Tasks == null ? ErrorCodes.Unknown : ErrorCodes.TaskMissing, Id = req.Id, Follow = req.Follow });
        if (ok) await FlushAsync(session);
    }

    /// <summary>31202: accepting an Npc / UI quest (main-story ones are Auto).</summary>
    public async Task AcceptAsync(GameSession session, TaskAcceptReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        var defs = Defs();
        // Error selections are inferred from the error texts. Unknown is retained for absent server state;
        // receive-mode/repeat mismatches use status, not a fabricated repeat policy.
        int result = session.Tasks == null ? ErrorCodes.Unknown
            : !defs.TryGetValue(req.Id, out var def) ? ErrorCodes.TaskNoConfig
            : Active(session, req.Id) ? ErrorCodes.TaskExists
            : IsDelivered(session, req.Id) || def.Accept is not (ReceiveNpc or ReceiveUi) ? ErrorCodes.TaskStatus
            : !def.Pre.All(p => IsDelivered(session, p)) ? ErrorCodes.TaskPrerequisite
            : !Ready(session, def) ? ErrorCodes.TaskRequirements
            : def.Accept == ReceiveNpc ? ValidateNpc(session, def.AcceptArgs, req.NpcId) : 0;
        bool ok = result == 0;
        if (ok) Accept(session, req.Id);
        log.LogInformation("{Name} accepts task {Task}: {Result}", session.Role?.Name, req.Id, ok ? "ok" : "refused");
        await session.SendGameAsync(TaskAccept, new TaskAcceptResp { Result = result, Id = req.Id, NpcId = req.NpcId });
        if (ok) await SettleAsync(session);
    }

    /// <summary>31205: turning in Npc / UI quests whose targets are met.</summary>
    public async Task DeliverAsync(GameSession session, TaskDeliveryReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        if (session.Tasks != null) EvaluatePassive(session);
        var resp = new TaskDeliveryResp();
        foreach (var one in req.Req)
        {
            int result = session.Tasks == null ? ErrorCodes.Unknown
                : !session.Tasks.Infos.TryGetValue(one.Id, out var info) ? ErrorCodes.TaskMissing
                : !Defs().TryGetValue(one.Id, out var def) ? ErrorCodes.TaskNoConfig
                : info.Status != Finished || def.Deliver is not (ReceiveNpc or ReceiveUi) && !ActivityClaim(session, def) ? ErrorCodes.TaskStatus
                : ChapterLocked(info) ? ErrorCodes.TaskRequirements
                : !Complete(session, def, info) ? ErrorCodes.TaskRequirements
                : def.Deliver == ReceiveNpc ? ValidateNpc(session, def.DeliverArgs, one.NpcId) : 0;
            if (result == 0) result = await DeliverCoreAsync(session, RequireTaskOwner(session), one.Id);
            // LC_DAL_Task treats every data row as a success regardless of result (error-map §6).
            // Only committed rows go there; typed operation errors carry refusals. This mixed-batch policy is
            // inferred, not a recovered original-server ordering, and avoids false client TaskRewardEvent calls.
            if (result == 0) resp.Data.Add(new TaskDeliverySingleResp { Id = one.Id, NpcId = one.NpcId });
            else await session.SendGameAsync(ApiError, new APIErrorMsg
            {
                ErrorCode = result, OriginMsgId = TaskDelivery,
                ErrorParams = { new ErrorParam { ParamType = (ErrorParamType)7,
                    ParamValue = { one.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
            });
        }
        await session.SendGameAsync(TaskDelivery, resp);
        await SettleAsync(session);
    }

    /// <summary>31203: abandoning a quest that allows it.</summary>
    public async Task GiveUpAsync(GameSession session, TaskGiveUpReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        int result = session.Tasks == null ? ErrorCodes.Unknown
            : !session.Tasks.Infos.ContainsKey(req.Id) ? ErrorCodes.TaskMissing
            : data.Row("TaskConfig", req.Id) is not { } row ? ErrorCodes.TaskNoConfig
            : !Active(session, req.Id) ? ErrorCodes.TaskStatus
            : GameData.Int(row, "_iIfGiveUp") != 1 ? ErrorCodes.TaskNoGiveUp : 0;
        bool ok = result == 0;
        if (ok) session.Tasks!.Infos.Remove(req.Id);
        await session.SendGameAsync(TaskGiveUp, new TaskGiveUpResp { Result = result, Id = req.Id });
        if (ok) await FlushAsync(session);
    }

    // ---- GM ----

    /// <summary>GM: "accept|finish|done|drop &lt;id&gt;" or "reset" (back to a fresh Novice story).</summary>
    public async Task<string> GmAsync(GameSession session, string op, long id)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        if (session.Tasks == null) return "No quest state.";
        var defs = Defs();
        switch (op)
        {
            case "reset":
                session.Tasks = new DBTaskInfo();
                Accept(session, FirstTask);
                session.Tasks.CompleteTaskNum[FirstTask] = 0;
                break;
            case "drop":
                session.Tasks.Infos.Remove(id);
                session.Tasks.CompleteTaskNum.Remove(id);
                break;
            case "accept" when defs.ContainsKey(id):
                Accept(session, id);
                break;
            case "finish" when Active(session, id):
                var def = defs[id];
                var info = session.Tasks.Infos[id];
                for (int i = 0; i < def.Targets.Length; i++) info.CompleteNum[i] = Required(session, def.Targets[i]);
                break;
            case "done" when defs.ContainsKey(id):
                if (!Active(session, id)) Accept(session, id);
                if (await DeliverCoreAsync(session, RequireTaskOwner(session), id) != 0) return $"done {id}: settlement failed";
                break;
            default:
                return "usage: !quest accept|finish|done|drop <taskId> | reset";
        }
        await SettleAsync(session);
        return $"{op} {id}: {Describe(session)}";
    }

    public string Describe(GameSession session) => session.Tasks == null ? "-" :
        string.Join(", ", session.Tasks.Infos.Values.Where(i => i.Status is Running or Finished)
            .Select(i => $"{i.TaskId} {data.TextOf(data.Row("TaskConfig", i.TaskId)!.Value, "_iName")} [{string.Join('/', i.CompleteNum)}]"));

    // ---- engine ----

    /// <summary>
    /// Adds progress from an event to every running task's matching targets: <paramref name="amount"/> returns how much a
    /// target gains (long.MaxValue = complete it); <paramref name="type"/> limits it to one target type.
    /// </summary>
    private async Task ProgressAsync(GameSession session, int? type, Func<long[], long> amount)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        await ProgressCoreAsync(session, type, amount);
    }

    private async Task ProgressCoreAsync(GameSession session, int? type, Func<long[], long> amount, Func<TaskDef, bool>? eligible = null)
    {
        if (session.Tasks == null || session.Progress == null) return;
        bool changed = false;
        foreach (var info in session.Tasks.Infos.Values.Where(i => i.Status == Running).ToList())
        {
            if (!Defs().TryGetValue(info.TaskId, out var def) || eligible?.Invoke(def) == false) continue;
            for (int i = 0; i < def.Targets.Length && i < info.CompleteNum.Count; i++)
            {
                var t = def.Targets[i];
                if (type != null && t[0] != type) continue;
                long gain = amount(t);
                if (gain <= 0) continue;
                long required = Required(session, t);
                if (required <= 0) continue;
                long previous = Math.Clamp(info.CompleteNum[i], 0, required);
                long next = gain >= required - previous ? required : previous + gain;
                if (next == info.CompleteNum[i]) continue;
                info.CompleteNum[i] = next;
                changed = true;
            }
        }
        if (changed) await SettleAsync(session);
    }

    /// <summary>Re-reads the passive targets (level, job, points, other tasks) and settles.</summary>
    public async Task RefreshAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        await RefreshCoreAsync(session);
    }

    /// <summary>Required card hook runs inline under the original owner capability. It must never
    /// post behind the request whose reply is waiting for it.</summary>
    private async Task<bool> RefreshCollectionRequiredAsync(GameSession session, Ownership.OwnerContext context)
    {
        context.AssertRole(session.Role!.Id);
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        await RefreshCoreAsync(session);
        return !roles.HasTaskFollowup(session.Role.Id);
    }

    private async Task RefreshCoreAsync(GameSession session)
    {
        if (session.Tasks == null || session.Progress == null) return;
        AcceptReady(session);
        EvaluatePassive(session);
        await SettleAsync(session);
    }

    /// <summary>Finished tasks become status 3, Auto ones are delivered (and their follow-ups accepted) until nothing moves; then one 5027.</summary>
    private async Task SettleAsync(GameSession session)
    {
        if (session.Tasks == null) return;
        var context = RequireTaskOwner(session);
        bool debtIncomplete = false;
        bool requiredFollowup = roles.HasTaskFollowup(session.Role!.Id);
        for (int round = 0; round < 64; round++)
        {
            bool moved = false, storageFailed = false;
            EvaluatePassive(session);
            foreach (long taskId in session.Tasks.Infos.Values.Where(i => i.Status is Running or Finished).Select(i => i.TaskId).ToList())
            {
                var info = session.Tasks.Infos[taskId]; // a delivery atomically replaces the whole snapshot
                var def = Defs()[info.TaskId];
                if (!Complete(session, def, info))
                {
                    if (info.Status == Finished) { info.Status = Running; moved = true; }
                    continue;
                }
                if (info.Status != Finished) { info.Status = Finished; moved = true; }
                if (def.Deliver == ReceiveAuto)
                {
                    int result = await DeliverCoreAsync(session, context, def.Id);
                    if (result == 0) moved = true;
                    else if (result == ErrorCodes.Unknown)
                    {
                        // Storage failure stops this pass. Missing costs or unresolved awards hold only their own quest.
                        debtIncomplete = true;
                        storageFailed = true;
                        break;
                    }
                    else if (requiredFollowup) debtIncomplete = true;
                }
            }
            if (storageFailed) break;
            if (AcceptReady(session) > 0) moved = true;
            EvaluatePassive(session);
            if (round == 63 && moved) debtIncomplete = true;
            if (!moved) break;
        }
        // Login runs task settlement before CardService loads 5057. A previous role's Library
        // can still be present on a reused GameSession, so readiness is bound to this attachment.
        bool cardSourceReady = ReferenceEquals(session.CardLibraryReadyAttachment, session.Attachment);
        await FlushAsync(session, !debtIncomplete && (!requiredFollowup || cardSourceReady));
    }

    private async Task FlushAsync(GameSession session, bool settled = false)
    {
        if (settled && roles.HasTaskFollowup(session.Role!.Id))
            await roles.SaveSettledTasksAsync(session.Role.Id, session.Tasks!.ToByteArray(), JsonSerializer.SerializeToUtf8Bytes(session.TaskRuntime));
        else Save(session);
        if (!session.DeferTaskSync)
        {
            await SyncTasksAsync(session);
            if (SyncGrowthClass is { } growth) await growth(session); // 5045 reads the delivered growth quests
        }
        if (session.DailyReady && RefreshDailyTracking is { } refresh) await refresh(session);
        await talents.SyncAsync(session); // 5050 before 5056 when quest 10103160 opens function 1003
        await functions.SyncAsync(session);
    }

    /// <summary>Rewards first (the delivery events open UI that reads them), then status 4 and completeTaskNum.</summary>
    private async Task<int> DeliverCoreAsync(GameSession session, Ownership.OwnerContext context, long id)
    {
        context.AssertRole(session.Role!.Id);
        // Daily owns reissuing a Running instance even after a previous completion; history is not that instance's status.
        if (session.Tasks == null || !Active(session, id)) return ErrorCodes.TaskStatus;
        var daily = PrepareDailyDelivery?.Invoke(session, id);
        if (IsDelivered(session, id) && daily?.MirrorChanged != true) return ErrorCodes.TaskStatus;
        var def = Defs()[id];
        if (!TryDeliveryActions(session, context, def, out var costs, out bool cutScene, out int? questMount)) return ErrorCodes.TaskRequirements;
        foreach (var target in def.Targets.Where(t => t[0] == TSubmit))
        {
            if (!Supported(target) || target[2] > int.MaxValue) return ErrorCodes.TaskRequirements;
            costs.Add(((int)target[1], (int)target[2]));
        }
        var expectedTasks = daily == null ? null : session.Tasks.ToByteArray();
        AwardPlan? deliveredPlan = null;
        var attachment = session.Attachment;
        var next = session.Tasks.Clone();
        if (!next.Infos.ContainsKey(id)) next.Infos[id] = NewInfo(id, Running);
        next.Infos[id].Status = Delivered;
        next.CompleteTaskNum[id] = next.CompleteTaskNum.GetValueOrDefault(id) + 1;
        if (daily?.Repeat == true) next.Infos[id] = NewInfo(id, Running);
        try
        {
            // 5021 is the authoritative season snapshot sent to this session, also used by client previews.
            int? playBook = session.Synced.GetValueOrDefault(SeasonService.SeasonSync) is DBSeasonInfo { Current: { } current }
                ? (int)current.PlayBookId : null;
            var plan = awards.Resolve(session, def.Award, playBook);
            var runtime = new TaskRuntimeState { CompletedGuides = session.TaskRuntime.CompletedGuides.ToHashSet(), InCutScene = session.InCutScene || cutScene };
            string awardSource = $"task/{session.Role!.Id}/{id}/{next.CompleteTaskNum[id]}/" +
                $"{session.Daily?.DailyEpoch ?? 0}/{session.Daily?.WeeklyEpoch ?? 0}";
            if (!await character.ApplyAwardAsync(session, plan, def.Exp, costs, next.ToByteArray(),
                JsonSerializer.SerializeToUtf8Bytes(runtime), expectedDaily: daily?.Before, nextDaily: daily?.After,
                expectedTasks: expectedTasks, persist: questMount is { } ride ? tx => mounts.PersistQuestRide(session, ride, tx) : null,
                committed: questMount is { } appliedRide ? () => mounts.ApplyQuestRideCore(session, context, appliedRide) : null,
                ownedAward: new CharacterService.OwnedAwardSource("cd13e/quest-award", awardSource,
                    new { Version = 1, Task = id, Count = next.CompleteTaskNum[id], plan.AwardId })))
                return daily == null ? ErrorCodes.ItemsShort : ErrorCodes.Unknown;
            session.TaskRuntime = runtime;
            deliveredPlan = plan;
            if (questMount is { } selectedRide && attachment?.Cancelled != true && ReferenceEquals(session.Attachment, attachment))
                await mounts.PublishQuestRideAsync(session, selectedRide);
        }
        catch (AwardResolutionException e)
        {
            log.LogWarning("Task {Task} reward held: {Reason}", id, e.Message);
            return ErrorCodes.TaskRequirements;
        }
        catch (Exception e) when (daily != null || roles.HasTaskFollowup(session.Role!.Id))
        {
            log.LogError(e, "Task {Task} transaction failed for {Role}", id, session.Role?.Id);
            return ErrorCodes.Unknown;
        }
        session.Tasks = next;
        if (daily?.MirrorChanged == true && SyncDailyDelivery is { } sync) await sync(session);
        if (cutScene) session.InCutScene = true;
        if (deliveredPlan != null) foreach (var handler in QuestDelivered) handler(session, id, deliveredPlan);
        log.LogInformation("{Name} delivers task {Task} {TaskName}: exp {Exp}", session.Role?.Name, id,
            data.TextOf(data.Row("TaskConfig", id)!.Value, "_iName"), def.Exp);
        return 0;
    }

    private static Ownership.OwnerContext RequireTaskOwner(GameSession session)
    {
        var context = session.Owners?.Current ?? throw new InvalidOperationException("Task Core requires an active owner capability.");
        context.AssertRole(session.Role!.Id);
        return context;
    }

    /// <summary>Accepts every Auto task whose pre-tasks are delivered and whose condition holds; returns how many.</summary>
    private int AcceptReady(GameSession session)
    {
        int accepted = 0;
        var defs = Defs();
        foreach (var delivered in session.Tasks!.CompleteTaskNum.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList())
        {
            foreach (var child in _children!.GetValueOrDefault(delivered) ?? [])
            {
                var def = defs[child];
                if (def.Accept != ReceiveAuto || Active(session, child) || IsDelivered(session, child) || !Ready(session, def)) continue;
                Accept(session, child);
                accepted++;
            }
        }
        return accepted + AcceptGrowthTitle(session) + AcceptGrowthClass(session) + AcceptActivityTasks(session);
    }

    private bool Ready(GameSession session, TaskDef def) =>
        def.Pre.All(p => IsDelivered(session, p)) && (def.Condition == 0 || functions.ConditionMet(session, def.Condition))
        && AcceptActionsSupported(def);

    private void Accept(GameSession session, long id)
    {
        session.Tasks!.Infos[id] = NewInfo(id, Running);
        StartLoginTargets(session, id);
        // Same current-step selection as LC_TaskHelper.DealAcception (inferred for server host).
        // Extra slots are retained, never treated as sequential instructions.
        EvaluatePassive(session);
        var def = Defs()[id];
        var info = session.Tasks.Infos[id];
        int step = Enumerable.Range(0, def.Targets.Length).FirstOrDefault(i => info.CompleteNum[i] != Required(session, def.Targets[i]), -1);
        if (step >= 0 && step < def.AcceptActions.Length && ServerOpcode(def.AcceptActions[step]) == 621) session.InCutScene = true;
        log.LogInformation("{Name} accepts task {Task} {TaskName}", session.Role?.Name, id, data.TextOf(data.Row("TaskConfig", id)!.Value, "_iName"));
    }

    private TaskInfo NewInfo(long id, TaskState status)
    {
        var info = new TaskInfo { TaskId = id, Status = status, Follow = true, StartTime = clock.GetUtcNow().ToUnixTimeMilliseconds() };
        // One completeNum per target, zeros at accept (an empty list breaks the client's decode, §1.3); [0] without targets.
        int n = Math.Max(1, Defs()[id].Targets.Length);
        for (int i = 0; i < n; i++) info.CompleteNum.Add(0);
        if (status == Delivered)
            for (int i = 0; i < Defs()[id].Targets.Length; i++) info.CompleteNum[i] = Math.Max(0, Required(null, Defs()[id].Targets[i]));
        return info;
    }

    private bool Active(GameSession session, long id) =>
        session.Tasks!.Infos.TryGetValue(id, out var info) && info.Status is Running or Finished;

    /// <summary>Targets the server reads from the player's state rather than from events.</summary>
    private void EvaluatePassive(GameSession session)
    {
        foreach (var info in session.Tasks!.Infos.Values.Where(i => i.Status is Running or Finished))
        {
            var def = Defs()[info.TaskId];
            for (int i = 0; i < def.Targets.Length && i < info.CompleteNum.Count; i++)
            {
                var t = def.Targets[i];
                if (!Supported(t)) { info.CompleteNum[i] = 0; continue; }
                long value = PassiveValue(session, t);
                if (value < 0) continue;
                info.CompleteNum[i] = Math.Min(Required(session, t), value);
            }
        }
    }

    private bool Complete(GameSession session, TaskDef def, TaskInfo info)
    {
        if (def.Targets.Length == 0) return false;
        for (int i = 0; i < def.Targets.Length; i++)
            if (!Supported(def.Targets[i]) || Required(session, def.Targets[i]) <= 0
                || i >= info.CompleteNum.Count || info.CompleteNum[i] != Required(session, def.Targets[i])) return false;
        return true;
    }

    /// <summary>Persists non-reward progress. Deliveries use the atomic award boundary instead.</summary>
    private void Save(GameSession session)
    {
        if (session.Role is { } role && session.Tasks != null)
        {
            roles.SaveTasks(role.Id, session.Tasks.ToByteArray());
            roles.SaveTaskRuntime(role.Id, JsonSerializer.SerializeToUtf8Bytes(session.TaskRuntime));
        }
    }

    private Dictionary<long, TaskDef> Defs()
    {
        if (_defs != null) return _defs;
        var defs = new Dictionary<long, TaskDef>();
        foreach (var row in data.Table("TaskConfig").Values)
        {
            long id = GameData.Int(row, "_iTaskID");
            if (id == 0) continue;
            defs[id] = new TaskDef(id, (int)GameData.Int(row, "_iTaskType"), Longs(row, "_kPreTask"), GameData.Int(row, "_iCondition"),
                (int)First(row, "_kAcceptTask"), (int)First(row, "_kDeliverTask"), Targets(row), GameData.Int(row, "_iAward"),
                Longs(row, "_kEXP") is [1, var exp, ..] ? exp : 0,
                Longs(row, "_kAcceptTask"), Longs(row, "_kDeliverTask"), Actions(row, "_kAcceptAction"), Actions(row, "_kEXECAction"), Actions(row, "_kTrack"));
        }
        var children = new Dictionary<long, List<long>>();
        foreach (var def in defs.Values)
            foreach (var pre in def.Pre)
                (children.TryGetValue(pre, out var list) ? list : children[pre] = new()).Add(def.Id);
        _children = children;
        _defs = defs;
        log.LogInformation("Tasks: {Count} TaskConfig rows", defs.Count);
        return defs;
    }

    private static long[] Longs(JsonElement row, string field) =>
        row.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => (long)(GameData.Number(e) ?? 0)).ToArray() : [];

    private static long First(JsonElement row, string field) => Longs(row, field) is [var first, ..] ? first : 0;

    private static long[][] Targets(JsonElement row) =>
        row.TryGetProperty("_kProgressTarget", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0)
                .Select(t => t.EnumerateArray().Select(e => (long)(GameData.Number(e) ?? 0)).ToArray()).ToArray()
            : [];
}
