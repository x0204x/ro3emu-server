using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Talents that count casts or arm the next one ("After every ${1} casts of [Smite], the next [Smite] ..."). A charge holds
/// the _kLogicData buff (its own tuples carry the bonus) until a charged cast uses it up; when that buff carries nothing,
/// the description's "percentage damage multiplier increased by ${2}% and fixed damage increased by ${3}" become skill
/// tuples [group, 29, add, ${2} * 100] and [group, 30, add, ${3}] like Premeditation's own (inferred). A cast that uses a
/// counted charge does not count toward the next one. "@{1}" counts are runtime values, read as one.
/// </summary>
public sealed partial class TalentProcs
{
    private const int SpiralPierce = 11213, MagicCrasher = 12201, HeavensDrive = 12109, Disguise = 15112, EnvenomedBlade = 15105;
    private const long VenomSting = 1523001; // SkillConfig "Venom Sting", Deadly Poison's _kLogicData[1]
    private const int EmberZoneNpc = 1200021; // NPCConfig "Ember Zone-BD"

    /// <summary>
    /// A charge rule. <paramref name="Counted"/>: the casts that count, <paramref name="Every"/> of them (a description index;
    /// null = one) charge it; <paramref name="Timed"/>: one cast charges it for _kLogicData[1] ms. The next
    /// <paramref name="Uses"/> (a description index; null = one) casts of the charged groups use it: the counted ones, or
    /// with <paramref name="Timed"/> the groups the buff's tuples name. <paramref name="Ratio"/>/<paramref name="Flat"/>:
    /// description indexes of the bonus when the buff carries none.
    /// </summary>
    private sealed record Charge(int[] Counted, int? Every = null, bool Timed = false, int? Uses = null, int? Ratio = null, int? Flat = null);

    private static readonly Dictionary<long, Charge> Charges = new()
    {
        // Charged Shield Bash: "After every ${1} casts of [Smite], the next [Smite]'s percentage damage multiplier and
        // fixed damage increase by ${2}%." _kLogicData [1153301 ([11409, 29/30, x, 1500]), its group 11533]
        [1153201] = new([Smite], Every: 0),
        // Spiral Pierce Mastery: "After casting [Spiral Pierce] @{1} times, your next [Spiral Pierce] is guaranteed to
        // CRIT." _kLogicData [1123201 ([11213, 10, add, 10000])]
        [1123101] = new([SpiralPierce]),
        // Advanced Soul Destroyer: "After every ${1} casts of [Soul Destroyer], the next [Soul Destroyer] becomes [Advanced
        // Soul Destroyer], with its percentage damage multiplier increased by ${2}% and fixed damage increased by ${3}."
        [1524701] = new([SoulDestroyer], Every: 0, Ratio: 1, Flat: 2),
        // Magic Cannon: "After every ${1} casts of [Magic Crasher], the skill multiplier of the next @{1} casts of [Magic
        // Crasher] increases by ${2}%, and their fixed damage increases by ${3}."
        [1246201] = new([MagicCrasher], Every: 0, Ratio: 1, Flat: 2),
        // Premeditation: "For ${1} seconds after casting [Grimtooth], the percentage damage multiplier of your next ${2}
        // casts of [Basic Attack], [Sonic Blow], or [Envenomed Blade] increases by ${3}% ..." _kLogicData [1525501, 10000]
        [1523301] = new([Grimtooth], Timed: true, Uses: 1),
    };

    // Dagger Throw: "After casting [Envenomed Blade] ${1} times, triggers [Venom Sting] damage ${2} times on the target."
    private const long DaggerThrow = 1522901;
    // Sneak Attack: "For ${1} seconds after casting [Disguise], your next attack Deals DMG to the enemy an Extra ${2} times,
    // each dealing PATK * ${3}% + ${4} as Neutral Melee Physical damage." (_kLogicData [its marker buff, 10000])
    private const long SneakAttack = 1523801;
    // Ember Zone: "After casting [Heaven's Drive], the next [Meteor Storm] cast within ${1} sec. creates an [Ember Zone]
    // lasting ${2} sec. Every ${3} seconds, it deals Fire Ranged Magic damage equal to MATK*${5}%+${6} to up to ${4}
    // enemies within the area."
    private const long EmberZone = 1244301;

    private sealed class ChargeState
    {
        public readonly Dictionary<long, int> Casts = [];
        public readonly Dictionary<long, int> Left = [];
        public readonly Dictionary<long, SkillParameterRules.Modifier[]> Bonuses = [];
        public long SneakUntil, EmberUntil;
    }

    private readonly ConditionalWeakTable<GameSession, ChargeState> _charges = [];

    private async Task ChargesAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed)
    {
        var state = _charges.GetOrCreateValue(s);
        int group = (int)cast.SkillId;
        long now = MonsterService.NowMs;
        var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? (processed.Count > 0 ? processed[0] : null);
        foreach (var (buff, description) in s.TalentProcBuffs)
        {
            if (Charges.TryGetValue(buff, out var charge)) await ChargeAsync(s, state, buff, charge, description, group);
            else if (buff == DaggerThrow && group == EnvenomedBlade && target != null)
            {
                int casts = state.Casts.GetValueOrDefault(buff) + 1;
                state.Casts[buff] = casts < Math.Max(1, (int)Value(description, 0)) ? casts : 0;
                if (casts < Math.Max(1, (int)Value(description, 0)) || ProcRow(VenomSting) is not { } venom) continue;
                for (int i = 0; i < Math.Max(1, (int)Value(description, 1)) && target.Hp > 0; i++)
                    await _combat.CastExtraAsync(s, venom.Row, [target]);
            }
            else if (buff == SneakAttack)
            {
                if (group == Disguise && BuffConfig.Get(_data, buff)?.LogicData is [_, var ms, ..]) state.SneakUntil = now + ms;
                else if (state.SneakUntil > now && target is { Hp: > 0 } && cast.DamageParam1.Any(v => v > 0)
                    && SkillConfig.Get(_data, cast.Id) is { } trigger)
                {
                    state.SneakUntil = 0;
                    var hit = DescribedHit(trigger, buff, (long)(Value(description, 2) * 100), (long)Value(description, 3), Neutral);
                    for (int i = 0; i < Math.Max(1, (int)Value(description, 1)) && target.Hp > 0; i++)
                        await _combat.CastExtraAsync(s, hit, [target]);
                }
            }
            else if (buff == EmberZone)
            {
                if (group == HeavensDrive) state.EmberUntil = now + (long)(Value(description, 0) * 1000);
                else if (group == MeteorStorm && state.EmberUntil >= now && SkillConfig.Get(_data, cast.Id) is { } meteor)
                {
                    state.EmberUntil = 0;
                    var zone = DescribedHit(meteor, buff, (long)(Value(description, 4) * 100), (long)Value(description, 5), Fire,
                        (long)Value(description, 3));
                    await _combat.CreateZoneAsync(s, zone, EmberZoneNpc, action.Request.TargetPos ?? s.Position,
                        (int)(Value(description, 2) * 1000), (int)(Value(description, 1) * 1000));
                }
            }
        }
    }

    private async Task ChargeAsync(GameSession s, ChargeState state, long buff, Charge charge, string[] description, int group)
    {
        var logic = BuffConfig.Get(_data, buff)?.LogicData ?? [];
        var granted = logic is [var id, ..] ? BuffConfig.Get(_data, id) : null;
        if (state.Left.TryGetValue(buff, out int left))
        {
            // A timed charge ends with its buff.
            bool expired = charge.Timed && granted is { } live && !SkillParameterRules.HasBuffGroup(s, _data, live.BuffId);
            bool used = !expired && Charged(charge, granted).Contains(group);
            if (used) left--;
            if (expired || left <= 0) await EndChargeAsync(s, state, buff, granted);
            else state.Left[buff] = left;
            if (used && !charge.Timed) return;
        }
        if (Array.IndexOf(charge.Counted, group) < 0) return;
        int casts = state.Casts.GetValueOrDefault(buff) + 1;
        int every = charge.Every is int e ? Math.Max(1, (int)Value(description, e)) : 1;
        if (casts < every)
        {
            state.Casts[buff] = casts;
            return;
        }
        state.Casts[buff] = 0;
        state.Left[buff] = charge.Uses is int uses ? Math.Max(1, (int)Value(description, uses)) : 1;
        if (charge.Ratio is int ratio)
        {
            state.Bonuses[buff] = [.. charge.Counted.SelectMany(counted => new[]
            {
                new SkillParameterRules.Modifier(counted, 29, 1, (long)(Value(description, ratio) * 100), -1),
                new SkillParameterRules.Modifier(counted, 30, 1, charge.Flat is int flat ? (long)Value(description, flat) : 0, -1),
            })];
            s.TalentChargeModifiers = [.. state.Bonuses.Values.SelectMany(m => m)];
        }
        else if (granted is { } row)
            await _buffs.AddAsync(s, (int)row.Id, charge.Timed && logic.Length > 1 ? (int)logic[1] : 0, s.Role!.Id);
    }

    /// <summary>The groups whose casts use a charge: the counted ones, or a timed buff's tuple groups.</summary>
    private int[] Charged(Charge charge, BuffConfig? granted) => charge.Timed && granted is { } row
        ? [.. SkillParameterRules.BuffModifiers(_data, (int)row.Id).Where(m => m.Scope == 0).Select(m => m.Group).Distinct()]
        : charge.Counted;

    private async Task EndChargeAsync(GameSession s, ChargeState state, long buff, BuffConfig? granted)
    {
        state.Left.Remove(buff);
        if (state.Bonuses.Remove(buff)) s.TalentChargeModifiers = [.. state.Bonuses.Values.SelectMany(m => m)];
        else if (granted is { } row)
            foreach (var live in s.Buffs.Where(b => BuffConfig.Get(_data, b.ConfigId)?.BuffId == row.BuffId).ToArray())
                await _buffs.RemoveAsync(s, live);
    }
}
