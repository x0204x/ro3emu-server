using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    private bool ValidPreparedDelivery(GameSession session, TaskDef def, DBTaskInfo prepared)
    {
        var current = session.Tasks;
        if (current == null || !current.Infos.TryGetValue(def.Id, out var original)
            || !prepared.Infos.TryGetValue(def.Id, out var candidate)
            || candidate.TaskId != def.Id || candidate.StartTime != original.StartTime || candidate.Status != Finished
            || prepared.CompleteTaskNum.GetValueOrDefault(def.Id) != current.CompleteTaskNum.GetValueOrDefault(def.Id)
            || !Complete(session, def, candidate)) return false;
        // 可準備的差異僅限這個目標實例，不能順便改其他任務或歷史完成次數。
        var expected = current.Clone();
        expected.Infos[def.Id] = candidate.Clone();
        return expected.Equals(prepared);
    }
}
