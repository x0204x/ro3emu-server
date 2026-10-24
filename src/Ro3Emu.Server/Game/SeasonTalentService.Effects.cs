using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Profession talents' SpecialEffectConfig operations: SeasonTalentEffectConfig._kSpecialEffectList of the active plan's
/// bought nodes. The client only describes them (LC_SeasonTalentHelper.GetSpecialEffectDesc), and its
/// enum_SpecialEffectType stops at 15, so the layouts of the server-only ops are read from the exported rows and their
/// descriptions (inferred):
/// - Attr(1) [1, attr, value]: "Physical ATK +15";
/// - 16 [16, weapon, attr, value]: "When a Sword is equipped, STR +10" (ItemConfig._iSubType 2 Sword, 3 Two-Handed Sword);
/// - 19 [19, skill group, learned level, attr, value]: "When Mace Mastery reaches Lv. 10, STR +5";
/// - 22 [22, weapon, attr, per learned level, skill group]: "PATK +2 for each Learn Lv. of Sword Quicken";
/// - 21 [21, weapon or 0, BuffConfig._kSkillAttributeList tuple]: "Bowling Bash's Cooldown is changed to 8 seconds";
/// - 20 [20, pendant CartRemouldTable._iSubType, attr, value]: Falcon's Might, whose _kOtherDesc buff reads "Damage
///   Increase: ${1}% while fighting alongside [Falcon]" (attribute 215 Damage Increase%, +100); the leased falcon is the
///   pendant row of subtype 3, as type 17 names a ridden mount's subtype;
/// - Buff(2): the buff's _kSkillAttributeList ("Basic Attack's percentage damage multiplier increases by 3%").
/// The ops of one effect that differ only in their condition ("a dagger or katar", "Sanctuary or Magnus Exorcismus")
/// apply once. The skill tuples go through SkillParameterRules like a buff's.
/// Buff(2) procs (_kLogicData: Rapid Pierce's extra hits, Double Counter's extra cast ...) are TalentProcs'. A Buff(2) row's
/// _kBuffAttributeList and BuffDictRef(12) change other buffs (GameSession.TalentBuffAttributes): scope 0 names a buff
/// group, scope 1 a buff category (Scouting's ["1", "109", "4", "1", "300", "0"]: "[Detect Stealth] range +${1}
/// meters" on the detection category).
/// </summary>
public sealed partial class SeasonTalentService
{
    private const long AttrOp = 1, BuffOp = 2, BuffDictRefOp = 12, WeaponAttrOp = 16, SkillLevelAttrOp = 19, PendantAttrOp = 20,
        SkillParameterOp = 21, WeaponSkillAttrOp = 22;

    /// <summary>
    /// One _kBuffAttributeList tuple ["0", buff group, EBuffAffectValueType, operation, value, index, ...], also written as
    /// op [12, 0, group, type, operation, value, index, ...] (BuffDictRef). Type 2 (BuffAttr) changes the value of the
    /// group's _kChangeAttri pair at index: Guard Enhance's two rows give Parry's PDEF and MDEF pairs +50 each. The
    /// operation arithmetic is SkillParameterRules' (1 add, 2 set, 5 basis points) (inferred).
    /// </summary>
    internal readonly record struct BuffAttribute(long Group, int Type, int Operation, double Value, int Index, bool ByCategory = false);

    private static BuffAttribute? ParseBuffAttribute(IReadOnlyList<double> t) =>
        t.Count >= 6 && t[0] is 0 or 1 && t[1] > 0 ? new BuffAttribute((long)t[1], (int)t[2], (int)t[3], t[4], (int)t[5], t[0] == 1) : null;

    /// <summary>The leased pendant's CartRemouldTable._iSubType (3: the falcon), 0 without one.</summary>
    private long PendantSubType(GameSession s) => s.CareerSpecial?.PendantSlot is { CartRemouldCfg: > 0 } slot
        && CartRemouldTable.Get(_data, slot.CartRemouldCfg) is { } row ? row.SubType : 0;

    private IEnumerable<BuffAttribute> BuffAttributes(long buff)
    {
        if (BuffConfig.Get(_data, buff)?.BuffAttributeList is not { ValueKind: JsonValueKind.Array } list) yield break;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Array
                && ParseBuffAttribute([.. item.EnumerateArray().Select(v => GameData.Number(v) ?? double.NaN)]) is { } parsed
                && !double.IsNaN(parsed.Value))
                yield return parsed;
    }

    /// <summary>The values a description shows: the effect's own, or its _kOtherDesc buff's (_iIsOtherDesc 1).</summary>
    private string[] DescriptionValues(SpecialEffectConfig special) =>
        special.IsOtherDesc == 1 && special.OtherDesc is [_, var buff] && BuffConfig.Get(_data, buff) is { } other
            ? other.DescData : special.DescData;

    /// <summary>Observers of a changed set of talent skill modifiers in the world (CombatService republishes 31132).</summary>
    public List<Func<GameSession, Task>> EffectsChanged { get; } = [];

    private IEnumerable<(long Id, double Value)> SpecialAttributes(GameSession s, JsonElement effect)
    {
        foreach (long id in new SeasonTalentEffectConfig(effect).SpecialEffectList)
        {
            if (SpecialEffectConfig.Get(_data, id) is not { } special) continue;
            var once = new HashSet<(long Op, long Attr, long Value, long Group)>();
            foreach (var op in special.SpecialEffect)
            {
                switch (op)
                {
                    case [AttrOp, var attr, var value]:
                        yield return (attr, value);
                        break;
                    case [WeaponAttrOp, var weapon, var attr, var value]
                        when SkillParameterRules.Wields(s, _data, weapon) && once.Add((WeaponAttrOp, attr, value, 0)):
                        yield return (attr, value);
                        break;
                    case [SkillLevelAttrOp, var group, var level, var attr, var value]
                        when LearnedLevel(s, group) >= level && once.Add((SkillLevelAttrOp, attr, value, 0)):
                        yield return (attr, value);
                        break;
                    case [PendantAttrOp, var subType, var attr, var value]
                        when subType > 0 && PendantSubType(s) == subType && once.Add((PendantAttrOp, attr, value, subType)):
                        yield return (attr, value);
                        break;
                    case [WeaponSkillAttrOp, var weapon, var attr, var perLevel, var group]
                        when SkillParameterRules.Wields(s, _data, weapon) && once.Add((WeaponSkillAttrOp, attr, perLevel, group)):
                        yield return (attr, perLevel * LearnedLevel(s, group));
                        break;
                    // "While [Battle Chant] is active, your Max HP increases by ${1}.": the granted buff's _kChangeAttri.
                    case [BuffOp, var buff] when TalentProcs.WhileActive.TryGetValue(buff, out long active)
                        && SkillParameterRules.HasBuffGroup(s, _data, active) && BuffConfig.Get(_data, buff)?.LogicData is [var granted, ..]
                        && BuffConfig.Get(_data, granted) is { } grantedRow:
                        foreach (var change in grantedRow.ChangeAttri)
                            if (change is [var attr, var value]) yield return (attr, value);
                        break;
                }
            }
        }
    }

    private static long LearnedLevel(GameSession s, long group) => s.Progress?.Skills.GetValueOrDefault((int)group) ?? 0;

    /// <summary>The active plan's skill parameter modifiers, each with the weapon and the buff group it requires (0 = none),
    /// its Buff(2) rows with their effect's description values, and its changes to other buffs.</summary>
    private (List<(long Weapon, long Buff, SkillParameterRules.Modifier Modifier)> Modifiers, List<(long Buff, string[] Description)> Buffs,
        List<BuffAttribute> BuffAttributes) SkillEffects(GameSession s)
    {
        var modifiers = new List<(long, long, SkillParameterRules.Modifier)>();
        var buffs = new List<(long, string[])>();
        var attributes = new List<BuffAttribute>();
        foreach (var effect in ActiveEffects(s))
            foreach (long id in new SeasonTalentEffectConfig(effect).SpecialEffectList)
            {
                if (SpecialEffectConfig.Get(_data, id) is not { } special) continue;
                foreach (var op in special.SpecialEffect)
                {
                    if (op is [BuffDictRefOp, ..] && ParseBuffAttribute([.. op.Skip(1).Select(v => (double)v)]) is { } reference)
                        attributes.Add(reference);
                    else if (op is [SkillParameterOp, var weapon, 0, var group, var type, var operation, var value, ..] && group > 0)
                        modifiers.Add((weapon, 0, new SkillParameterRules.Modifier((int)group, (int)type, (int)operation, value,
                            op.Length > 7 ? (int)op[7] : 0)));
                    else if (op is [BuffOp, var buff])
                    {
                        foreach (var modifier in SkillParameterRules.BuffModifiers(_data, (int)buff)) modifiers.Add((0, 0, modifier));
                        // "While [Aura Blade] is active ...": the buff _kLogicData names applies while that group lives.
                        if (TalentProcs.WhileActive.TryGetValue(buff, out long active) && BuffConfig.Get(_data, buff)?.LogicData is [var granted, ..])
                            foreach (var modifier in SkillParameterRules.BuffModifiers(_data, (int)granted)) modifiers.Add((0, active, modifier));
                        attributes.AddRange(BuffAttributes(buff));
                        buffs.Add((buff, DescriptionValues(special)));
                    }
                }
            }
        return (modifiers, buffs, attributes);
    }

    /// <summary>Recompute the session's talent skill modifiers and procs after a talent, plan or quest change.</summary>
    internal async Task RefreshEffectsAsync(GameSession s)
    {
        var (next, buffs, attributes) = SkillEffects(s);
        s.TalentProcBuffs = buffs;
        s.TalentBuffAttributes = attributes;
        if (next.SequenceEqual(s.TalentSkillModifiers)) return;
        s.TalentSkillModifiers = next;
        if (s.SceneLoading || s.Role == null) return; // the loaded scene publishes 31132 itself
        foreach (var observer in EffectsChanged) await observer(s);
    }
}
