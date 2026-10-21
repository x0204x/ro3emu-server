using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>AttributeConfig per_dmg1 and the bonus of the hit's element (none_ele..undead_ele).</summary>
    private static bool RaisesDamage(long attribute, int element) => attribute == AttributeId.PerDmg1
        || element is >= 1 and <= AttributeId.ElementCount && attribute == AttributeId.NoneEle + element - 1;

    private static long GuardianOutgoingDamage(GameSession source, JsonElement row, long damage)
    {
        int element = (int)Math.Clamp(GameData.Int(row, "_iEleType"), 1, AttributeId.ElementCount);
        double bonus = source.Buffs.Where(b => b.IsGuardianOwned && b.ExpireMs > Now
                && (b.ConfigId / 100 != GuardianGroup.WildcatCharge || !IsGuardianEffect(row) && !row.TryGetProperty("_ro3GuardianFixedHit", out _)))
            .SelectMany(b => b.Changes).Where(c => RaisesDamage(c.Attr, element)).Sum(c => c.Value);
        return damage <= 0 ? 0 : (long)Math.Clamp(Math.Floor(damage * Math.Max(0, 1 + bonus / 10000)), 0, long.MaxValue);
    }

    /// <summary>PetStarConfig._iPassiveMain + its localized description. (inferred) One reaction after the primary
    /// guardian effect resolves; live lineup, category matching, shared healing modifiers, no unproven internal CD.
    /// Bobo's HP threshold is sampled after the shield; description duration overrides the conflicting candidate tuple.</summary>
    private async Task GuardianReactionsAsync(GameSession s, GuardianPetRules.Skill cast, ActiveSkillCast action)
    {
        foreach (var passive in _pets.Guardians(s).Where(g => !g.IsPrimary).ToArray())
        {
            bool Current() => CurrentCastMap(s, action) && _pets.GuardianEquipped(s, passive);
            if (!Current() || passive.Passive == null || !GuardianPetRules.ReactionTriggered(passive.Reactive, cast.Effect)) continue;
            var stats = _character.Stats(s);
            if (passive.Reactive is GuardianPetRules.Reaction.Heal or GuardianPetRules.Reaction.DispelHeal)
            {
                long amount = (long)Math.Max(0, Math.Floor(stats[AttributeId.MaxHp] * GuardianPetRules.Description(passive, 0) / 100));
                await GuardianHealAsync(s, s, (int)GameData.Int(passive.Passive.Value, "_iID"), action.Id, amount, Current);
            }
            else if (GuardianPetRules.ReactiveBuff(_data, passive) is var buff && buff > 0)
            {
                if (passive.Reactive is GuardianPetRules.Reaction.NightmareDebuff or GuardianPetRules.Reaction.DaedreDebuff
                    or GuardianPetRules.Reaction.IsisReduction)
                { await GuardianAreaReactionAsync(s, passive, action, buff); continue; }
                if (passive.Reactive == GuardianPetRules.Reaction.Defence
                    && stats[AttributeId.Hp] >= stats[AttributeId.MaxHp] * GuardianPetRules.Description(passive, 0) / 100) continue;
                int durationIndex = passive.Reactive == GuardianPetRules.Reaction.CastSpeed ? 0
                    : passive.Reactive == GuardianPetRules.Reaction.Defence ? 2 : 1;
                int duration = (int)(GuardianPetRules.Description(passive, durationIndex) * 1000);
                if (passive.Reactive == GuardianPetRules.Reaction.DefenceBuff && passive.Core != null)
                    await GuardianHopeCoreAsync(s, passive, action, buff, duration, Current);
                else await _buffs.AddGuardianAsync(s, buff, duration, action.OwnerId);
            }
            else await GuardianSupportReactionAsync(s, passive, action, Current);
        }
    }

    /// <summary>Offensive guardian hit reactions use the actual child skill's category and positive damage.
    /// (inferred) Trigger per hit, applying shared hostile-buff resistance/refresh rules.</summary>
    private async Task GuardianDamageReactionAsync(GameSession s, MonsterUnit target, JsonElement row, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead || s.SceneLoading
            || GameData.Int(row, "_iSystemType") != GuardianPetRules.EffectType) return;
        if (target.Hp <= 0 && GuardianPetRules.OffensiveOrControl(row))
            await GuardianKillHealingAsync(s, row);
        if (target.Dead || target.Hp <= 0 || !GuardianPetRules.OffensiveOrControl(row)) return;
        long role = s.Role.Id, generation = s.SceneGeneration, worldScene = s.WorldScene;
        var attachment = s.Attachment;
        foreach (var passive in _pets.Guardians(s).Where(p => !p.IsPrimary
            && GuardianHitReactionMatches(p.Reactive, row)).ToArray())
        {
            if (s.Role?.Id != role || s.Dead || s.SceneLoading || s.SceneGeneration != generation || s.WorldScene != worldScene
                || !ReferenceEquals(s.Attachment, attachment) || attachment?.Cancelled == true) break;
            int buff = passive.Reactive == GuardianPetRules.Reaction.Stun ? GuardianPetRules.StunBuff : GuardianPetRules.ReactiveBuff(_data, passive);
            if (!_pets.GuardianEquipped(s, passive) || buff <= 0) continue;
            int chance = passive.Reactive == GuardianPetRules.Reaction.Stun ? (int)(GuardianPetRules.Description(passive, 0) * 100) : 10000;
            int durationIndex = passive.Reactive == GuardianPetRules.Reaction.MarionetteDebuff ? 2 : 1;
            var tuple = JsonSerializer.SerializeToElement(new[] { buff, chance, (int)(GuardianPetRules.Description(passive, durationIndex) * 1000) });
            if (RollHostileBuff(tuple, s, null, null, target, out int id, out int duration))
            {
                await _buffs.AddGuardianAsync(target, id, duration, role);
                // (inferred) Reuse the existing timed monster slow policy, not only the cosmetic buff icon.
                if (passive.Reactive == GuardianPetRules.Reaction.Slow && s.Role?.Id == role && s.SceneGeneration == generation && s.WorldScene == worldScene
                    && !s.Dead && !s.SceneLoading && ReferenceEquals(s.Attachment, attachment)
                    && attachment?.Cancelled != true && _pets.GuardianEquipped(s, passive))
                    await _monsters.ApplySlowAsync(target, (int)(GuardianPetRules.Description(passive, 0) * 100),
                        _buffs.UnitBuffRemainingMs(target, id, role));
            }
        }
    }

    private async Task GuardianKillHealingAsync(GameSession s, JsonElement row)
    {
        var owner = s.Role;
        long generation = s.SceneGeneration;
        var attachment = s.Attachment;
        foreach (var passive in _pets.Guardians(s).Where(p => !p.IsPrimary && p.Reactive == GuardianPetRules.Reaction.KillHeal).ToArray())
        {
            bool Current() => ReferenceEquals(s.Role, owner) && !s.Dead && !s.SceneLoading && s.SceneGeneration == generation
                && ReferenceEquals(s.Attachment, attachment) && attachment?.Cancelled != true && _pets.GuardianEquipped(s, passive);
            if (!Current() || passive.Passive is not { } effect) continue;
            var stats = _character.Stats(s);
            long amount = (long)Math.Floor(Math.Max(stats[AttributeId.Atk], stats[AttributeId.Matk])
                * GuardianPetRules.Description(passive, 0) / 100);
            await GuardianHealAsync(s, s, (int)GameData.Int(effect, "_iID"), ++_sessionIds, amount, Current);
        }
    }

    /// <summary>Same named Wild Rose reaction for a committed hostile-player hit. Hostility, source generation and
    /// damage settlement are checked by HitEventPlayersAsync; player movement comes from the real 1131 buff.</summary>
    private async Task GuardianPlayerDamageReactionAsync(GameSession source, GameSession target, JsonElement row, long damage, Func<bool> current)
    {
        if (damage <= 0 || GameData.Int(row, "_iSystemType") != GuardianPetRules.EffectType) return;
        if (_character.Stats(target)[AttributeId.Hp] <= 0)
        {
            if (GuardianPetRules.OffensiveOrControl(row) && current()) await GuardianKillHealingAsync(source, row);
            return;
        }
        if (!GuardianPetRules.OffensiveOrControl(row)) return;
        foreach (var passive in _pets.Guardians(source).Where(p => !p.IsPrimary
            && GuardianHitReactionMatches(p.Reactive, row)).ToArray())
        {
            int buff = passive.Reactive == GuardianPetRules.Reaction.Stun ? GuardianPetRules.StunBuff : GuardianPetRules.ReactiveBuff(_data, passive);
            if (!current() || !_pets.GuardianEquipped(source, passive) || buff <= 0) continue;
            int chance = passive.Reactive == GuardianPetRules.Reaction.Stun ? (int)(GuardianPetRules.Description(passive, 0) * 100) : 10000;
            int durationIndex = passive.Reactive == GuardianPetRules.Reaction.MarionetteDebuff ? 2 : 1;
            var tuple = JsonSerializer.SerializeToElement(new[] { buff, chance, (int)(GuardianPetRules.Description(passive, durationIndex) * 1000) });
            if (RollHostileBuff(tuple, source, null, target, null, out int id, out int duration))
                await _buffs.AddGuardianAsync(target, id, duration, source.Role!.Id);
        }
    }

    private static bool GuardianHitReactionMatches(GuardianPetRules.Reaction reaction, JsonElement row) => reaction switch
    {
        GuardianPetRules.Reaction.MarionetteDebuff => GuardianPetRules.OffensiveOrControl(row),
        GuardianPetRules.Reaction.Slow or GuardianPetRules.Reaction.Stun => GuardianPetRules.Category(row, GuardianPetRules.OffensiveCategory),
        _ => false,
    };
}
