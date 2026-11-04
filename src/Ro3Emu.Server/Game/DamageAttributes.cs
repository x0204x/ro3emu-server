namespace Ro3Emu.Server.Game;

/// <summary>
/// The attributes a hit reads on top of attack, defense and the element table. The client does not compute a hit, but
/// PBDubugDamageFormulaEnum lists every term, each attacker's beside the defender's, in this order: ADamagePercent /
/// BBeDamagePercent, AAtkDmgPer, the melee or ranged pair, AMAtkDmgPer, then the same again as Pec* (the 12xx buff attributes),
/// ASkillDamagePercent, ARaceAttr, AElementDamageAttr, AElementMonsterPercent, ABodyAttr. AttributeConfig names each one.
/// (inferred) A group adds up inside and the groups multiply, as the list groups them. Values are basis points.
/// </summary>
internal static class DamageAttributes
{
    private const int All = 215, AllTaken = 216, Physical = 211, PhysicalTaken = 212, Magic = 213, MagicTaken = 214;
    private const int MeleePhysical = 227, MeleePhysicalTaken = 228, RangedPhysical = 229, RangedPhysicalTaken = 230;
    private const int MeleeMagic = 250, MeleeMagicTaken = 251, RangedMagic = 252, RangedMagicTaken = 253;
    private const int BuffGroup = 1000; // 1215 is 215 from a buff, and so on
    private const int Skill = 225, SkillTaken = 226;
    private const int VsRace = 400, FromRace = 410, ElementDealt = 300, ElementTaken = 310, VsElement = 320, FromElement = 330;
    private const int SizeBase = 499; // 501/502 small, 503/504 medium, 505/506 large
    private const int HuntingMastery = 231, HuntingProtection = 232;
    private const int VsBoss = 601, FromBoss = 602, VsNormal = 603, FromNormal = 604; // boss_type, be_boss_type, normal_type, be_normal_type
    internal const int CritDamage = 209, CritDamageTaken = 210, CritChance = 237, CritChanceTaken = 238;
    internal const int IgnoreDef = 217, IgnoreDefPercent = 218, IgnoreMdef = 219, IgnoreMdefPercent = 220;

    /// <summary>PublicParamConfig BOSSDamageMin / NonBOSSDamageMin 0.1 and BOSSDamageMax / NonBOSSDamageMax 10.</summary>
    private const double MinFactor = 0.1, MaxFactor = 10;
    private const double BasisPoints = 10000.0;

    /// <summary>What a monster side counts as for 601-604; a player is neither.</summary>
    internal enum Kind { Player, Normal, Boss }

    internal readonly record struct Hit(bool MagicHit, bool Ranged, bool SkillHit, int DealtElement,
        int AttackerRace, int AttackerSize, int AttackerElement, int TargetRace, int TargetSize, int TargetElement,
        Kind Attacker = Kind.Player, Kind Target = Kind.Player);

    internal static double Factor(Func<int, long>? attacker, Func<int, long>? target, Hit hit)
    {
        long A(int id) => attacker?.Invoke(id) ?? 0;
        long B(int id) => target?.Invoke(id) ?? 0;
        double Group(int shift) => (A(All + shift) - B(AllTaken + shift)
            + (hit.MagicHit ? A(Magic + shift) - B(MagicTaken + shift) : A(Physical + shift) - B(PhysicalTaken + shift))
            + (hit.MagicHit
                ? hit.Ranged ? A(RangedMagic + shift) - B(RangedMagicTaken + shift) : A(MeleeMagic + shift) - B(MeleeMagicTaken + shift)
                : hit.Ranged ? A(RangedPhysical + shift) - B(RangedPhysicalTaken + shift) : A(MeleePhysical + shift) - B(MeleePhysicalTaken + shift))) / BasisPoints;
        double factor = (1 + Group(0)) * (1 + Group(BuffGroup));
        if (hit.SkillHit) factor *= 1 + (A(Skill) - B(SkillTaken)) / BasisPoints;
        factor *= 1 + ((hit.TargetRace is >= 1 and <= 10 ? A(VsRace + hit.TargetRace) : 0)
            - (hit.AttackerRace is >= 1 and <= 10 ? B(FromRace + hit.AttackerRace) : 0)) / BasisPoints;
        if (hit.DealtElement is >= 1 and <= 10) factor *= 1 + (A(ElementDealt + hit.DealtElement) - B(ElementTaken + hit.DealtElement)) / BasisPoints;
        factor *= 1 + ((hit.TargetElement is >= 1 and <= 10 ? A(VsElement + hit.TargetElement) : 0)
            - (hit.AttackerElement is >= 1 and <= 10 ? B(FromElement + hit.AttackerElement) : 0)) / BasisPoints;
        factor *= 1 + ((hit.TargetSize is >= 1 and <= 3 ? A(SizeBase + 2 * hit.TargetSize) : 0)
            - (hit.AttackerSize is >= 1 and <= 3 ? B(SizeBase + 2 * hit.AttackerSize + 1) : 0)) / BasisPoints;
        // Monster Resistance: "-30% damage from monsters" is 602 and 604 together; its own group (inferred).
        factor *= 1 + ((hit.Target switch { Kind.Boss => A(VsBoss), Kind.Normal => A(VsNormal), _ => 0 })
            - (hit.Attacker switch { Kind.Boss => B(FromBoss), Kind.Normal => B(FromNormal), _ => 0 })) / BasisPoints;
        // Hunting Mastery "Affects your damage against monsters" and Hunting Protection "... taken from monsters": Formula_APveDmg, Formula_BPveBeDmg
        // and Formula_PvEaram. (inferred) As factors of their own, at x / (x + FormulaParamPVE) as the panel's type 7 shows 1328 and 1329.
        if (hit.Target != Kind.Player) factor *= 1 + PveShare(A(HuntingMastery));
        if (hit.Attacker != Kind.Player) factor *= 1 - PveShare(B(HuntingProtection));
        return Math.Clamp(factor, MinFactor, MaxFactor);
    }

    /// <summary>PublicParamConfig FormulaParamPVE 10000.</summary>
    internal static double PveShare(long value) => value <= 0 ? 0 : value / (value + BasisPoints);

    /// <summary>Formula_AIgnoreDef and Formula_AIgnoreDefPercent, and their magic pair: flat first, then the share.</summary>
    internal static double DefenseAfterPenetration(double defense, long flat, long percent) =>
        Math.Max(0, defense - flat) * Math.Max(0, 1 - percent / BasisPoints);

    private const int BonusAtk = 221, BonusMatk = 222, BonusDef = 223, BonusMdef = 224;

    /// <summary>Bonus ATK "Adds an Extra attack that ignores DEF when calculating damage", Bonus DEF "Reduces damage from the target's Extra attacks
    /// and attacks", and their magic pair: Formula_AtkAppend and Formula_AtkReduce stand after DamageFix and before the race and element terms in
    /// PBDubugDamageFormulaEnum. (inferred) So an amount added after defense and before the factors. Nothing else reads these four, and nothing
    /// converts them into ATK or DEF, so they count once.</summary>
    internal static long FlatBonus(Func<int, long>? attacker, Func<int, long>? target, bool magic) =>
        (attacker?.Invoke(magic ? BonusMatk : BonusAtk) ?? 0) - (target?.Invoke(magic ? BonusMdef : BonusDef) ?? 0);

    /// <summary>FormulaParamCriDmg 15000 plus Crit Damage, as LC_DAL_PlayerInfo.GetRealPropertyValue shows it; Formula_BeCriticalDamage takes off the same (inferred).</summary>
    internal static double CritMultiplier(long dealt, long taken) => Math.Max(1, 1.5 + (dealt - taken) / BasisPoints);
}
