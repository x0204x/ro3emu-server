using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    /// <summary>Preserve action slots, order and string parameters, including the empty sentinel (§1).</summary>
    private static string[][] Actions(JsonElement row, string field) =>
        row.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).ToArray()
                : Array.Empty<string>()).ToArray() : [];

    // 0 is a client/no-op slot; -1 is malformed or an unknown action definition.
    private int ServerOpcode(string[] action)
    {
        if (action.Length == 0 || action[0] == "") return 0;
        if (!int.TryParse(action[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int op)
            || _data.Row("ActionConfig", op) is not { } row) return -1;
        return GameData.Int(row, "_iEXECHost") is 1 or 2 ? op : 0;
    }

    private bool AcceptActionsSupported(TaskDef def) => def.AcceptActions.All(a => ServerOpcode(a) is 0 or 621);

    /// <summary>
    /// Host-1 effects. 20's [item,count] is an inferred inventory cost, committed with delivery.
    /// 621 tracks cutscene state; no invented packet, invulnerability or movement rule. The resource semantics of
    /// 54/412 stage mount ride/recycle state with the same award transaction; other unsupported actions hold delivery.
    /// 115 MarkGuildActivityJoin [activity] marks the role as having joined a GuildActivityConfig activity (1601000, the
    /// guild caravan quest: 3000). The client never reads such a mark, so it is reported through
    /// <see cref="GuildActivityJoined"/> after the delivery commits, for the activity's own bookkeeping (inferred).
    /// This method only builds a plan, so a refusal cannot mutate action state or consume inventory.
    /// </summary>
    private bool TryDeliveryActions(GameSession session, Ownership.OwnerContext context, TaskDef def,
        out List<(int Entry, int Count)> costs, out bool cutScene, out int? questMount, out List<long> guildActivities)
    {
        context.AssertRole(session.Role!.Id);
        costs = new();
        cutScene = false;
        questMount = null;
        guildActivities = [];
        var actions = def.ExecActions.ToList();
        foreach (var action in actions)
        {
            int op = ServerOpcode(action);
            switch (op)
            {
                case 0: break;
                case 20 when action.Length == 3
                    && int.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int item)
                    && int.TryParse(action[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                    && count > 0 && ItemExists(item):
                    costs.Add((item, count));
                    break;
                case 412 when action.Length == 2
                    && int.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ride)
                    && _mounts.IsConfigured(ride):
                    questMount = ride;
                    break;
                case 54 when action.Length == 3
                    && long.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long originTask)
                    && _data.Row("TaskConfig", originTask) != null
                    && int.TryParse(action[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int recycle)
                    && _mounts.IsConfigured(recycle):
                    if ((questMount ?? session.MountState.QuestMount) == recycle) questMount = 0;
                    break;
                case 621 when action.Length == 1: cutScene = true; break;
                case MarkGuildActivityJoin when action.Length == 2
                    && long.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long activity)
                    && GuildActivityConfig.Get(_data, activity) != null:
                    guildActivities.Add(activity);
                    break;
                default:
                    LogHeldOnce(session, def.Id, $"server action {string.Join(',', action)}: resource semantics not implemented");
                    return false;
            }
        }
        // The dialogue that completes a 104001 target ends in a row whose action 17 reports that dialog id; its other
        // actions run with it. In the whole DialogueConfig only two such rows carry a server-host action, both
        // "20 ConsumeItem 1253015 x1" (the boarding pass: 1010320002 for 10103200, 1040202 for the older 103xxx route).
        // The pass has no source on the connected main story: its only award is AwardConfig 10030034 (MultiverseGroup
        // 30034), given by TaskConfig 103300 of the older route, which 10101010's chain never reaches. Requiring it held
        // 10103200 for every player (story walk); the client shows the pass as confirmed whatever the bag holds
        // (DialogueConfig 1010320001 rows 1-2, actions 42/43 only show the ticket image). So the pass is taken when the
        // player has it and never required (inferred).
        foreach (var target in def.Targets.Where(t => t is [TDialog, _, _, _]))
            foreach (var row in DialogueRows().Where(r => r.Actions.Any(a => a is ["17", var d] && d == target[3].ToString(CultureInfo.InvariantCulture))))
                foreach (var action in row.Actions.Where(a => ServerOpcode(a) == 20 && a.Length == 3))
                    if (int.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int item)
                        && int.TryParse(action[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                        && count > 0 && ItemExists(item) && _character.CountOf(session, item) >= count)
                        costs.Add((item, count));
        return true;
    }

    /// <summary>ActionConfig 115 MarkGuildActivityJoin (_iEXECHost 1).</summary>
    internal const int MarkGuildActivityJoin = 115;

    /// <summary>A delivered task's 115 MarkGuildActivityJoin: the role and the GuildActivityConfig id.</summary>
    public List<Action<GameSession, long>> GuildActivityJoined { get; } = [];

    private readonly HashSet<(long Role, long Task, string Reason)> _heldLogged = [];

    /// <summary>
    /// A held delivery is retried by the client every few seconds while the task stays complete: warn once per role,
    /// task and reason, and log the repeats at debug level.
    /// </summary>
    private void LogHeldOnce(GameSession session, long task, string reason)
    {
        long role = session.Role?.Id ?? 0;
        bool first;
        lock (_heldLogged) first = _heldLogged.Add((role, task, reason));
        if (first) LogHeld(task, role, reason);
        else LogHeldAgain(task, role, reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task {Task} held for {Role}: {Reason}")]
    private partial void LogHeld(long task, long role, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Task {Task} still held for {Role}: {Reason}")]
    private partial void LogHeldAgain(long task, long role, string reason);

    private List<(long Id, string[][] Actions)>? _dialogueRows;

    /// <summary>Every DialogueConfig row with its _iEXECAction slots (a dialogue id holds an array of rows).</summary>
    private List<(long Id, string[][] Actions)> DialogueRows() => _dialogueRows ??= _data.Table("DialogueConfig")
        .SelectMany(kv => kv.Value.ValueKind == JsonValueKind.Array ? kv.Value.EnumerateArray() : Enumerable.Repeat(kv.Value, 1))
        .Select(r => ((long)GameData.Int(r, "_iID"), Actions(r, "_iEXECAction"))).Where(r => r.Item2.Length > 0).ToList();

    /// <summary>32709 exits state only. It never completes a plot objective.</summary>
    public async Task ExitCutSceneAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        session.InCutScene = false;
        Save(session);
    }

    /// <summary>31216 runs only configured server-host tracking effects for an owned active task (§1).</summary>
    public async Task TrackAsync(GameSession session, TaskTrackReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        if (session.Tasks == null || !Active(session, req.TaskId) || !Defs().TryGetValue(req.TaskId, out var def)) return;
        foreach (var action in def.Track)
            if (ServerOpcode(action) == 621 && action.Length == 1) session.InCutScene = true;
        Save(session);
    }

    /// <summary>
    /// NPC modes carry [mode,npc,map,point,dialog]. Static UUIDs must match the configured NPC and map. A local
    /// quest NPC has a client-only UUID, so verify the configured point and proximity instead (inferred).
    /// Use the scene service's 12-unit tolerance; an unrelated static UUID is always rejected.
    /// </summary>
    private int ValidateNpc(GameSession session, long[] args, long uid)
    {
        if (args.Length < 5 || session.SceneEntry != args[2] || session.SceneLoading) return ErrorCodes.TaskRequirements;
        float x, z;
        if (_worldData.Npc(uid) is { } npc)
        {
            if (npc.NpcId != args[1] || npc.SceneEntry != args[2]) return ErrorCodes.TaskRequirements;
            (x, z) = (npc.X, npc.Z);
        }
        else if (_worldData.Map(args[2])?.Points.TryGetValue(args[3], out var point) == true)
            (x, z) = (point.X, point.Z);
        else return ErrorCodes.TaskRequirements;
        return NearPoint(session, x, z) ? 0 : ErrorCodes.TaskRequirements;
    }

    // Same tolerance as SceneService (inferred); local reports cannot prove a played dialogue animation.
    private static bool NearPoint(GameSession session, float x, float z)
    {
        float dx = session.Position.X - x, dz = session.Position.Z - z;
        return !session.SceneLoading && dx * dx + dz * dz <= 144;
    }
}
