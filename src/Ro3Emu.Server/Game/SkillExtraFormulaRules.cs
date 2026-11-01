using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>有設定與文字交叉證據的附加公式；不將技能消耗、治療或反射公式當成一般傷害。</summary>
internal static partial class SkillExtraFormulaRules
{
    private const int AttackSpeed = 115;

    /// <summary>BuffConfig 1334001..6 的說明：每點六維額外增加傷害係數，最多生效設定的屬性點數。
    /// 五欄為 [source, bp, attribute, valueType, cap]；ColuseoHeal 的四欄省略 valueType。
    /// 係數加算先於防禦／元素／取整（inferred）；不是對總傷害再乘共鳴比例。</summary>
    internal static double ResonanceRatioBonus(SkillConfig skill, StatSheet stats, int branch = 0)
    {
        var (formula, tuple) = Extra(skill, branch);
        if (formula != "T_AXadd0_etc_withlimit" || tuple.Length is not (4 or 5)
            || tuple[0] != 1 || tuple[2] is < 1 or > 6 || tuple[1] < 0
            || tuple.Length == 5 && tuple[3] != 1) return 0;
        long cap = tuple[^1];
        return cap <= 0 ? 0 : Math.Clamp(stats[(int)tuple[2]], 0, cap) * (double)tuple[1];
    }

    /// <summary>WolfAttack／BanishingPoint 等描述：每 1% 攻速增加設定比例，withlimit 額外限制生效攻速。
    /// AttributeConfig 115 的顯示單位為百分數（100 表示 100%）。僅解釋此已配對形狀。
    /// 與其他增傷的乘算順序沿既有基礎傷害層，原服後端順序仍為 inferred。</summary>
    internal static double AttackSpeedMultiplier(SkillConfig skill, StatSheet stats, int branch = 0)
    {
        var (formula, tuple) = Extra(skill, branch);
        bool limited = formula == "AXaddBplus1_withlimit";
        if ((!limited && formula != "AXaddBplus1") || tuple.Length != (limited ? 4 : 3)
            || tuple[1] != AttackSpeed || tuple[2] != 10000 || tuple[0] < 0) return 1;
        double speed = Math.Max(0, stats[AttackSpeed]);
        if (limited) speed = Math.Min(speed, Math.Max(0, tuple[3]));
        return 1 + tuple[0] * speed / 10000.0;
    }

    private static (string Formula, long[] Tuple) Extra(SkillConfig skill, int branch)
    {
        var names = skill.DamageFormulaExtra;
        var parameters = skill.DamageParamExtra;
        if (names.Length == 0 || parameters.Length == 0 || branch < 0) return ("", []);
        int index = names.Length == 1 ? 0 : branch;
        return index >= names.Length || index >= parameters.Length ? ("", []) : (names[index], parameters[index]);
    }
}
