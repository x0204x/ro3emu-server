using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const long EquippedSubtypeCondition = 2001, ActivePendantTypeCondition = 2006, EquipmentItemType = 5;
    // 配對 LC_InteractCondition：2001 讀本機主角的 EquipGearDataMap，不讀傳入 actor。
    // 2006 雖名為 HasPendantByConfigId，實際比對的是執行中附件的 GetPendantType。
    internal bool? EvaluateEquipmentCastCondition(GameSession source, long[] values, bool playerActor)
    {
        if (values.Length != 2 || values[1] < 0) return null;
        if (values[0] == EquippedSubtypeCondition)
            return source.Items.Any(item => item.Pack == 2 && item.Grid is >= 1 and <= 8
                && ItemConfig.Get(_data, item.Entry) is { } row
                && row.Type == EquipmentItemType && row.SubType == values[1]);
        if (values[0] == ActivePendantTypeCondition && playerActor)
            return source.CareerPendants.Any(id => PendantConfig.Get(_data, id) is { } row && row.Type == values[1]);
        // 受控 NPC／技能物件的附件需讀該 actor 的執行中實例，不能借用玩家目前租用欄位。
        return null;
    }
}
