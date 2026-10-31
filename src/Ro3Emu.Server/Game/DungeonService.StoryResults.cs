using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    // 2026-10-31 原服錄製：僅套用已確認的六張任務地圖，不改團隊／競技結算。
    internal static bool CapturedStoryScene(long scene) => scene is 1022001 or 1051001 or 1000501
        or 1000201 or 1000301 or 1000401;

    private static DungeonResultNotify CapturedStoryResult(Attempt a, bool success)
    {
        var statistics = StatisticsOf(a);
        var info = new DungeonResultInfo
        {
            StatisticsId = a.Room.Id, DungeonId = (int)a.Plan.SceneEntry,
            StatisticsTime = statistics.StatisticsTime, DungeonStartTimestamp = a.StartMs,
            WinCamp = success ? 1 : 0,
        };
        info.SceneStatistics.Add(statistics.SceneStatistics);
        // 任務獎勵由既有交易及 5066／5027 同步，不另塞一份獎勵到結算通知。
        return new DungeonResultNotify { ResultInfo = info };
    }

    private Task StoryFunctionAsync(GameSession s, Attempt a, string function, params EventParams[] parameters) =>
        s.SendGameAsync(Event, new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)6,
            CliFuncname = function, Params = { parameters },
        });

    private async Task TigerBlackScreenAsync(Attempt a)
    {
        if (a.Plan.SceneEntry != 1022001 || a.Wave != 0) return;
        foreach (var s in LoadedMembers(a))
            await StoryFunctionAsync(s, a, "BlackScreenMs", new EventParams { IntParams = { 1000 } });
    }

    private void ScheduleCapturedStoryReturn(GameSession s, Attempt a)
    {
        var role = s.Role;
        var lease = s.Attachment;
        long generation = s.SceneGeneration;
        bool Current() => role != null && ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, lease)
            && lease?.Cancelled != true && ReferenceEquals(s.DungeonAttempt, a) && a.Settled && a.Paid.Contains(s)
            && s.InstanceId == a.Room.Id && s.SceneGeneration == generation && !s.SceneLoading && !a.Room.Closed;
        void Later(int ms, Func<Task> action) => _loop.ScheduleLegacy(Paced(ms),
            () => Current() ? action() : Task.CompletedTask);

        // 單次觀察的相對時序；不是所有難度的通用固定規則。
        if (a.Plan.SceneEntry == 1022001)
        {
            foreach (var (delay, chat) in new[] { (6000, 5300012), (9000, 5300013) })
                Later(delay, async () =>
                {
                    var parameters = new List<EventParams> { new() { IntParams = { chat } } };
                    for (int i = 0; i < 10; i++) parameters.Add(new EventParams { StrParams = { "" } });
                    await StoryFunctionAsync(s, a, "Shouting", [.. parameters]);
                });
        }
        int delayMs = a.Plan.SceneEntry switch { 1022001 => 12000, 1000201 => 1000, 1000301 => 3000, 1000501 => 200, _ => 5000 };
        Later(delayMs, async () =>
        {
            long next = a.Plan.SceneEntry switch { 1000201 => 1000301, 1000301 => 1000401, _ => 0 };
            if (next != 0)
            {
                // 下一段任務必須已由正常完成鏈接入。Create 會保留首次進副本的外部返回位置。
                if (StoryContinuationAllowed?.Invoke(s, next) != true
                    || _catalog.Resolve(new EnterDungeonReq { SceneEntry = next }) is not { } plan
                    || Admission(s, plan, continuation: true) != 0) return;
                await BeginAsync(plan, [s], role!.Id, 0);
            }
            else await _instances.LeaveAsync(s, recoverDeadOnReturn: true);
        });
    }
}
