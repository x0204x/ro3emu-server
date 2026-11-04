using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What _kDamageFormulaExtra adds to a skill's percentage. No client code outside config reads these names, so each shape is
/// read from the rows' text, whose numbers match the tuples: "T_AXadd0_etc" [1, 5, 2, 1] on Blitz Beat is "Every 20 AGI
/// increases this DMG by 1%". "_withlimit" [1, 10, 6, 1, 100] is "Luck Resonance 0.1%", and Resonance's text says "this skill's
/// percentage damage multiplier gains an Extra increase ... Active for up to @{2} STR", so the percentage alone gains it and the
/// stat stops at the limit. "AXaddBplus1_withlimit" [12, 115, 10000, 500] is Rolling Cutter's "(1 + ASPD * 0.12%)", from the
/// whole ASPD (owner's decision 2.11), which stops at 500%.
/// Source 2 or mode 3 tuples are amounts for their owners (shields, Resurrection), not a factor. Not read here: Lord of
/// Vermilion's AXadd_HitCount_ExtraDmg and Death Bound's T_0addAXY, whose text does not say what they do.
/// </summary>
internal static class DamageFormula
{
    private const long Caster = 1, FactorMode = 1, AspdShare = 114;
    private const int Aspd = 115;
    private const double BasisPoints = 10000.0;

    /// <param name="branch">Which damage value the hit takes: a row with one formula per value (Pinpoint Attack) reads the same one.</param>
    internal static double Factor(SkillConfig skill, int branch, Func<int, long> stat)
    {
        var names = skill.DamageFormulaExtra;
        var tuples = skill.DamageParamExtra;
        int i = names.Length > 1 && branch < names.Length ? branch : 0;
        if (names.Length == 0 || i >= tuples.Length) return 1;
        return (names[i], tuples[i]) switch
        {
            ("AXaddBplus1", [var a, var attr, var b]) when b > 0 => 1 + a * Stat(stat, attr) / (double)b,
            ("AXaddBplus1_withlimit", [var a, var attr, var b, var limit]) when b > 0 => 1 + a * Math.Min(Stat(stat, attr), limit) / (double)b,
            ("T_AXadd0_etc", [Caster, var c, var attr, FactorMode]) => 1 + c * Stat(stat, attr) / BasisPoints,
            ("T_AXadd0_etc_withlimit", [Caster, var c, var attr, FactorMode, var limit]) => 1 + c * Math.Min(Stat(stat, attr), limit) / BasisPoints,
            ("T_AXadd0_etc_withlimit", [Caster, var c, var attr, var limit]) => 1 + c * Math.Min(Stat(stat, attr), limit) / BasisPoints, // Radiant Holy Light
            _ => 1,
        };
    }

    /// <summary>T_AXaddBadd3_etc [source, coefficient, attribute, flat] on a skill row, as it reads on the buff rows that heal by it (Breath
    /// Medicine [0,0,1,30], BuffRuleTables): the airship cannons' 5353001 and 5353501, [0,0,1,3000], the only two skill rows that have it.</summary>
    internal static long Flat(SkillConfig skill, int branch)
    {
        var names = skill.DamageFormulaExtra;
        int i = names.Length > 1 && branch < names.Length ? branch : 0;
        return names.Length > i && names[i] == "T_AXaddBadd3_etc" && skill.DamageParamExtra.Length > i
            && skill.DamageParamExtra[i] is [_, 0, _, var flat] ? flat : 0;
    }

    /// <summary>Heal's T_AXadd0_Xisskilllv [1, 10, 0, 1] is "Each Base Level increases Healing Volume by 0.1%": its heal, not its Undead hit.</summary>
    internal static double HealFactor(SkillConfig skill, Func<int, long> stat, long level) =>
        skill.DamageFormulaExtra is ["T_AXadd0_Xisskilllv", ..] && skill.DamageParamExtra is [[Caster, var c, 0, FactorMode], ..]
            ? 1 + c * Math.Max(0, level) / BasisPoints
            : Factor(skill, 0, stat);

    /// <summary>(inferred) 114 is the percent part of ASPD on the sheet, and Ambush's "Every 1% Attack Speed" means the ASPD a player sees. Its text says 0.2%, its tuple 10 (0.1%): the tuple counts.</summary>
    private static long Stat(Func<int, long> stat, long attr) => Math.Max(0, stat(attr == AspdShare ? Aspd : (int)attr));
}
