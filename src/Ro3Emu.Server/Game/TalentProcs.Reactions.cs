using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Talents that react to something other than the owner's cast resolving: the count of enemies a cast hits, damage taken,
/// kills, Gravitation Field's end, and the allies a heal reaches.
/// </summary>
public sealed partial class TalentProcs
{
    private const int LordOfVermilion = 12107, NapalmVulcan = 12210, SoulDrain = 12209, Heal = 14007, Sanctuary = 14115;
    private const long Immobilize = 902401; // BuffConfig "Immobilize"
    private const long AttrMatk = 1043; // AttributeConfig "MATK" (flat)

    /// <summary>
    /// "For every @{1} enemies hit by [Hammer Fall], the percentage damage multiplier increases by ${1}%, up to ${2}%"
    /// (Earthshatter) adds to the ratio; "For each enemy hit by [Lord of Vermilion], its DMG Bonus increases by ${1}%, up to
    /// ${2}%" (Thunder Shockwave; Greater Psychic Shockwave on Napalm Vulcan) multiplies the hit (inferred from the
    /// wording). "@{1}" is read as one.
    /// </summary>
    private static readonly Dictionary<long, (int Group, bool Multiplier)> PerEnemyHit = new()
    {
        [1642301] = (HammerFall, false), // Earthshatter
        [1245301] = (LordOfVermilion, true), // Thunder Shockwave
        [1246301] = (NapalmVulcan, true), // Greater Psychic Shockwave
    };

    // Resilient Spirit: "While [Auto Guard] is active, taking damage increases your DMG Reduction by ${1}% for ${2} seconds.
    // Internal Cooldown: ${3} seconds." _kLogicData [1154201 (DMG Reduction% +5), 3000]
    private const long ResilientSpirit = 1154101;
    // Soul Reaper: "When [Soul Drain] scores a Kill, gain ${1} MATK, up to ${2}. The amount gained is doubled when you Kill
    // Players. Resets upon death, switching Locations, or going offline."
    private const long SoulReaper = 1247201;
    // Gravity Trap: "When [Gravitation Field] ends, inflicts [Immobilize] on enemies in the area for ${1} seconds."
    private const long GravityTrapTalent = 1248301;
    // Sacred Divine Light: "[Heal] restores an Extra MATK*${2}%+${3} HP to targets below ${1}% HP." _kLogicData [80, 1421601]
    private const long SacredDivineLight = 1421501;
    // Holy Healing Light: "[Heal] has a ${1}% Chance to Splash to ${3} Other allies within ${2} meters of the target,
    // restoring the same Healing Volume as the skill itself. Internal Cooldown: ${4} seconds."
    private const long HolyHealingLight = 1423801;
    // Purifying Light Orbit: "When [Sanctuary] is cast, it restores MATK*${2}%+${3} HP to up to ${1} allies within the area."
    // _kLogicData [10000, 1421501]
    private const long PurifyingLightOrbit = 1422101;
    // Radiant Sanctuary: "After [Sanctuary] has been active for a time, it restores MATK*${3}%+${4} HP to up to ${2} allies
    // within ${1} meters of you." _kLogicData [1424101 (Sanctuary [Triggered Skill]: _kLogicData [10000, 1421101]), 3000]
    private const long RadiantSanctuary = 1423901;

    private sealed class Reaper
    {
        public double Matk;
        public long SceneGeneration;
    }

    private readonly ConditionalWeakTable<GameSession, Reaper> _reapers = [];

    private static JsonElement TargetCountRow(GameSession s, JsonElement row, int count)
    {
        var skill = new SkillConfig(row);
        // Holy Cross Exorciser's mode row: "When [Magnus Exorcismus] hits a single enemy" its damage pairs 2 and 3 apply
        // (Magnus_ExorcismusBD [500,650,1000,1300] / [24,24,110,110]).
        if (count == 1 && skill.ScriptLogic == "Magnus_ExorcismusBD" && skill.DamageParam1 is [_, _, var ratio, var ratio2]
            && skill.DamageParam2 is [_, _, var flat, var flat2])
            return skill.WithDamage([ratio, ratio2], [flat, flat2]);
        if (s.TalentProcBuffs.Count == 0) return row;
        foreach (var (buff, description) in s.TalentProcBuffs)
        {
            if (!PerEnemyHit.TryGetValue(buff, out var rule) || rule.Group != skill.SkillId) continue;
            double bonus = Math.Min(Value(description, 0) * count, Value(description, 1));
            if (bonus <= 0) return row;
            return rule.Multiplier
                ? skill.WithDamage([.. skill.DamageParam1.Select(v => (long)Math.Round(v * (1 + bonus / 100)))],
                    [.. skill.DamageParam2.Select(v => (long)Math.Round(v * (1 + bonus / 100)))])
                : skill.WithDamage([.. skill.DamageParam1.Select(v => v + (long)Math.Round(bonus * 100))], skill.DamageParam2);
        }
        return row;
    }

    private async Task OnTakenAsync(MonsterUnit attacker, GameSession s, long damage)
    {
        if (damage <= 0 || s.Role == null || Talent(s, ResilientSpirit) is not { } description
            || !SkillParameterRules.HasBuffGroup(s, _data, AutoGuard)
            || BuffConfig.Get(_data, ResilientSpirit)?.LogicData is not [var buff, var ms, ..]
            || !Ready(s, ResilientSpirit, (long)(Value(description, 2) * 1000))) return;
        await _buffs.AddAsync(s, (int)buff, (int)ms, s.Role.Id);
    }

    private Task OnKilledAsync(GameSession s, MonsterUnit monster) => ReapAsync(s, 1);

    private Task OnPlayerKilledAsync(GameSession killer, GameSession victim) => ReapAsync(killer, 2);

    private async Task ReapAsync(GameSession s, int times)
    {
        if (s.Progress == null || Talent(s, SoulReaper) is not { } description || _skills.Level(s.Progress, SoulDrain) <= 0) return;
        var reaper = _reapers.GetOrCreateValue(s);
        if (reaper.SceneGeneration != s.SceneGeneration) reaper.Matk = 0;
        reaper.SceneGeneration = s.SceneGeneration;
        double next = Math.Min(Value(description, 1), reaper.Matk + Value(description, 0) * times);
        if (next == reaper.Matk) return;
        reaper.Matk = next;
        await _character.RefreshStatsAsync(s);
    }

    private async Task OnDiedAsync(GameSession s)
    {
        if (!_reapers.TryGetValue(s, out var reaper) || reaper.Matk == 0) return;
        reaper.Matk = 0;
        await _character.RefreshStatsAsync(s);
    }

    private IEnumerable<(long Id, double Value)> SoulReaperAttributes(GameSession s)
    {
        if (_reapers.TryGetValue(s, out var reaper) && reaper.Matk > 0 && reaper.SceneGeneration == s.SceneGeneration
            && Talent(s, SoulReaper) != null)
            yield return (AttrMatk, reaper.Matk);
    }

    /// <summary>Gravitation Field resolves at once here (it leaves no field NPC), so it "ends" its _kLogicData[1] (3900 ms)
    /// after the cast; the Immobilize then lands on the monsters within its _kRange of the target point (inferred).</summary>
    private void GravityTrap(GameSession s, ActiveSkillCast action, SkillConfig cast)
    {
        if (cast.SkillId != GravitationField || Talent(s, GravityTrapTalent) is not { } description
            || cast.LogicData is not [_, var ms, ..] || s.Role is not { } role) return;
        long scene = s.SceneGeneration;
        var center = (action.Request.TargetPos ?? s.Position).Clone();
        float radius = (cast.Range is [var range, ..] ? range : 0) / 100f;
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(ms), async () =>
        {
            if (!ReferenceEquals(s.Role, role) || s.SceneGeneration != scene) return;
            foreach (var monster in _combat.MonstersNear(s, center, radius))
                await _buffs.ShowOnUnitAsync(monster, (int)Immobilize, (int)(Value(description, 0) * 1000), role.Id);
        });
    }

    /// <summary>After a heal or Sanctuary of the owner: <paramref name="hit"/> lists the allies it healed.</summary>
    private async Task HealProcsAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, SkillHitInfo hit)
    {
        int group = (int)cast.SkillId;
        if (group == Heal)
        {
            var healed = new List<(GameSession Ally, long Amount)>();
            foreach (var dst in hit.Dsts)
                if (dst.DamageType == DamageType.Heal && _sessions.All().FirstOrDefault(p => p.Role?.Id == dst.DstID) is { } ally)
                    healed.Add((ally, dst.Damage));
            if (healed.Count == 0) return;
            if (Talent(s, SacredDivineLight) != null && BuffConfig.Get(_data, SacredDivineLight)?.LogicData is [var below, var extra, ..]
                && ProcRow(extra) is { } extraHeal)
                foreach (var (ally, amount) in healed)
                {
                    var stats = _character.Stats(ally);
                    if ((stats[2000] - amount) * 100 < stats[101] * below) await _combat.CastExtraOnAlliesAsync(s, extraHeal.Row, [ally]);
                }
            if (Talent(s, HolyHealingLight) is { } splash && Roll(10000) < Value(splash, 0) * 100
                && Ready(s, HolyHealingLight, (long)(Value(splash, 3) * 1000)))
            {
                var first = healed[0].Ally;
                var others = _combat.AlliesNear(s, first.Position, (float)Value(splash, 1))
                    .Where(p => !healed.Any(h => ReferenceEquals(h.Ally, p))).Take((int)Value(splash, 2)).ToList();
                if (others.Count > 0) await _combat.CastExtraOnAlliesAsync(s, action.Row, others);
            }
            return;
        }
        if (group != Sanctuary) return;
        var center = action.Request.TargetPos ?? s.Position;
        if (Talent(s, PurifyingLightOrbit) is { } orbit && BuffConfig.Get(_data, PurifyingLightOrbit)?.LogicData is [_, var orbitRow, ..]
            && ProcRow(orbitRow) is { } orbitHeal)
        {
            var allies = _combat.AlliesNear(s, center, (cast.Range is [var range, ..] ? range : 0) / 100f).Take((int)Value(orbit, 0)).ToList();
            if (allies.Count > 0) await _combat.CastExtraOnAlliesAsync(s, orbitHeal.Row, allies);
        }
        if (Talent(s, RadiantSanctuary) is { } radiant && BuffConfig.Get(_data, RadiantSanctuary)?.LogicData is [var triggered, var delay, ..]
            && BuffConfig.Get(_data, triggered)?.LogicData is [_, var radiantRow, ..] && ProcRow(radiantRow) is { } radiantHeal)
        {
            var role = s.Role;
            long scene = s.SceneGeneration;
            _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(delay), async () =>
            {
                if (!ReferenceEquals(s.Role, role) || s.Dead || s.SceneGeneration != scene) return;
                var allies = _combat.AlliesNear(s, s.Position, (float)Value(radiant, 0)).Take((int)Value(radiant, 1)).ToList();
                if (allies.Count > 0) await _combat.CastExtraOnAlliesAsync(s, radiantHeal.Row, allies);
            });
        }
    }
}
