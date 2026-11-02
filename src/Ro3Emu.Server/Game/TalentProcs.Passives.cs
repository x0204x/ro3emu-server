using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The base passives the Thief talents build on. Each reads its BuffConfig row, which shares the passive's SkillConfig id:
/// - Double Attack (group 15002): "When a Dagger is equipped, each Basic Attack has a ${1}% Chance to deal ${2} Extra hits
///   of Neutral melee Physical damage equal to PATK * ${3}% (this damage is not considered a Normal Attack)." The chance
///   is the row's _kLogicData[0] in basis points (100 at level 1, 1000 at level 10).
/// - Deadly Poison (buff group 15211, _kLogicData [3200, 1523001]): "After attacking, there is a @{2}% Chance to inflict
///   [Venom] damage on the target": Venom Sting 1523001 on the monster the cast hit.
/// - Advanced Katar Mastery (15220, _kLogicData [1522101, 10000, 1]): "When a Basic Attack crits (internal cooldown:
///   ${1}s), your ${2} Crit increases for ${3}s, stacking up to ${4} times": _kLogicData[2] piles of the CRIT buff
///   1522101, with a _kLogicData[1] basis-point chance.
/// A talent's BuffLogicData tuple (type 4) changes these values: Efficient Deadly Poison [15211, +2000 at 0], Seize the
/// Initiative [15220, set 5 at 2], Combo Moonlit Challenger [15002, +1000 at 1]. Double Attack's _kLogicData has one value,
/// so a tuple past its end changes that chance (inferred).
/// Blitz Beat is the Hunter's: <see cref="AutoBlitzBeatAsync"/>.
/// </summary>
public sealed partial class TalentProcs
{
    private const int DoubleAttack = 15002, KatarMastery = 15220;
    private const long DeadlyPoison = 15211, Dagger = 1;
    private const int BuffLogicType = 4; // EBuffAffectValueType.BuffLogicData
    // Triple Slash: "When [Double Attack] triggers, it additionally Deals DMG equal to PATK * ${3}% + ${4} as Neutral Melee
    // Physical damage to up to ${2} enemies within a ${1}-meter radius of you. Cooldown: ${5} seconds."
    private const long TripleSlash = 1523901;
    // Blitz Beat's area in the description: "... the designated ST enemy ... and up to ${3} Other enemies within ${2} meters".
    private static readonly Rule BlitzBeatArea = new([], Radius: 1, Others: 2);
    // SkillConfig._iSkillCondition of Blitz Beat and its automatic rows (86000361, 86000415 ...): LC_ConditionList's
    // CheckMountType02 [1500400] (not riding the wolf) and CheckPendantType01 [1] (a pendant of PendantConfig._iType 1).
    private const long WolfMount = 1500400, FalconPendantType = 1;

    private async Task PassiveProcsAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed,
        SkillHitInfo hit)
    {
        if (s.Progress == null || (processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? (processed.Count > 0 ? processed[0] : null))
            is not { } target) return;
        if (cast.IsNormalAtk == 1)
        {
            await DoubleAttackAsync(s, cast, target);
            if (hit.Dsts.Any(d => d.DstID == target.Uid && d.HitResult is DamageHitResult.Critical or DamageHitResult.DoubleCritical))
                await KatarMasteryAsync(s);
        }
        if (cast.IsNormalAttack(_data)) await AutoBlitzBeatAsync(s, action, target);
        await VenomAsync(s, target);
    }

    private async Task DoubleAttackAsync(GameSession s, SkillConfig cast, MonsterUnit target)
    {
        int level = _skills.Level(s.Progress!, DoubleAttack);
        if (level <= 0 || target.Hp <= 0 || !SkillParameterRules.Wields(s, _data, Dagger)) return;
        int id = _skills.RealId(DoubleAttack, level);
        if (SkillConfig.Get(_data, id) is not { } passive || BuffConfig.Get(_data, id)?.LogicData is not { Length: > 0 } logic) return;
        if (Roll(10000) >= LogicValue(s, DoubleAttack, logic, 0)) return;
        var desc = passive.DescData;
        var row = DescribedHit(cast, id, (long)(Value(desc, 2) * 100), 0, Neutral);
        for (int i = 0; i < Math.Max(1, (int)Value(desc, 1)) && target.Hp > 0; i++) await _combat.CastExtraAsync(s, row, [target]);
        if (Talent(s, TripleSlash) is not { } triple || !Ready(s, TripleSlash, (long)(Value(triple, 4) * 1000))) return;
        var around = _combat.MonstersNear(s, s.Position, (float)Value(triple, 0)).Take((int)Value(triple, 1)).ToList();
        if (around.Count > 0)
            await _combat.CastExtraAsync(s, DescribedHit(cast, TripleSlash, (long)(Value(triple, 2) * 100), (long)Value(triple, 3), Neutral), around);
    }

    /// <summary>
    /// Blitz Beat: "When using a Basic Attack, the Falcon has a ${8}% Chance to cast [Blitz Beat] on the enemy, dealing
    /// ${9}% of the original skill's Neutral Ranged Physical DMG." Its BuffConfig row shares the SkillConfig id and holds
    /// [chance in basis points, the automatic group 13110], whose rows have that share of the damage and _kAutoLearnSkill
    /// links level for level. Thunder Shockwave's type 4 tuple adds to the chance. The description names no cooldown, though
    /// the row copies Blitz Beat's _iCD, so it has none (inferred). The automatic cast counts for the talents Blitz Beat
    /// triggers, the one place a proc leads to another (see Rules) (inferred).
    /// </summary>
    private async Task AutoBlitzBeatAsync(GameSession s, ActiveSkillCast action, MonsterUnit target)
    {
        int level = _skills.Level(s.Progress!, BlitzBeat);
        if (level <= 0 || target.Hp <= 0 || !FalconFollows(s)) return;
        if (BuffConfig.Get(_data, _skills.RealId(BlitzBeat, level))?.LogicData is not { Length: >= 2 } logic) return;
        if (Roll(10000) >= LogicValue(s, BlitzBeat, logic, 0) || _combat.ModeRow(s, (int)logic[1], level) is not { } row) return;
        var auto = new SkillConfig(row);
        var targets = Targets(s, BlitzBeatArea, auto.DescData, auto, target, []);
        await _combat.CastExtraAsync(s, row, targets);
        await RuleProcsAsync(s, action, auto, targets);
    }

    private bool FalconFollows(GameSession s) => MountService.Rider(s) != WolfMount
        && s.CareerPendants.Any(pendant => PendantConfig.Get(_data, pendant)?.Type == FalconPendantType);

    private async Task KatarMasteryAsync(GameSession s)
    {
        int level = _skills.Level(s.Progress!, KatarMastery);
        if (level <= 0) return;
        int id = _skills.RealId(KatarMastery, level);
        if (SkillConfig.Get(_data, id) is not { } passive || BuffConfig.Get(_data, id)?.LogicData is not { Length: >= 3 } logic) return;
        var desc = passive.DescData;
        if (Roll(10000) >= LogicValue(s, KatarMastery, logic, 1) || !Ready(s, KatarMastery, (long)(Value(desc, 0) * 1000))) return;
        for (int i = 0; i < (int)LogicValue(s, KatarMastery, logic, 2); i++)
            await _buffs.AddAsync(s, (int)logic[0], (int)(Value(desc, 2) * 1000), s.Role!.Id);
    }

    private async Task VenomAsync(GameSession s, MonsterUnit target)
    {
        if (target.Hp <= 0) return;
        long now = MonsterService.NowMs;
        foreach (var buff in s.Buffs)
        {
            if (buff.ExpireMs <= now || BuffConfig.Get(_data, buff.ConfigId) is not { BuffId: DeadlyPoison } row
                || row.LogicData is not { Length: >= 2 } logic) continue;
            if (Roll(10000) < LogicValue(s, DeadlyPoison, logic, 0) && ProcRow(logic[1]) is { } venom)
                await _combat.CastExtraAsync(s, venom.Row, [target]);
            return;
        }
    }

    /// <summary>A passive's _kLogicData value at <paramref name="index"/> after the talents' BuffLogicData tuples on its group.</summary>
    private static double LogicValue(GameSession s, long group, long[] logic, int index)
    {
        double value = index < logic.Length ? logic[index] : 0;
        foreach (var tuple in s.TalentBuffAttributes)
            if (!tuple.ByCategory && tuple.Group == group && tuple.Type == BuffLogicType && (tuple.Index == index || index == 0 && tuple.Index >= logic.Length))
                value = tuple.Operation switch
                {
                    1 => value + tuple.Value,
                    2 => tuple.Value,
                    5 => value * (1 + tuple.Value / 10000),
                    _ => value,
                };
        return value;
    }

    /// <summary>The talent's description values when the session's plan holds it.</summary>
    private static string[]? Talent(GameSession s, long buff)
    {
        foreach (var (owned, description) in s.TalentProcBuffs)
            if (owned == buff) return description;
        return null;
    }

    /// <summary>The triggering row with a described damage (SkillConfig.AsDescribedHit), once per (row, key).</summary>
    private JsonElement DescribedHit(SkillConfig trigger, long key, long ratio, long flat, long element, long targetMax = 0)
    {
        lock (_describedHits)
        {
            if (_describedHits.TryGetValue((trigger.Id, key), out var hit)) return hit;
            return _describedHits[(trigger.Id, key)] = trigger.AsDescribedHit(ratio, flat, element, targetMax);
        }
    }
}
