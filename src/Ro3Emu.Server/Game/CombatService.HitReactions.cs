using Romsg;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a class skill's buff does when its holder is hit. Each buff names the row it fires in _kLogicData, a row id or a group
/// taken at the buff's level: Parry 1121001 [1121101] "casts [Auto Counter] in front of You", Shield Reflect 1140401 [11419],
/// Reflect Damage 1161001 [11618, ...], Shrink 1140901 [11420], Death Bound 1131101 [1138101]. No client code reads these, so the
/// rules come from each skill's text; the internal cooldowns are the text's, and a monster's or a player's hit reaches here alike.
/// </summary>
public sealed partial class CombatService
{
    private const int ParryGroup = 11210, ShieldReflectGroup = 11404, ReflectDamageGroup = 11610, ShrinkGroup = 11409, DeathBoundGroup = 11311;
    private const int AutoCounterGroup = 11105, AutoGuardGroup = 11403, DevotionGroup = 11504;
    private const int ReflectGrantsStancePercent = 50;
    private const long ParryCooldownMs = 1000, ReflectDamageCooldownMs = 500, ShrinkCooldownMs = 3000, DeathBoundCooldownMs = 200;
    private const long ShrinkSkill = 1141600, DeathBoundSkill = 1131100, AutoCounterSkill = 1110500;

    private readonly ConditionalWeakTable<GameSession, Dictionary<int, long>> _reactionReady = [];
    private readonly ConditionalWeakTable<GameSession, StrongBox<bool>> _parried = [];

    private bool ReactionReady(GameSession p, int group, long cooldownMs)
    {
        var ready = _reactionReady.GetOrCreateValue(p);
        if (Now < ready.GetValueOrDefault(group)) return false;
        ready[group] = Now + cooldownMs;
        return true;
    }

    /// <summary>The row a buff's _kLogicData entry names: a row id as it stands, a group at the buff's level.</summary>
    private SkillConfig? ReactionRow(ActiveBuff buff, long named) =>
        SkillConfig.Get(_data, named >= 1_000_000 ? named : named * 100 + Math.Max(1, buff.ConfigId % 100));

    private static float FaceTo(Vec3 from, Vec3 to) => (float)(Math.Atan2(to.X - from.X, to.Z - from.Z) * 180 / Math.PI);

    /// <summary>A card's answer to a hit on its holder: the holder, the player who struck (null for a monster), the row, what it took (CardEffects).</summary>
    internal List<Func<GameSession, GameSession?, JsonElement?, long, Task>> CardHitReactions { get; } = [];

    /// <summary>A fixed hit from the holder on the player who struck, after the hit in hand has gone out. Like every hit on a player it may
    /// kill, and it answers only while the holder can still fight that player.</summary>
    internal void StrikeBackLater(GameSession holder, SkillConfig row, long damage, GameSession enemy) => _loop.PostRuntime(() =>
        !CanPlayerHit(holder, enemy) ? Task.CompletedTask
            : CastExtraOnPlayerAsync(holder, new SkillConfig(row.AsFixedHit(damage)), enemy), "card reflect");

    /// <summary>After a hit landed on a player. <paramref name="row"/> is the attacker's row, null when it has none.</summary>
    internal async Task ReactToHitAsync(GameSession p, MonsterUnit? monster, GameSession? enemy, JsonElement? row, long damage)
    {
        if (damage <= 0 || p.Dead || p.Role == null || (monster == null && enemy == null)) return;
        var attacker = monster?.Pos ?? enemy!.Position;
        // Melee Physical: _iDmgType 2 is magic, _iDistanceType 2 ranged (as ApplyPetSupportResistance reads them).
        bool melee = row is not { } hit || (new SkillConfig(hit).DmgType != 2 && new SkillConfig(hit).DistanceType != 2);
        foreach (var buff in p.Buffs.Where(b => b.ExpireMs > Now).ToArray())
        {
            int group = buff.ConfigId / 100;
            if (group is not (ParryGroup or ShieldReflectGroup or ReflectDamageGroup or ShrinkGroup or DeathBoundGroup or AutoCounterGroup)) continue;
            if (group == AutoCounterGroup)
            {
                // "If you take melee Physical damage while parrying, Auto Counter damage increases".
                if (melee) _parried.GetOrCreateValue(p).Value = true;
                continue;
            }
            if (BuffConfig.Get(_data, buff.ConfigId)?.LogicData is not [var named, ..] || ReactionRow(buff, named) is not { } counter) continue;
            float distance = Distance(p.Position, attacker);
            switch (group)
            {
                case ParryGroup when melee && ReactionReady(p, group, ParryCooldownMs):
                    {
                        // "casts [Auto Counter] in front of You": its area, turned to whoever struck.
                        float face = FaceTo(p.Position, attacker);
                        var inFront = MonstersNear(p, p.Position, counter.DistanceMax / 100f)
                            .Where(m => SkillAreaContains(counter.Row, p.Position, p.Position, face, m.Pos))
                            .Take((int)Math.Max(1, counter.TargetMax)).ToList();
                        await StrikeBackAsync(p, counter, inFront, enemy);
                        break;
                    }
                // "When you take Melee Physical DMG from an attacker within ${5} meters ... up to 1 times per second" (its row's _iCD).
                case ShieldReflectGroup when melee && distance <= counter.DistanceMax / 100f && ReactionReady(p, group, Math.Max(1, counter.Cd)):
                    await StrikeBackAsync(p, counter, monster != null ? [monster] : [], enemy);
                    break;
                // "When you are hit, deals ... to up to ${3} enemies within a ${4}-meter radius ... up to 1 times every 0.5 seconds."
                case ReflectDamageGroup when ReactionReady(p, group, ReflectDamageCooldownMs):
                    await StrikeBackAsync(p, counter, [.. MonstersNear(p, p.Position, counter.DistanceMax / 100f).Take((int)Math.Max(1, counter.TargetMax))], enemy);
                    if (Rolls.Proc(100) < ReflectGrantsStancePercent) await _buffs.AddShieldStanceAsync(p); // Powerful Shield Bash: "[Reflect Damage] ... 50% Chance ... 1 stack of [Shield Stance]"
                    break;
                // "While [Auto Guard] is active, taking damage from an attacker within ${5} meters has a ${2}% Chance to inflict [Stun]".
                case ShrinkGroup when p.Buffs.Any(b => b.ExpireMs > Now && b.ConfigId / 100 == AutoGuardGroup)
                        && SkillConfig.Get(_data, ShrinkSkill + Math.Max(1, buff.ConfigId % 100)) is { DescData: [_, var chance, _, _, var metres, ..] }
                        && distance <= ParseDesc(metres) && Rolls.Proc(100) < ParseDesc(chance) && ReactionReady(p, group, ShrinkCooldownMs):
                    await StrikeBackAsync(p, counter, monster != null ? [monster] : [], enemy);
                    break;
                // "taking melee damage from an enemy player reflects ... ${4}% of the damage taken to up to ${3} enemies within a ${2}-meter radius".
                case DeathBoundGroup when melee && enemy != null && ReactionReady(p, group, DeathBoundCooldownMs)
                        && SkillConfig.Get(_data, DeathBoundSkill + Math.Max(1, buff.ConfigId % 100)) is { DescData: [_, var radius, var count, var share, ..] }:
                    {
                        var fixedHit = new SkillConfig(counter.AsFixedHit(Math.Max(1, (long)(damage * ParseDesc(share) / 100))));
                        await StrikeBackAsync(p, fixedHit, [.. MonstersNear(p, p.Position, (float)ParseDesc(radius)).Take(Math.Max(0, (int)ParseDesc(count) - 1))], enemy);
                        break;
                    }
            }
        }
        foreach (var react in CardHitReactions) await react(p, enemy, row, damage);
    }

    private static double ParseDesc(string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;

    private async Task StrikeBackAsync(GameSession p, SkillConfig row, List<MonsterUnit> monsters, GameSession? enemy)
    {
        if (monsters.Count > 0) await CastExtraAsync(p, row.Row, monsters);
        if (enemy != null && CanPlayerHit(p, enemy)) await CastExtraOnPlayerAsync(p, row, enemy);
    }

    /// <summary>Auto Counter 1110501: "enter a Parry state for ${1} sec. ... When Parry ends, perform an Auto Counter ... If you take melee
    /// Physical damage while parrying, Auto Counter damage increases by ${4}%". The strike is its buff's _kLogicData row.</summary>
    private Task AutoCounterOnCastAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, Romsg.SkillHitInfo hit)
    {
        if (Script(action.Row) != "Counter_Attack" || new SkillConfig(action.Row) is not { DescData: [var seconds, _, _, var bonus, ..] } cast) return Task.CompletedTask;
        var flag = _parried.GetOrCreateValue(s);
        flag.Value = false;
        long generation = s.SceneGeneration;
        var role = s.Role;
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(ParseDesc(seconds)), async () =>
        {
            if (!ReferenceEquals(s.Role, role) || s.Dead || s.SceneLoading || s.SceneGeneration != generation) return;
            if (SkillConfig.Get(_data, AutoCounterStrike + Math.Max(1, cast.Level)) is not { } strike) return;
            var targets = MonstersNear(s, s.Position, strike.DistanceMax / 100f)
                .Where(m => SkillAreaContains(strike.Row, s.Position, s.Position, s.Face, m.Pos)).Take((int)Math.Max(1, strike.TargetMax)).ToList();
            if (targets.Count == 0) return;
            double factor = flag.Value ? 1 + ParseDesc(bonus) / 100 : 1;
            var row = factor == 1 ? strike : new SkillConfig(strike.WithDamage([.. strike.DamageParam1.Select(v => (long)(v * factor))], strike.DamageParam2));
            await CastExtraAsync(s, row.Row, targets);
        }, "auto counter");
        return Task.CompletedTask;
    }

    private const long AutoCounterStrike = 1110600;

    /// <summary>Devotion 1141301: "you take ${2}% of the damage they receive. If the Distance between you exceeds ${4} meters ... the effect ends."
    /// Its buff 1150401 holds the share in _kLogicData. Returns what the holder still takes; the guardian takes the rest through the usual HP path.</summary>
    private long ShareDevotedDamage(GameSession target, long damage)
    {
        if (damage <= 0) return damage;
        foreach (var buff in target.Buffs.Where(b => b.ExpireMs > Now && b.ConfigId / 100 == DevotionGroup).ToArray())
        {
            if (buff.SourceId == target.Role?.Id || BuffConfig.Get(_data, buff.ConfigId)?.LogicData is not [var share, ..]
                || FindPlayer(target, buff.SourceId) is not { Dead: false } guardian) continue;
            if (Distance(guardian.Position, target.Position) > DevotionRange)
            {
                _loop.TrackRuntimeDetached("devotion ended", () => _buffs.RemoveAsync(target, buff));
                continue;
            }
            long taken = Math.Min(damage, damage * share / 100);
            if (taken <= 0) continue;
            damage -= taken;
            _loop.TrackRuntimeDetached("devotion share", () => EnvironmentDamageAsync(guardian, taken));
            break;
        }
        return damage;
    }

    private const float DevotionRange = 10; // 1141301 _kDescData[3], the same at every level
}
