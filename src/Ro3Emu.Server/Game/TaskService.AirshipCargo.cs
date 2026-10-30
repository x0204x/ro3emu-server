using System.Text.Json;
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    private const long AirshipCargoTask = 10103150;

    // G 的 83651 是空包；前三關內無法區分合法下一關與重送，不臆造關卡證明或 nonce。
    public async Task GoodsStackAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        int result = ErrorCodes.TaskRequirements;
        try
        {
            if (BeforeProgress is { } before) await before(session);
            result = await CompleteAirshipCargoCoreAsync(session);
        }
        catch (Exception error)
        {
            _log.LogError(error, "飛空艇貨物進度或獎勵仍待恢復，角色 {Role}", session.Role?.Id);
            result = ErrorCodes.Unknown;
        }
        if (session.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired)
        {
            session.CancelPendingCharacterIntents();
            session.AbortForRecovery();
            return;
        }
        await session.SendGameAsync(GoodsStack, new TaskGoodsStackResp { Result = result });
    }

    private async Task<int> CompleteAirshipCargoCoreAsync(GameSession session)
    {
        if (session.Role == null || session.Tasks == null
            || !session.Tasks.Infos.TryGetValue(AirshipCargoTask, out var info)
            || info.Status is not (Running or Finished) || IsDelivered(session, AirshipCargoTask)
            || !Defs().TryGetValue(AirshipCargoTask, out var def) || !ContextMapMatches(session, def))
            return ErrorCodes.TaskRequirements;
        string source = $"airship-cargo/{session.Role.Id}/{info.TaskId}/{info.StartTime}/" +
            $"{session.Tasks.CompleteTaskNum.GetValueOrDefault(info.TaskId)}/";
        bool retry = HasAirshipCargoFollowup(session.Role.Id, source);
        if (!retry)
        {
            int goal = Array.FindIndex(def.Targets, target => target is [TCargo, > 0]);
            if (info.Status != Running || goal < 0 || goal >= info.CompleteNum.Count
                || info.CompleteNum[goal] >= Required(session, def.Targets[goal]))
                return ErrorCodes.TaskRequirements;
            int result = await PersistAirshipCargoGoalAsync(session, def, goal, source);
            if (result != 0) return result;
        }
        // 前兩關的目標提交後若尚未完成後續同步，重試只續做原任務的債務。
        await SettleAsync(session);
        if (session.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired)
            throw new InvalidOperationException("飛空艇貨物提交後需重新載入角色。");
        return HasAirshipCargoFollowup(session.Role.Id, source) ? ErrorCodes.Unknown : 0;
    }

    private async Task<int> PersistAirshipCargoGoalAsync(GameSession session, TaskDef def, int goal, string source)
    {
        long role = session.Role!.Id;
        RequireTaskOwner(session).AssertRole(role);
        var next = session.Tasks!.Clone();
        var info = next.Infos[AirshipCargoTask];
        info.CompleteNum[goal]++;
        if (Complete(session, def, info))
        {
            info.Status = Finished;
            // G 重開視窗取 CompleteNum+1；第三關與發獎須同筆提交，不能暴露不存在的第四關。
            return await DeliverCoreAsync(session, RequireTaskOwner(session), AirshipCargoTask, preparedTasks: next);
        }
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(role, "tasks", next.ToByteArray());
        transaction.TaskFollowup(role, source + $"{goal}/{info.CompleteNum[goal]}");
        // 目標與發獎債務同筆提交，成功後才替換記憶體，避免寫入失敗吃掉一次闖關。
        await _roles.CommitAsync(transaction, "task/airship-cargo", _ => session.Tasks = next);
        return 0;
    }

    private bool HasAirshipCargoFollowup(long role, string prefix) =>
        _roles.LoadTaskFollowup(role) is { Length: > 2 } bytes
        && (JsonSerializer.Deserialize<HashSet<string>>(bytes) ?? [])
            .Any(source => source.StartsWith(prefix, StringComparison.Ordinal));
}
