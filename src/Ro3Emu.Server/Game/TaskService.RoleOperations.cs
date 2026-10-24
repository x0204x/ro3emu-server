using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;
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
    /// 31217: Dodo's Request Wheel (UI_Sys_Main_EntrustLotteryPanelCtrl) spends PublicParamConfig.RunRingTaskTurntableItem
    /// (the Dodo Pink Gem) once or ten times. Its eight slots are the _kPreview of the RunRingTaskTurnReward award
    /// (MultiverseGroupConfig 84011), and each turn rolls that award's drop pool, whose one group lists the same eight
    /// tuples in the same order. Version is the client's in-flight marker (always 1), not a once-only claim key.
    /// The panel stays locked (no close, no Esc) until LC_DAL_Reward sees a GetRewardNotify whose rewardType holds
    /// ManifestTypeRunRing; its rewardIndexes are the 0-based slots the wheel stops on (_onRingTaskRewardReceived). The
    /// 31217 reply only refreshes the view. The old LuckyTrunConfig draw paid Zeny the wheel never shows, under a plain
    /// reward notice, so the wheel never stopped (#bug-reports "Dodo Pink Gem hard lock").
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
        var param = _data.Table("PublicParamConfig");
        int ticket = param.TryGetValue("RunRingTaskTurntableItem", out var configuredTicket)
            && GameData.Number(configuredTicket) is { } ticketNumber and > 0 and <= int.MaxValue
            ? (int)ticketNumber : 0;
        long reward = param.TryGetValue("RunRingTaskTurnReward", out var configuredReward)
            && GameData.Number(configuredReward) is { } rewardNumber and > 0 ? (long)rewardNumber : 0;
        var slots = reward > 0 ? WheelSlots(reward) : [];
        if (ticket <= 0 || slots.Count == 0)
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.FeatureUnavailable);
            return;
        }
        var grants = new List<AwardGrant>();
        var indexes = new List<int>();
        try
        {
            for (int draw = 0; draw < turns; draw++)
            {
                var resolved = _awards.Resolve(session, reward).Grants;
                if (resolved.Count != 1) throw new AwardResolutionException("Wheel draw must name one displayed prize");
                var grant = resolved[0];
                int slot = slots.FindIndex(s => s.Entry == grant.Entry && s.Count == grant.Count);
                if (slot < 0) throw new AwardResolutionException("Wheel prize is absent from its preview");
                indexes.Add(slot);
                grants.Add(grant);
            }
        }
        catch (AwardResolutionException)
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.FeatureUnavailable);
            return;
        }
        if (!await _character.ApplyAwardAsync(session, new AwardPlan(reward, 0, grants), costs: [(ticket, turns)],
                manifestType: ManifestTypeRunRing, rewardIndexes: indexes))
        {
            await TaskErrorAsync(session, TaskLuckyTurn, req.Type, ErrorCodes.ItemsShort);
            return;
        }
        await session.SendGameAsync(TaskLuckyTurn, new LuckyTurnResp { Result = 0, Version = req.Version });
    }

    /// <summary>PBManifestType.ManifestTypeRunRing (MG_Common.proto), the rewardType LC_DAL_Reward routes to the wheel.</summary>
    internal const int ManifestTypeRunRing = 18;

    /// <summary>The wheel's slots in order: the award's AwardConfig _kPreview, as GetSeasonAwardPreview lists them (the
    /// default value, the one <see cref="AwardService.Resolve"/> rolls here).</summary>
    private List<(int Entry, int Count)> WheelSlots(long group)
    {
        if (MultiverseGroupConfig.Get(_data, group) is not { } mv) return [];
        var rows = AwardConfig.Get(_data, mv.ValueDefault);
        if (rows.Count != 1) return [];
        var preview = rows[0].Preview;
        if (preview.Length != 8 || preview.Any(t => t.Length < 2 || t[0] is <= 0 or > int.MaxValue || t[1] is <= 0 or > int.MaxValue)) return [];
        return [.. preview.Select(t => ((int)t[0], (int)t[1]))];
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
