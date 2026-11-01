using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

internal static partial class SkillExtraFormulaRules
{
    private const string HitTargetCountField = "_ro3FormulaHitTargetCount";

    /// <summary>BuffConfig1245301/1246301：每命中一名敵人提高5%／4%，上限25%／20%；
    /// SkillConfig 的 AXadd_HitCount_ExtraDmg 對應 [500,2500]／[400,2000]。
    /// 候選以本次共同篩選且套目標上限後的名單計數（inferred）；不以歷次命中累積。
    /// 同公式的大地之擊沿同參數形狀；通用文字沒有單獨顯示該加成。</summary>
    internal static JsonElement WithHitTargetCount(JsonElement row, int count)
    {
        var skill = new SkillConfig(row);
        if (!skill.DamageFormulaExtra.Contains("AXadd_HitCount_ExtraDmg")) return row;
        var node = JsonNode.Parse(row.GetRawText())?.AsObject();
        if (node == null) return row;
        node[HitTargetCountField] = Math.Clamp(count, 0, (int)Math.Max(0, skill.TargetMax));
        return JsonSerializer.SerializeToElement(node);
    }

    internal static double HitTargetMultiplier(SkillConfig skill, int branch = 0)
    {
        var (formula, tuple) = Extra(skill, branch);
        if (formula != "AXadd_HitCount_ExtraDmg" || tuple is not [>= 0, >= 0]
            || !skill.Row.TryGetProperty(HitTargetCountField, out var value) || !value.TryGetInt32(out int count)) return 1;
        return 1 + Math.Min(Math.Max(0, count) * (double)tuple[0], tuple[1]) / 10000;
    }

    /// <summary>閃電衝擊的主動說明：每20敏捷增加1%傷害，對應[1,5,2,1]。
    /// 衝擊椎同形狀[1,10,1,1]指定力量；乘原總傷害再算防禦／元素（inferred）。
    /// 自動閃電列13110的文字寫10敏捷卻仍配置5bp，本候選保留資料表並明列差異。</summary>
    internal static double AttributeDamageMultiplier(SkillConfig skill, StatSheet stats, int branch = 0)
    {
        var (formula, tuple) = Extra(skill, branch);
        if (formula != "T_AXadd0_etc" || tuple is not [1, >= 0, >= 1 and <= 6, 1]
            || skill.ScriptLogic is not ("BlitzBeat" or "PileBunker")) return 1;
        return 1 + Math.Max(0, stats[(int)tuple[2]]) * (double)tuple[1] / 10000;
    }
}
