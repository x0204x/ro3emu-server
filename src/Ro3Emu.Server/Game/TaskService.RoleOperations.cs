using System.Globalization;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    /// <summary>31204: only TaskConfig._iIfReset=1 may reset an active task's positional progress.</summary>
    public async Task ResetAsync(GameSession session, TaskResetReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        int result = session.Tasks == null ? ErrorCodes.Unknown
            : !Defs().ContainsKey(req.Id) ? ErrorCodes.TaskNoConfig
            : !session.Tasks.Infos.TryGetValue(req.Id, out var info) ? ErrorCodes.TaskMissing
            : info.Status is not (Running or Finished) ? ErrorCodes.TaskStatus
            : _data.Row("TaskConfig", req.Id) is not { } row || GameData.Int(row, "_iIfReset") != 1
                ? ErrorCodes.TaskNoGiveUp : 0;
        if (result != 0)
        {
            // LC_DAL_Task._dealTaskReset dispatches TaskResetEvent without inspecting result.
            await TaskErrorAsync(session, TaskReset, req.Id, result);
            return;
        }
        session.Tasks!.Infos[req.Id] = NewInfo(req.Id, Running);
        await FlushAsync(session);
        await session.SendGameAsync(TaskReset, new TaskResetResp { Result = 0, Id = req.Id });
    }

    /// <summary>
    /// 31212: acknowledge only a task chat that was configured as a delivered client action 36.
    /// Recording the viewed chat under its id is (inferred); it never progresses or pays a quest.
    /// </summary>
    public async Task NpcChatAsync(GameSession session, TaskNpcChatReq req)
    {
        using var gate = await LockTasksAsync(session);
        bool allowed = req.ChatId > 0 && session.Tasks != null && session.Tasks.Infos.Values.Any(info =>
            info.Status == Delivered && Defs().TryGetValue(info.TaskId, out var def)
            && def.ExecActions.Any(action => action.Length >= 2 && action[0] == "36"
                && int.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id == req.ChatId));
        if (allowed && !session.Tasks!.NpcChatIds.ContainsKey(req.ChatId))
        {
            session.Tasks.NpcChatIds[req.ChatId] = 1;
            await FlushAsync(session);
        }
        await session.SendGameAsync(TaskNpcChat, new TaskNpcChatResp
        { Result = allowed ? 0 : ErrorCodes.TaskRequirements, ChatId = req.ChatId });
    }

    /// <summary>
    /// 31213 is sent by the login download widget only when its local download reaches 100% and task 100 is
    /// Running. The request contains no milestone proof, so that client report is the bounded trust boundary.
    /// Only task 100's configured 106701 objective is advanced; UI reward delivery remains owned by 31205.
    /// </summary>
    public async Task FinishPydAsync(GameSession session, FinishPYDTaskReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        const long taskId = 100;
        int result = session.Tasks == null || session.Progress == null ? ErrorCodes.Unknown
            : !Defs().TryGetValue(taskId, out var def) || def.Targets.Length != 1
                || def.Targets[0] is not [TPydDownload, 1] ? ErrorCodes.TaskNoConfig
            : !session.Tasks.Infos.TryGetValue(taskId, out var info) ? ErrorCodes.TaskMissing
            : info.Status != Running || info.CompleteNum.Count != 1 || info.CompleteNum[0] != 0
                ? ErrorCodes.TaskStatus : 0;
        if (result == 0)
        {
            await ProgressCoreAsync(session, TPydDownload,
                t => t is [TPydDownload, 1] ? 1 : 0, d => d.Id == taskId);
            if (session.Tasks!.Infos[taskId].Status != Finished) result = ErrorCodes.TaskRequirements;
        }
        await session.SendGameAsync(TaskFinishPyd, new FinishPYDTaskResp { Result = result });
    }

    /// <summary>31215 accepts only a branch already advertised as pending in authoritative 5027.</summary>
    public async Task SelectBranchAsync(GameSession session, SelectTaskQuestBranchReq req)
    {
        using var gate = await LockTasksAsync(session);
        bool pending = req.BranchId > 0 && session.Tasks?.WaitSelectBranch.ContainsKey(req.BranchId) == true
            && session.Tasks.SelectBranch.GetValueOrDefault(req.BranchId) != true;
        if (!pending)
        {
            // Lua OnTaskQuestBranch blindly sets selectBranch[resp.req.branchId], even if a result existed.
            await TaskErrorAsync(session, TaskSelectQuestBranch, req.BranchId, ErrorCodes.TaskRequirements);
            return;
        }
        session.Tasks!.SelectBranch[req.BranchId] = true;
        session.Tasks.WaitSelectBranch.Remove(req.BranchId);
        Save(session);
        await session.SendGameAsync(TaskSelectQuestBranch, new SelectTaskQuestBranchResp { Req = req });
        await SyncTasksAsync(session);
    }

    /// <summary>
    /// 31217: the turntable consumes PublicParamConfig.RunRingTaskTurntableItem once or ten times.
    /// Version is the client's in-flight marker (always 1), not a once-only claim key. A repeated
    /// request is another paid draw. Every selected MultiverseGroupConfig award and all tickets are
    /// resolved before CharacterService atomically spends/grants them (inferred draw transaction).
    /// </summary>
    public async Task LuckyTurnAsync(GameSession session, LuckyTurnReq req)
    {
        using var gate = await LockTasksAsync(session);
        int turns = req.Type switch { 0 => 1, 1 => 10, _ => 0 };
        if (turns == 0 || req.Version != 1)
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.Parameter);
            return;
        }
        int ticket = _data.Table("PublicParamConfig").TryGetValue("RunRingTaskTurntableItem", out var configuredTicket)
            && GameData.Number(configuredTicket) is { } ticketNumber and > 0 and <= int.MaxValue
            ? (int)ticketNumber : 0;
        if (ticket <= 0)
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.FeatureUnavailable);
            return;
        }
        var rows = _data.Table("LuckyTrunConfig").Values.OrderBy(r => GameData.Int(r, "_iID")).ToArray();
        if (rows.Length != 8 || rows.Any(r => GameData.Int(r, "_iweight") <= 0 || GameData.Int(r, "_iAward") <= 0))
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.FeatureUnavailable);
            return;
        }
        long totalWeight = rows.Sum(r => GameData.Int(r, "_iweight"));
        var grants = new List<AwardGrant>();
        try
        {
            for (int draw = 0; draw < turns; draw++)
            {
                long choice = Random.Shared.NextInt64(totalWeight);
                foreach (var row in rows)
                {
                    choice -= GameData.Int(row, "_iweight");
                    if (choice >= 0) continue;
                    grants.AddRange(_awards.Resolve(session, GameData.Int(row, "_iAward")).Grants);
                    break;
                }
            }
        }
        catch (AwardResolutionException)
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.FeatureUnavailable);
            return;
        }
        if (!await _character.ApplyAwardAsync(session, new AwardPlan(0, 0, grants), costs: [(ticket, turns)]))
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.ItemsShort);
            return;
        }
        await session.SendGameAsync(TaskLuckyTurn, new LuckyTurnResp { Result = 0, Version = req.Version });
    }

    /// <summary>30016 resolves the destination from a running quest's own 142/143 track action.</summary>
    public async Task TrackTeleportAsync(GameSession session, TaskTrackTeleportReq req)
    {
        using var gate = await LockTasksAsync(session);
        if (session.Tasks == null || !session.Tasks.Infos.TryGetValue(req.TaskId, out var info)
            || info.Status != Running || !Defs().TryGetValue(req.TaskId, out var def))
        {
            await TaskErrorAsync(session, TaskTrackTeleport, req.TaskId, ErrorCodes.TaskRequirements);
            return;
        }
        // Track is positional. The first incomplete target selects the client action (LC_TaskHelper.DealFollow).
        int step = Enumerable.Range(0, def.Targets.Length)
            .FirstOrDefault(i => i < info.CompleteNum.Count && info.CompleteNum[i] != Required(session, def.Targets[i]), -1);
        if (step < 0 || step >= def.Track.Length || def.Track[step] is not { Length: >= 3 } action
            || action[0] is not ("142" or "143")
            || !long.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sceneId)
            || !long.TryParse(action[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long pointId)
            || pointId <= 0 || _worldData.Map(sceneId)?.Points.ContainsKey(pointId) != true
            || !await _scene.TaskTrackTeleportAsync(session, sceneId, pointId))
            await TaskErrorAsync(session, TaskTrackTeleport, req.TaskId, ErrorCodes.TaskRequirements);
    }

    private static Task TaskErrorAsync(GameSession session, uint messageId, long parameter, int error) =>
        session.SendGameAsync(ApiError, new APIErrorMsg
        {
            OriginMsgId = messageId,
            ErrorCode = error,
            ErrorParams = { new ErrorParam { ParamType = (ErrorParamType)7,
                ParamValue = { parameter.ToString(CultureInfo.InvariantCulture) } } },
        });
}
