using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The learned passives' standing effects: a passive row's _kAttri, and its own BuffConfig row. The client data links a
/// passive to that row only by their English names, and the row's id does not always share the passive's
/// (<see cref="OwnBuffs"/>). The own row's _kSkillAttributeList counts like a live buff's tuples and its _kChangeAttri adds
/// attributes. A weapon mastery's own row names in _kLogicData the BuffConfig row that applies while the weapon its
/// description names is held (<see cref="MasteryWeapons"/>). Procs, shields and reactions are in PassiveSkills.
/// </summary>
public sealed partial class CombatService
{
    /// <summary>
    /// Passive skill group -> the id prefix of its own BuffConfig rows (prefix * 100 + level), every level with the
    /// passive's English name in Localization_en (inferred). The same id but another name is another buff: BuffConfig
    /// 1310410 is Steel Crow's, not Beastbane's 1310410, and Riding's 1140701 is Providence. Union's row is its team
    /// stacks (_iMaxStack 10) and the mount passives' rows (Wolf Taming, Wolf's Fang) need a mount: RidingEffects grants them.
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, int> OwnBuffs = new Dictionary<int, int>
    {
        [11003] = 11003, // Fatal Blow
        [11009] = 11009, // Sword Mastery
        [11101] = 11101, // Spear Mastery (Knight line)
        [11401] = 11101, // Spear Mastery (Crusader line): the same rows
        [11103] = 11103, // Sword Quicken
        [11313] = 11313, // Enchant Blade
        [11421] = 11412, // Monster Resistance
        [11508] = 11510, // Advanced Monster Resistance
        [11637] = 11640, // Advanced Monster Resistance
        [12003] = 12014, // Energy Coat
        [12209] = 12209, // Soul Drain
        [13105] = 13104, // Steel Crow
        [14002] = 14002, // Signum Crucis
        [14101] = 14101, // Mace Mastery
        [14104] = 14104, // Recovery
        [14203] = 14203, // Meditatio
        [14204] = 14204, // Mana Recharge
        [14207] = 14207, // Arcane Mastery
        [14306] = 14306, // Prayer of Gratitude
        [14307] = 14307, // Wood Spirit's Blessing
        [14311] = 14311, // Double Holy Light
        [15002] = 15002, // Double Attack
        [15101] = 15111, // Katar Mastery
        [15107] = 15115, // Dual-Wield Mastery
        [15220] = 15220, // Advanced Katar Mastery
        [16101] = 16101, // Hilt Binding
        [16302] = 16302, // Axe Mastery
        [16303] = 16307, // Emergency Treatment
    };

    // ItemConfig._iSubType of the weapons a mastery's description names: 1 Dagger, 2 Sword, 3 Two-Handed Sword,
    // 4 Halberd, 5 Knight Spear, 6 Axe, 7 Battle Axe, 8 Club, 12 Katar, 50 Guard (the shield).
    private static readonly long[] Swords = [2, 3], Spears = [4, 5], Maces = [8], AxesAndMaces = [6, 7, 8], Katars = [12],
        Daggers = [1], Shields = [50];

    /// <summary>Weapon masteries: their _kLogicData BuffConfig row applies while one of these weapons is held (inferred
    /// from the descriptions; the _kLogicData lists repeat that row, one entry per weapon or hand).</summary>
    private static readonly Dictionary<int, long[]> MasteryWeapons = new()
    {
        [11009] = Swords, // Sword Mastery: "When equipped with a sword, Physical ATK increases by ${1}."
        [11101] = Spears, // Spear Mastery: "When equipped with a Spear weapon, increases Physical ATK by ${1}."
        [11401] = Spears,
        [11103] = Swords, // Sword Quicken: "When equipped with a sword, HIT increases by ${1} and Attack Speed ..."
        [11421] = Shields, // Monster Resistance: "... damage reduction from monsters by ${1}% while a shield is equipped."
        [11508] = Shields,
        [11637] = Shields,
        [14101] = Maces, // Mace Mastery: "When equipped with a [Mace] weapon, gain ${1} Physical ATK ..."
        [15101] = Katars, // Katar Mastery: "While equipped with a Katar-type weapon, increases Physical ATK by ${1} ..."
        [15107] = Daggers, // Dual-Wield Mastery: "When equipped with a dagger, increases Attack Speed by ${1} and AGI ..."
        [16302] = AxesAndMaces, // Axe Mastery: "When equipped with an axe or mace, increases your Physical ATK ..."
    };

    private const int DoubleAttackGroup = 15002, SupersonicBlow = 15103, ArcaneMastery = 14207, TurnUndead = 14118;
    private const long CritAttribute = 111; // AttributeConfig "CRIT"; Advanced Katar Mastery's "+1 Crit" row is [111, 100]
    private int[]? _passiveGroups;

    /// <summary>
    /// The learned passives' attributes: each row's _kAttri, its own row's _kChangeAttri and a held mastery's. Double
    /// Attack's "When a Katar is equipped, increases CRIT Stat by ${4}" has no row: ${4} * 100 CRIT (inferred).
    /// </summary>
    public IEnumerable<(long Id, double Value)> PassiveAttributes(GameSession session)
    {
        foreach (var row in PassiveRows(session))
        {
            var skill = new SkillConfig(row);
            foreach (var pair in skill.Attri)
                if (pair is [var id, var value]) yield return (id, value);
            if (OwnBuff(skill) is { } own)
                foreach (var pair in own.ChangeAttri)
                    if (pair is [var id, var value]) yield return (id, value);
            if (MasteryBuff(skill) is { } mastery && MasteryWeapons.TryGetValue((int)skill.SkillId, out var weapons)
                && weapons.Any(w => SkillParameterRules.Wields(session, _data, w)))
                foreach (var pair in mastery.ChangeAttri)
                    if (pair is [var id, var value]) yield return (id, value);
            if (skill.SkillId == DoubleAttackGroup && Katars.Any(w => SkillParameterRules.Wields(session, _data, w))
                && skill.DescData.Length > 3 && double.TryParse(skill.DescData[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double crit))
                yield return (CritAttribute, crit * 100);
        }
    }

    /// <summary>
    /// The learned passives' skill tuples, each with the weapons it needs (empty: none): the own row's (Advanced Katar
    /// Mastery 1522001: "[Assassin Normal Attack] CRIT Chance increases by ${5}%, but its damage is reduced by ${6}%" is
    /// [15000, 10, +4000] and [15000, 29, -2000]) and a mastery row's (Mace Mastery's normal attack CRIT).
    /// </summary>
    internal IReadOnlyList<(long[] Weapons, SkillParameterRules.Modifier Modifier)> PassiveModifiers(GameSession session)
    {
        var result = new List<(long[], SkillParameterRules.Modifier)>();
        foreach (var row in PassiveRows(session))
        {
            var skill = new SkillConfig(row);
            // Arcane Mastery: "... and increases the damage of [Turn Undead] by ${2}." as flat damage (inferred; its own
            // row's _kLogicData names a stacking MATK buff that nothing in the data triggers).
            if (skill.SkillId == ArcaneMastery && skill.DescData.Length > 1
                && long.TryParse(skill.DescData[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long bonus))
                result.Add(([], new SkillParameterRules.Modifier(TurnUndead, 30, 1, bonus, -1)));
            if (OwnBuff(skill) is { } own)
                foreach (var modifier in SkillParameterRules.BuffModifiers(_data, (int)own.Id)) result.Add(([], modifier));
            if (MasteryBuff(skill) is { } mastery && MasteryWeapons.TryGetValue((int)skill.SkillId, out var weapons))
                foreach (var modifier in SkillParameterRules.BuffModifiers(_data, (int)mastery.Id)) result.Add((weapons, modifier));
        }
        return result;
    }

    /// <summary>
    /// Sonic Blow: "If the target's HP is at or below ${4}%, increases the percentage damage multiplier of [Sonic Blow] by
    /// ${5}%." Its damage pairs are [plain, +20%, +25%] and its _kLogicData [2000, 101, 5000] the 20% and the 50% (in basis
    /// points). Supersonic Blow: "Allows [Sonic Blow] to trigger its damage increase effect when the target's Life is at or
    /// below ${1}%, and changes the percentage damage multiplier increase to ${2}%": the third pair below its ${1}%. The
    /// target's Life is read before the hit (inferred).
    /// </summary>
    private int SonicBlowBranch(GameSession s, MonsterUnit target, JsonElement row)
    {
        var skill = new SkillConfig(row);
        if (skill.ScriptLogic != "SonicBlow" || skill.LogicData is not [_, _, var threshold, ..] || skill.DamageParam1.Length < 2) return 0;
        double life = target.Hp * 10000.0 / Math.Max(1, target.Template.MaxHp);
        if (skill.DamageParam1.Length > 2 && LearnedPassive(s, SupersonicBlow) is { DescData: [var below, ..] }
            && double.TryParse(below, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent)
            && life <= percent * 100) return 2;
        return life <= threshold ? 1 : 0;
    }

    /// <summary>A passive row's own BuffConfig row (<see cref="OwnBuffs"/>), or null.</summary>
    internal BuffConfig? OwnBuff(SkillConfig skill) =>
        OwnBuffs.TryGetValue((int)skill.SkillId, out int prefix) ? BuffConfig.Get(_data, prefix * 100L + skill.Level) : null;

    /// <summary>A weapon mastery's conditional row: the first BuffConfig row its own row's _kLogicData names.</summary>
    private BuffConfig? MasteryBuff(SkillConfig skill)
    {
        if (!MasteryWeapons.ContainsKey((int)skill.SkillId) || OwnBuff(skill) is not { } own) return null;
        foreach (long id in own.LogicData)
            if (BuffConfig.Get(_data, id) is { } row) return row;
        return null;
    }

    /// <summary>The learned level's row of a passive group of the career's tree (its whole career chain), or null: a
    /// career changed by a GM to another line keeps the old line's levels, which no longer count.</summary>
    internal SkillConfig? LearnedPassive(GameSession session, int group)
    {
        if (session.Progress is not { } progress || !_skills.InTree(progress.Career, group)
            || _skills.Level(progress, group) is not (> 0 and var level)) return null;
        return SkillConfig.Get(_data, _skills.RealId(group, level)) is { BasicType: 0 } row ? row : null;
    }

    /// <summary>The SkillConfig row of each learned mode-0 passive at its level.</summary>
    private IEnumerable<JsonElement> PassiveRows(GameSession session)
    {
        if (session.Progress == null) yield break;
        _passiveGroups ??= [.. SkillConfig.All(_data).Where(r => r.BasicType == 0 && r.Mode == 0 && r.InitialLevel > 0)
            .Select(r => (int)r.SkillId).Where(g => g > 0).Distinct()];
        foreach (int group in session.Progress.Skills.Keys.Concat(_passiveGroups).Distinct())
            if (LearnedPassive(session, group) is { } row) yield return row.Row;
    }
}
