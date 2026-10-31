using System.Globalization;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    // G ActionConfig[141] JumpMapArea；LC_InteractActions 將五個參數原樣送往 30131。
    private const long JumpMapAreaAction = 141;
    private const long EnterStoryDungeonAction = 3000;

    internal bool StoryDungeonContinuationAllowed(GameSession session, long scene) =>
        session.Tasks?.Infos.Values.Any(info => (info.Status is Running or Finished)
            && !IsDelivered(session, info.TaskId) && Defs().TryGetValue(info.TaskId, out var def)
            && def.Targets.Any(target => target is [TDungeonPass, var dungeon, > 0] && dungeon == scene)) == true;

    /// <summary>
    /// G 10103210 接受動作 3000 只在客戶端送場景 ID；由既有任務目標與動作資料綁定來源。
    /// 此門檻是 (inferred) 的服務端相容性查核，不表示取得原服 admission 實作。
    /// </summary>
    internal bool StoryDungeonEntryAllowed(GameSession session, long scene) =>
        session.Tasks?.Infos.Values.Any(info => (info.Status is Running or Finished)
            && !IsDelivered(session, info.TaskId) && Defs().TryGetValue(info.TaskId, out var def)
            && def.Targets.Any(target => target is [TDungeonPass, var dungeon, > 0] && dungeon == scene)
            && def.AcceptActions.Any(action => action.Length >= 2
                && long.TryParse(action[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long op)
                && op == EnterStoryDungeonAction
                && long.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long destination)
                && destination == scene)) == true;

    /// <summary>
    /// TaskDelivery 傳送必須來自已交付任務的完整動作，而非任意有效地圖／點位。
    /// 服務端再驗來源是 (inferred) 相容性政策；不能由此證明客戶端真的播放完動畫。
    /// 其他來源類型保留其原路徑，不在此猜測對話或追蹤傳送的規則。
    /// </summary>
    private int ValidateTaskDeliveryJump(GameSession session, ClientCustomJumpMapReq req)
    {
        if (req.CustomType != EClientCustomJumpMapType.TaskDelivery) return 0;
        if (session.SceneLoading) return ErrorCodes.SwitchingScenes;
        if (session.Tasks == null || !IsDelivered(session, req.TypeParam1)
            || !Defs().TryGetValue(req.TypeParam1, out var def))
            return ErrorCodes.TaskRequirements;
        return def.ExecActions.Any(action => MatchesDeliveryJump(action, req)) ? 0 : ErrorCodes.TaskRequirements;
    }

    private static bool MatchesDeliveryJump(string[] action, ClientCustomJumpMapReq req)
    {
        long[] expected = [JumpMapAreaAction, req.SceneId, req.PointId, (long)req.CustomType, req.TypeParam1, req.TypeParam2];
        return action.Length == expected.Length && Enumerable.Range(0, expected.Length).All(i =>
            long.TryParse(action[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            && value == expected[i]);
    }
}
