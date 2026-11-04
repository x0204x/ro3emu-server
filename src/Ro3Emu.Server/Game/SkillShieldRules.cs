using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The finite shield a player skill's _kBuff1 buff carries. BuffConfig holds two shapes:
/// <list type="bullet">
/// <item>Max HP share: _kDamageFormulaExtra T_AXadd0_etc with [source, coefficient, attribute, 3], read by
/// <see cref="PetCombatRules.AttributeAmount"/>. King's Grace 11163401 [0, 2000, 101, 3] is "Shield equal to Max HP *
/// @{1}%" with @{1} 20; Rift Impact's 1130505 (1000) is "${7}% of Max HP" with ${7} 10; Shield Cast's 1161101 (500) is
/// its skill's ${3} 5 (the buff's own text shows 0.5); High-Energy Impact's 1634601 (1000) is its ${1} 10.</item>
/// <item>Magic ATK ratio: _iDmgType 2 and _kData1[0] basis points of the caster's Magic ATK. Kyrie Eleison 1411701
/// (4000) is "Magic ATK*${1}%" with ${1} 40; Radiant Holy Light 1430301 (3700) is its skill's ${4} 37; Highness Heal's
/// Psalm 11430701 (4000) is its ${3} 40. (inferred) Original Melody Chorus 11430901 takes its row's 1350 although
/// its skill's ${4} says 100.</item>
/// </list>
/// (inferred) Rounding down follows the pet shields.
/// </summary>
internal static class SkillShieldRules
{
    /// <summary>BuffConfig._kBuffCategory 30. (inferred) The shield tag: every skill-applied row that carries it is named
    /// a shield (Shield, Kyrie Eleison, Radiant Holy Light, Vanguard Shield, Fairy Shield, Phantom Barrier), while the
    /// untagged T_AXadd0_etc rows also restore Life (Resurrection, Meditate, Tension Relax).</summary>
    internal const long ShieldCategory = 30;

    /// <summary>BuffConfig._iBuffID of High-Energy Impact's "High-Energy Shield" 1634601 ("Gain a shield."), which lacks
    /// category 30. Its skill 1630701 reads "gain a Shield equal to ${1}% of your Max HP" with ${1} 10.</summary>
    internal const long HighEnergyShieldGroup = 16346;

    /// <summary>BuffConfig._iBuffID of Kyrie Eleison: "This shield absorbs Physical damage equal to Magic ATK *${1}%".
    /// </summary>
    internal const long KyrieEleisonGroup = 14117;

    private const int ShieldBonus = 1205;
    private const long MagicDamage = 2; // _iDmgType 2, as CombatService.BaseDamage reads it
    private const double BasisPoints = 10000.0;

    internal static bool IsShield(BuffConfig buff) =>
        buff.BuffCategory.Contains(ShieldCategory) || buff.BuffId == HighEnergyShieldGroup;

    /// <summary>The shield one application (one pile) of <paramref name="buff"/> carries; 0 for other buffs.</summary>
    internal static long Amount(BuffConfig buff, StatSheet caster, StatSheet recipient)
    {
        if (!IsShield(buff)) return 0;
        long share = PetCombatRules.AttributeAmount(buff.Row, caster, recipient);
        long amount = share > 0 ? share : MagicRatio(buff, caster);
        // 1205 per_shieldbonus: Prayer of Gratitude "Shield Efficiency +${1}%" (inferred: the caster's).
        return (long)Math.Floor(amount * Math.Max(0, 1 + caster[ShieldBonus] / BasisPoints));
    }

    private static long MagicRatio(BuffConfig buff, StatSheet caster) =>
        buff.DmgType == MagicDamage && buff.Data1 is [var ratio, ..] && ratio > 0
            ? Math.Max(0, (long)Math.Floor(caster[AttributeId.Matk] * ratio / BasisPoints)) : 0;

    /// <summary>(inferred) Each new pile adds one shield to what is left; the total never exceeds one shield per pile
    /// (Rift Impact: "For each enemy hit, You gain a Shield equal to ${7}% of Max HP ..., up to ${9}%").</summary>
    internal static long Stacked(long held, long perPile, int pile)
    {
        if (perPile <= 0) return 0;
        long cap = perPile > long.MaxValue / Math.Max(1, pile) ? long.MaxValue : perPile * Math.Max(1, pile);
        long total = held > long.MaxValue - perPile ? long.MaxValue : Math.Max(0, held) + perPile;
        return Math.Min(total, cap);
    }

    /// <summary>Kyrie Eleison's shield absorbs Physical damage only.</summary>
    internal static bool AbsorbsPhysicalOnly(BuffConfig buff) => buff.BuffId == KyrieEleisonGroup;

    /// <summary>A hit of a magic SkillConfig row; a hit without a row counts as physical, as BaseDamage reads it.</summary>
    internal static bool IsMagic(JsonElement? incoming) =>
        incoming is { } row && new SkillConfig(row).DmgType == MagicDamage;
}
