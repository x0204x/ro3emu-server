using System.Text.Json;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const int BattleMountSlot = 2, MountedCondition = 2003;

    // 配對LC_MountHelper.GetCurRidingBattleMountId只看Battle slot；QuestMount或System不是替代品。
    internal static long CurrentBattleMount(GameSession source) => source.Mounts?.MountIds.TryGetValue(BattleMountSlot, out var mount) == true
        && mount.IsRiding ? mount.MountId : 0;

    // 保留原窄評估入口供既有騎乘布林測試；共用入場則另處理完整已知條件樹。
    internal static bool? EvaluateMountCondition(JsonElement node, long battleMount, int depth = 0) =>
        EvaluateKnownSkillCondition(node, values => values is [MountedCondition, var expected] && expected >= 0
            ? battleMount == expected : null, depth);
}
