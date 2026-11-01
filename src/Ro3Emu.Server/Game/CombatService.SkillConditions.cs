using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const int ConditionAnd = 1, ConditionOr = 2, ConditionData = 3;
    private bool KnownSkillConditionsMet(GameSession source, JsonElement row, WorldUnit? actor, bool playerActor)
    {
        long id = new SkillConfig(row).SkillCondition;
        if (id <= 0 || LCConditionList.Get(_data, id) is not { } condition) return true;
        // 原1004/2000讀實際施放actor；未知的劇情／受控actor不能借用主角Buff。
        IEnumerable<ActiveBuff>? buffs = playerActor ? source.Buffs : actor != null ? _buffs.UnitBuffs(actor) : null;
        bool? Leaf(long[] values)
        {
            if (values is [MountedCondition, var mount] && mount >= 0)
                return CurrentBattleMount(source) == mount;
            return EvaluateBuffCastCondition(source, values, buffs)
                ?? EvaluateEquipmentCastCondition(source, values, playerActor)
                ?? EvaluateFlyMountCastCondition(source, values);
        }
        return EvaluateKnownSkillCondition(condition.Row, Leaf) != false;
    }

    // 配對LC_ConditionManager：只對整棵已理解的常數條件作結論；不把未知OR/AND分支當false。
    // 缺資料／未知actor保留舊未處理行為；這不是宣稱未知條件已驗證。
    internal static bool? EvaluateKnownSkillCondition(JsonElement node, Func<long[], bool?> leaf, int depth = 0)
    {
        if (depth > 32 || node.ValueKind != JsonValueKind.Object) return null;
        long type = new LCConditionList(node).Type;
        if (type == ConditionData)
        {
            if (!node.TryGetProperty("m_kData", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0) return null;
            if (node.TryGetProperty("m_kDynamicData", out var dynamicData) && (dynamicData.ValueKind switch
            { JsonValueKind.Object => dynamicData.EnumerateObject().Any(), JsonValueKind.Array => dynamicData.GetArrayLength() > 0, _ => true })) return null;
            var values = new long[data.GetArrayLength()];
            for (int i = 0; i < values.Length; i++)
                if (data[i].ValueKind != JsonValueKind.Number || !data[i].TryGetInt64(out values[i])) return null;
            if (leaf(values) is not { } result) return null;
            if (node.TryGetProperty("m_bInverse", out var inverse))
            {
                if (inverse.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                if (inverse.GetBoolean()) result = !result;
            }
            return result;
        }
        if (type is not (ConditionAnd or ConditionOr) || node.TryGetProperty("m_bInverse", out _) || node.TryGetProperty("m_kDynamicData", out _)
            || !node.TryGetProperty("m_kSubConditionList", out var children)
            || children.ValueKind != JsonValueKind.Array || children.GetArrayLength() == 0) return null;
        bool aggregate = type == ConditionAnd;
        foreach (var child in children.EnumerateArray())
        {
            if (EvaluateKnownSkillCondition(child, leaf, depth + 1) is not { } value) return null;
            aggregate = type == ConditionAnd ? aggregate && value : aggregate || value;
        }
        return aggregate;
    }
}
