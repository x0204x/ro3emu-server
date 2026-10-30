using Ro3Emu.Server.Data.Config;
namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    /// <summary>僅酒館每日任務接收已提交租約的秒數；不影響同代碼的公會／幽暗領域目標。</summary>
    internal CharacterService.FirstCardSightPlan? StageTavernSeconds(GameSession session, string source, long seconds)
        => seconds <= 0 ? null : StageEventTarget(session, source,
            target => target is [TBarroomActTime, > 0] ? seconds : 0,
            def => def.Id == PublicParamConfig.BarroomTaskId(_data));
}
