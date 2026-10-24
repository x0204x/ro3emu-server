using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The learned passives that act on events rather than stand as attributes (those are CombatService.Passives). Each reads
/// its own BuffConfig row (CombatService.OwnBuffs) and its description values, quoted per passive. The trigger is written
/// only in the description, as for the talents' procs (inferred). Double Attack, Deadly Poison's Venom and Advanced Katar
/// Mastery are in TalentProcs.Passives, next to the talents that change them; Supersonic Blow and Arcane Mastery change a
/// skill's damage in CombatService.Passives; the mount passives (Riding, Dragon Training, Wolf Taming, Wolf's Fang) are
/// RidingEffects; Sight Sprout's detection is CombatService.Stealth.
/// </summary>
public sealed class PassiveSkills : IGameTicker
{
    private const int DoubleHolyLight = 14311, Judex = 14309, EnchantBlade = 11313, EnergyCoat = 12003,
        EmergencyTreatment = 16303, SoulDrain = 12209, PreyOnTheWeak = 15316, PrayerOfGratitude = 14306,
        WoodSpiritsBlessing = 14307, Union = 11614, SoulExpansion = 12302, PsychokineticAstralBody = 12343, ElementControl = 12306;
    // Element Control: "When a Boss target resists your [Frozen], [Petrify], or [Stun], respectively deals Water/Earth/Wind
    // Ranged Magic damage equal to your MATK * ${2}% to the target." The control's BuffConfig category (Frozen 9, Petrify
    // 10, Stun 13) picks the Warlock's Ice Blast 1235301, Earth Blast 1235501 or Thunder Blast 1235401 row: water, earth
    // and wind, each _kDamageParam1 10800 (the ${2} 108).
    private static readonly Dictionary<long, int> ElementControlBlasts = new() { [9] = 1235301, [10] = 1235501, [13] = 1235401 };
    // Psychokinetic Astral Body's description links its bodies to BuffConfig 12361 (the row of its level).
    private const int AstralBodyBuff = 12361;
    private const int UnionBuff = 1162701; // BuffConfig "Union": "Each stack increases your Physical ATK, ..." (_iMaxStack 10)
    private const long Crusader = 1210; // JobConfig: the Crusader line (Paladin 1310, Royal Guard 1410) descends from it
    private const int AttrMaxSp = 102; // CharacterService stats: 101 Max HP, 102 Max SP, 2000 HP, 2001 SP
    private const int AttrMaxHp = 101, AttrHp = 2000, AttrSp = 2001;
    // "Permanent" for the Prayer of Gratitude shield: a day, renewed on every scene load (inferred).
    private const int PermanentMs = 86_400_000;

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly CharacterService _character;
    private readonly GameLoop _loop;
    private readonly SessionRegistry _sessions;
    private readonly TeamService _teams;
    private readonly ConditionalWeakTable<GameSession, State> _states = [];

    private sealed class State
    {
        public readonly Dictionary<int, long> ReadyAt = [];
        public long LastHitMs;
        public long NextAstralMs;
    }

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);
    private TimeSpan _lastTick;

    public PassiveSkills(GameData data, CombatService combat, BuffService buffs, CharacterService character, SceneService scenes,
        SkillService skills, GameLoop loop, SessionRegistry sessions, TeamService teams)
    {
        _sessions = sessions;
        _teams = teams;
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _character = character;
        _loop = loop;
        combat.CastResolved.Add(OnCastResolvedAsync);
        buffs.ControlResisted.Add(OnControlResistedAsync);
        combat.TakenAsync.Add(OnTakenAsync);
        combat.Killed.Add(OnKilledAsync);
        combat.DamageAdjusters.Add(PreyOnTheWeakDamage);
        buffs.PassiveLethalSave = WoodSpiritsBlessingAsync;
        // It touches only the loaded session's own buffs, inside the scene load's scope: no hook scope of its own.
        scenes.AddOwnedAfterLoaded(StandingBuffsAsync);
        skills.Learned.Add(StandingBuffsAsync);
        character.JobChanged.Add(StandingBuffsAsync);
        // A roster or member card change runs in the party's scope; each member's Union is set in a work item of its own.
        teams.RosterChanged.Add(roles =>
        {
            foreach (long role in roles)
                if (_sessions.All().FirstOrDefault(p => p.Role?.Id == role) is { } member)
                    loop.PostLegacy(() => UnionAsync(member), "union stacks");
            return Task.CompletedTask;
        });
    }

    private (SkillConfig Skill, BuffConfig? Own)? Learned(GameSession s, int group) =>
        _combat.LearnedPassive(s, group) is { } skill ? (skill, _combat.OwnBuff(skill)) : null;

    private static double Value(string[] values, int index) =>
        index < values.Length && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private bool Ready(GameSession s, int group, long cooldownMs)
    {
        var ready = _states.GetOrCreateValue(s).ReadyAt;
        long now = MonsterService.NowMs;
        if (ready.GetValueOrDefault(group) > now) return false;
        ready[group] = now + cooldownMs;
        return true;
    }

    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (s.Role == null || processed.Count == 0) return;
        var cast = new SkillConfig(action.Row);
        var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? processed[0];
        // Double Holy Light: "[Judex] cast by you has a ${1}% Chance to deal one Extra instance of Neutral ranged magic damage
        // ... and a ${1}% Chance to deal one Extra instance of Neutral ranged Physical damage ... (The two Chances are
        // calculated independently.)" _kLogicData [chance, row, chance, row] in basis points.
        if (cast.SkillId == Judex && Learned(s, DoubleHolyLight) is { Own: { LogicData: [var magicChance, var magic, var physicalChance, var physical, ..] } })
            foreach (var (chance, id) in new[] { (magicChance, magic), (physicalChance, physical) })
                if (target.Hp > 0 && Roll(10000) < chance && SkillConfig.Get(_data, id) is { } row)
                    await _combat.CastExtraAsync(s, row.Row, [target]);
        // Enchant Blade: "After dealing damage, increases your DEF by ${2}% for @{1} seconds." _kLogicData [the DEF buff,
        // whose _iTime is those seconds].
        if (hit.Dsts.Any(d => d.Damage > 0) && Learned(s, EnchantBlade) is { Own: { LogicData: [var defense, ..] } })
            await _buffs.AddAsync(s, (int)defense, 0, s.Role.Id);
        // Psychokinetic Astral Body: "If you already have ${4} [Psychokinetic Astral Bodies], hitting with Soul Expansion
        // Launches all [Psychokinetic Astral Bodies]. Each Deals Ghost Ranged Magic DMG equal to MATK * ${5}% to the target
        // enemy." Each casts the row the bodies' buff's _kLogicData[0] names (12345 Singularity of its level), in the
        // caster's mode.
        if (cast.SkillId == SoulExpansion && hit.Dsts.Any(d => d.Damage > 0) && AstralBodies(s) is { } bodies
            && s.Buffs.FirstOrDefault(b => b.ConfigId == bodies.Buff && b.ExpireMs > MonsterService.NowMs) is { } live
            && live.PileCount >= bodies.Launch && BuffConfig.Get(_data, bodies.Buff) is { LogicData: [var launch, ..] }
            && (_combat.EffectiveSkillRow(s, (int)launch) ?? SkillConfig.Get(_data, launch)?.Row) is { } body)
        {
            await _buffs.RemoveAsync(s, live);
            for (int i = 0; i < live.PileCount && target.Hp > 0; i++) await _combat.CastExtraAsync(s, body, [target]);
        }
    }

    private async Task OnControlResistedAsync(WorldUnit unit, long sourceId, JsonElement control)
    {
        if (unit is not MonsterUnit monster || monster.Hp <= 0
            || _sessions.All().FirstOrDefault(p => p.Role?.Id == sourceId) is not { Dead: false } s
            || Learned(s, ElementControl) is null) return;
        foreach (long category in new BuffConfig(control).BuffCategory)
            if (ElementControlBlasts.TryGetValue(category, out int blast) && SkillConfig.Get(_data, blast) is { } row)
            {
                await _combat.CastExtraAsync(s, row.Row, [monster]);
                return;
            }
    }

    /// <summary>Psychokinetic Astral Body's bodies buff row and its numbers: "Auto-generating ${2} [Psychokinetic Astral
    /// Bodies] every ${1} seconds, up to ${3}. If you already have ${4} ..." The passive's mode row (a talent's type 15)
    /// carries its faster ${1}.</summary>
    private (int Buff, long EveryMs, int Count, int Max, int Launch)? AstralBodies(GameSession s)
    {
        if (Learned(s, PsychokineticAstralBody) is not { Skill: var learned }) return null;
        var desc = (_combat.EffectiveSkillRow(s, (int)learned.Id) is { } row ? new SkillConfig(row) : learned).DescData;
        return ((int)(AstralBodyBuff * 100L + learned.Level), (long)(Value(desc, 0) * 1000), (int)Value(desc, 1),
            (int)Value(desc, 2), (int)Value(desc, 3));
    }

    /// <summary>Psychokinetic Astral Body generates its bodies every ${1} seconds, in and out of combat (inferred).</summary>
    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < TimeSpan.FromMilliseconds(100)) return;
        _lastTick = time.Now;
        long now = MonsterService.NowMs;
        foreach (var s in _sessions.All())
        {
            if (s.Role is not { } role || s.Dead || s.SceneLoading || AstralBodies(s) is not { EveryMs: > 0 } bodies) continue;
            var state = _states.GetOrCreateValue(s);
            if (state.NextAstralMs == 0) state.NextAstralMs = now + bodies.EveryMs;
            if (now < state.NextAstralMs) continue;
            // Keep the cadence: a body on time counts from when it was due, not from this tick.
            state.NextAstralMs = (now - state.NextAstralMs < 100 ? state.NextAstralMs : now) + bodies.EveryMs;
            _loop.PostOwned(s, async () =>
            {
                int held = s.Buffs.FirstOrDefault(b => b.ConfigId == bodies.Buff && b.ExpireMs > MonsterService.NowMs)?.PileCount ?? 0;
                if (held < bodies.Max && !s.Dead)
                    await _buffs.SetPileCountAsync(s, bodies.Buff, PermanentMs, role.Id, Math.Min(bodies.Max, held + bodies.Count));
            }, "astral bodies");
        }
    }

    private async Task OnTakenAsync(MonsterUnit attacker, GameSession s, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead) return;
        _states.GetOrCreateValue(s).LastHitMs = MonsterService.NowMs;
        // Energy Coat: "When you take damage, gain [Energy Coat] for ${1} sec., increasing your damage reduction by ${2}%.
        // Cooldown: ${3} sec." _kLogicData [the reduction buff, ms, piles].
        if (Learned(s, EnergyCoat) is { Skill: var coat, Own: { LogicData: [var buff, var ms, ..] } }
            && Ready(s, EnergyCoat, (long)(Value(coat.DescData, 2) * 1000)))
            await _buffs.AddAsync(s, (int)buff, (int)ms, s.Role.Id);
        // Emergency Treatment: "When you take damage while your HP is below ${1}%, grants a shield equal to ${2}% of your HP
        // and restores ${3}% of your Life. This effect can trigger up to @{1} times every ${4} sec." _kLogicData [the shield
        // buff, the healing buff]; the runtime count is read as one.
        if (Learned(s, EmergencyTreatment) is { Skill: var treatment, Own: { LogicData: [var shield, ..] } })
        {
            var stats = _character.Stats(s);
            var desc = treatment.DescData;
            if (stats[AttrHp] * 100 < stats[AttrMaxHp] * Value(desc, 0) && Ready(s, EmergencyTreatment, (long)(Value(desc, 3) * 1000)))
            {
                await _buffs.AddSupportAsync(s, (int)shield, 0, s.Role.Id, (long)(stats[AttrMaxHp] * Value(desc, 1) / 100));
                await _character.SetHpSpAsync(s, Math.Min(stats[AttrMaxHp], stats[AttrHp] + (long)(stats[AttrMaxHp] * Value(desc, 2) / 100)), null);
            }
        }
        // Prayer of Gratitude: "(Refreshes after taking no damage for ${5} sec.)"
        if (Learned(s, PrayerOfGratitude) is { Skill: var prayer })
        {
            var role = s.Role;
            long quiet = (long)(Value(prayer.DescData, 4) * 1000);
            _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(quiet + 50), async () =>
            {
                if (ReferenceEquals(s.Role, role) && !s.Dead && MonsterService.NowMs - _states.GetOrCreateValue(s).LastHitMs >= quiet)
                    await PrayerShieldAsync(s);
            });
        }
    }

    private async Task StandingBuffsAsync(GameSession s)
    {
        await PrayerShieldAsync(s);
        await UnionAsync(s);
    }

    /// <summary>
    /// Union: "Join forces with other Crusaders on your team. For each Crusader on the team, gain 1 stack of [Union]:
    /// Physical ATK, Physical DEF, and Magic DEF increase by ${1}. Stacks up to ${2} times." The other online members of the
    /// Crusader line count (inferred), as piles of the Union buff, which leaves with the last of them or the passive.
    /// </summary>
    private async Task UnionAsync(GameSession s)
    {
        if (s.Role is not { } role || s.Dead) return;
        int stacks = 0;
        if (Learned(s, Union) is { Skill: var union })
            stacks = Math.Min((int)Value(union.DescData, 1), _teams.MembersOf(role.Id).Count(id => id != role.Id
                && _sessions.All().FirstOrDefault(p => p.Role?.Id == id) is { Progress: { } progress } && IsCrusader(progress.Career)));
        var live = s.Buffs.FirstOrDefault(b => b.ConfigId == UnionBuff && b.ExpireMs > MonsterService.NowMs);
        if (stacks > 0 && live?.PileCount != stacks) await _buffs.SetPileCountAsync(s, UnionBuff, PermanentMs, role.Id, stacks);
        else if (stacks == 0 && live != null) await _buffs.RemoveAsync(s, live);
    }

    private bool IsCrusader(long career)
    {
        for (int guard = 0; career > 0 && guard < 8; guard++)
        {
            if (career == Crusader) return true;
            career = JobConfig.Get(_data, career)?.ParentId ?? 0;
        }
        return false;
    }

    /// <summary>Prayer of Gratitude: "... plus a shield equal to Max Life * ${4}%." Its own row is "Gain a permanent
    /// damage-absorption shield."; it is renewed on a scene load and after the quiet time, and leaves with the passive
    /// (a career change).</summary>
    private async Task PrayerShieldAsync(GameSession s)
    {
        if (s.Role == null || s.Dead) return;
        if (Learned(s, PrayerOfGratitude) is not { Skill: var prayer, Own: { } own })
        {
            foreach (var stale in s.Buffs.Where(b => BuffConfig.Get(_data, b.ConfigId)?.BuffId == PrayerOfGratitude).ToArray())
                await _buffs.RemoveAsync(s, stale);
            return;
        }
        long amount = (long)(_character.Stats(s)[AttrMaxHp] * Value(prayer.DescData, 3) / 100);
        if (s.Buffs.Any(b => b.ConfigId == own.Id && b.ShieldRemaining >= amount && b.ExpireMs > MonsterService.NowMs)) return;
        await _buffs.AddSupportAsync(s, (int)own.Id, PermanentMs, s.Role.Id, amount);
    }

    // Soul Drain: "Each Kill restores ${2}% of Max MP as MP."
    private async Task OnKilledAsync(GameSession s, MonsterUnit monster)
    {
        if (s.Role == null || Learned(s, SoulDrain) is not { Skill: var drain }) return;
        var stats = _character.Stats(s);
        long sp = Math.Min(stats[AttrMaxSp], stats[AttrSp] + (long)Math.Round(stats[AttrMaxSp] * Value(drain.DescData, 1) / 100));
        if (sp > stats[AttrSp]) await _character.SetHpSpAsync(s, null, sp);
    }

    // Prey on the Weak: "Damage dealt to enemies afflicted with status effects is increased by ${1}%." A status effect is
    // any negative buff on the monster (BuffConfig._iType 2) (inferred).
    private long PreyOnTheWeakDamage(GameSession s, MonsterUnit target, System.Text.Json.JsonElement row, DamageHitResult result, long damage) =>
        _buffs.UnitHasDebuff(target) && Learned(s, PreyOnTheWeak) is { Skill: var prey }
            ? (long)Math.Round(damage * (1 + Value(prey.DescData, 0) / 100)) : damage;

    /// <summary>
    /// Wood Spirit's Blessing: "when you take lethal damage, you gain one chance to [Respawn]. When triggered, you recover
    /// ${2}% Life and enter the [Withered] state for ${3} minutes. During this time, [Respawn] cannot trigger again." The
    /// own row's _kLogicData[0] (240000 ms) is the Withered time.
    /// </summary>
    private async Task<bool> WoodSpiritsBlessingAsync(GameSession s)
    {
        if (Learned(s, WoodSpiritsBlessing) is not { Skill: var blessing, Own: { LogicData: [var withered, ..] } }
            || !Ready(s, WoodSpiritsBlessing, withered)) return false;
        var stats = _character.Stats(s);
        await _character.SetHpSpAsync(s, Math.Max(1, (long)(stats[AttrMaxHp] * Value(blessing.DescData, 1) / 100)), null);
        return true;
    }
}
