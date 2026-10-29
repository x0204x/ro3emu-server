using Google.Protobuf;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    // 通關當下凍結任務實例，重登時不得把已贏得的事件套用到後來才接受的任務。
    internal DungeonTaskClear CaptureDungeonClear(GameSession session, DungeonRewardObligation reward)
    {
        var targets = new List<DungeonTaskClearTarget>();
        session.Owners?.Current?.AssertRole(reward.RoleId);
        if (reward.Success && session.Tasks != null)
            foreach (var info in session.Tasks.Infos.Values.Where(i => i.Status == Running))
            {
                if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
                for (int goal = 0; goal < def.Targets.Length && goal < info.CompleteNum.Count; goal++)
                {
                    var target = def.Targets[goal];
                    if (target is not [TDungeonPass, var scene, _] || scene != reward.SceneEntry) continue;
                    long required = Required(session, target);
                    if (required <= 0 || info.CompleteNum[goal] >= required) continue;
                    targets.Add(new(info.TaskId, info.StartTime,
                        session.Tasks.CompleteTaskNum.GetValueOrDefault(info.TaskId), goal, [.. target], required));
                }
            }
        return new(reward.Key, reward.RoleId, reward.RoomId, reward.SceneEntry, targets);
    }

    internal async Task ApplyDungeonClearAsync(GameSession session, DungeonTaskClear clear)
    {
        using var gate = await LockTasksAsync(session);
        RequireTaskOwner(session).AssertRole(clear.RoleId);
        if (session.Role?.Id != clear.RoleId || session.Tasks == null && clear.Targets.Count > 0)
            throw new InvalidOperationException("副本任務恢復缺少原角色或任務資料。");
        if (BeforeProgress is { } before) await before(session);
        var next = session.Tasks?.Clone();
        bool changed = false;
        foreach (var target in clear.Targets)
        {
            if (next == null || !next.Infos.TryGetValue(target.TaskId, out var info)
                || info.Status is not (Running or Finished) || info.StartTime != target.StartTime
                || next.CompleteTaskNum.GetValueOrDefault(target.TaskId) != target.CompleteCount)
                continue; // 原實例已離開、完成或換輪；仍消費這筆來源，不套用到新實例。
            if (!Defs().TryGetValue(target.TaskId, out var def) || target.Goal >= def.Targets.Length
                || target.Goal >= info.CompleteNum.Count || !def.Targets[target.Goal].SequenceEqual(target.Target)
                || Required(session, def.Targets[target.Goal]) != target.Required)
                throw new InvalidDataException($"副本任務 {target.TaskId} 的原目標已變更，需人工核對。");
            long old = Math.Clamp(info.CompleteNum[target.Goal], 0, target.Required);
            long value = old < target.Required ? old + 1 : old;
            changed |= value != info.CompleteNum[target.Goal];
            info.CompleteNum[target.Goal] = value;
        }
        var transaction = _roles.NewTransaction();
        if (changed)
        {
            transaction.RoleBlob(clear.RoleId, "tasks", next!.ToByteArray());
            transaction.TaskFollowup(clear.RoleId, "dungeon-clear/" + clear.SourceKey);
        }
        // 任務目標、後續發獎債務與來源確認同筆提交；任一寫入失敗都不會重複計數。
        transaction.Add(new DungeonTaskClearAcknowledgePlan(clear.SourceKey, clear.RoleId));
        await _roles.CommitAsync(transaction, "task/dungeon-clear", _ =>
        {
            if (next != null) session.Tasks = next;
        });
        await SettleAsync(session);
        if (HasDungeonClearFollowup(clear.RoleId))
            throw new InvalidOperationException("副本任務後續獎勵仍待恢復，暫不發布通關結果。");
    }

    internal async Task ResumeDungeonClearFollowupAsync(GameSession session)
    {
        if (session.Role == null || !HasDungeonClearFollowup(session.Role.Id)) return;
        await RefreshAsync(session);
        if (HasDungeonClearFollowup(session.Role.Id))
            throw new InvalidOperationException("副本任務後續獎勵仍待恢復。");
    }

    private bool HasDungeonClearFollowup(long role) => _roles.LoadTaskFollowup(role) is { Length: > 2 } bytes
        && (JsonSerializer.Deserialize<HashSet<string>>(bytes) ?? [])
            .Any(source => source.StartsWith("dungeon-clear/", StringComparison.Ordinal));
}
