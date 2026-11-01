using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

internal static partial class SkillExtraFormulaRules
{
    /// <summary>煉獄祝禱：SkillConfig 2308401..6 與說明10110300631明列目標最大HP的20..40%，
    /// 上限施法寵物MATK的1500%。PetStarConfig31003的主動技能直接引用此六級，沒有一般傷害係數。</summary>
    internal static bool HasPetHealthDamage(SkillConfig skill) => skill.ScriptLogic == "lianyuzhudao"
        && skill.DamageFormulaExtra is ["T_AXadd0_etc_gotlimit"]
        && skill.DamageParamExtra is [[2, >= 0, AttributeId.MaxHp, 1, >= 0, AttributeId.Matk, 3]];

    /// <summary>僅在塔寵攻擊入口解譯：先取HP比例與MATK上限，再走既有魔防、元素與浮動（順序inferred）。
    /// 不把其他gotlimit參數當成相同公式，也不讓反射／治療路徑再次套用。</summary>
    internal static JsonElement PetHealthDamageRow(JsonElement row, long targetMaxHp, long casterMatk)
    {
        var skill = new SkillConfig(row);
        if (!HasPetHealthDamage(skill)) return row;
        var tuple = skill.DamageParamExtra[0];
        double amount = Math.Min(Math.Max(0, targetMaxHp) * (double)tuple[1],
            Math.Max(0, casterMatk) * (double)tuple[4]) / 10000;
        var node = JsonNode.Parse(row.GetRawText())?.AsObject();
        if (node == null) return row;
        node["_kDamageParam1"] = new JsonArray(0);
        node["_kDamageParam2"] = new JsonArray(amount);
        return JsonSerializer.SerializeToElement(node);
    }
}
