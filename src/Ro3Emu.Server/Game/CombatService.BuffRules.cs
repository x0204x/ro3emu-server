using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Combat's half of the shipped buff tables (BuffRuleTables): BuffTagConfig enhance/resistance on hostile buff
/// procs, the periodic damage and heal ticks, and the relations auras use. The decay and the aura registry live in
/// BuffService (BuffService.Decay/.Halo/.Periodic).
/// </summary>
public sealed partial class CombatService
{
    private void WireBuffRules()
    {
        _buffs.ResolvePeriodicSource = ResolvePeriodicSource;
        _buffs.PeriodicTick = PeriodicTickAsync;
        _buffs.HaloHostilePlayer = (carrier, other) => PlayerHostile?.Invoke(carrier, other) == true;
        _buffs.HaloEnemyMonster = CanHitMonster;
    }

    /// <summary>
    /// <see cref="RollBuff"/> for a buff a skill puts on someone else ([buff, chance, ms] of _kBuff2). Each
    /// BuffTagConfig tag in the buff's _kBuffCategory scales it by 1 + (the applier's _iAtkAttrID - the target's
    /// _iDefAttrID) / 10000 (BuffRuleTables.TagFactor, inferred arithmetic): formula 1 the chance (Curse, Blind,
    /// Silence, Fear, Immobilize, Freeze, Petrify), formula 2 the duration (Poisoned, Bleeding, Burn, Paralyze, Sleep,
    /// Stun, Slow), the split the attributes' own tips state. A duration scaled to nothing does not land.
    /// </summary>
    private bool RollHostileBuff(JsonElement tuple, GameSession? sourcePlayer, WorldUnit? sourceUnit,
        GameSession? targetPlayer, WorldUnit? targetUnit, out int id, out int duration)
    {
        id = duration = 0;
        if (tuple.GetArrayLength() < 3) return false;
        id = (int)(GameData.Number(tuple[0]) ?? 0);
        duration = (int)(GameData.Number(tuple[2]) ?? 0);
        double chance = GameData.Number(tuple[1]) ?? 0;
        if (id <= 0) return false;
        if (BuffRuleTables.For(_data).TagsByBuff.TryGetValue(id, out var tags))
        {
            double lasting = 1;
            foreach (var tag in tags)
            {
                double factor = BuffRuleTables.TagFactor(TagAttribute(sourcePlayer, sourceUnit, tag.AttackAttr),
                    TagAttribute(targetPlayer, targetUnit, tag.DefenseAttr));
                if (tag.Formula == 1) chance *= factor;
                else lasting *= factor;
            }
            if (lasting != 1)
            {
                int baseMs = duration > 0 ? duration : _data.Row("BuffConfig", id) is { } row ? (int)GameData.Int(row, "_iTime") : 0;
                if (baseMs > 0) duration = (int)Math.Round(baseMs * lasting);
                if (baseMs > 0 && duration <= 0) return false;
            }
        }
        return Rolls.Proc(10000) < chance;
    }

    /// <summary>A tag attribute of a fighter: a player's stat sheet, or what a unit's live buffs add (monster
    /// templates carry none of 701-728).</summary>
    private long TagAttribute(GameSession? player, WorldUnit? unit, int attr)
    {
        if (player?.Role != null && player.Progress != null) return _character.Stats(player)[attr];
        if (unit == null) return 0;
        double sum = 0;
        foreach (var (id, value) in _buffs.UnitAttributes(unit)) if (id == attr) sum += value;
        return (long)sum;
    }

    // ---- periodic damage and healing ----

    /// <summary>The applier of a buff: a player by role id, else a monster of that world scene by uid.</summary>
    private BuffService.PeriodicSource? ResolvePeriodicSource(long sourceId, long worldScene)
    {
        foreach (var s in _sessions.All())
            if (s.Role?.Id == sourceId && s.Progress != null)
            {
                var stats = _character.Stats(s);
                return new(sourceId, stats[103], stats[104], s.Progress.Level, 1, s);
            }
        var units = _world.Map(worldScene).Units;
        for (int i = 0; i < units.Count; i++)
            if (units[i] is MonsterUnit m && m.Uid == sourceId)
                return new(m.Uid, m.Template.Atk, m.Template.Matk, m.Template.Level, m.Template.Element, null);
        return null;
    }

    private Task PeriodicTickAsync(BuffService.PeriodicEffect effect, ActiveBuff buff)
    {
        if (effect.Rule.Kind == BuffRuleTables.PeriodicKind.Heal)
            return effect.Player is { } healed ? PeriodicHealAsync(healed, effect, buff) : Task.CompletedTask;
        if (effect.Source is not { } source) return Task.CompletedTask;
        var attacker = new Fighter(source.Atk, source.Matk, 0, 0, source.Level, source.Element);
        if (effect.Player is { } player) return PeriodicPlayerDamageAsync(player, effect, buff, attacker);
        if (effect.Unit is MonsterUnit monster) return PeriodicMonsterDamageAsync(monster, effect, buff, attacker);
        return Task.CompletedTask;
    }

    /// <summary>One tick: the applier's ATK or MATK x _kData1 through the defender's DEF/MDEF and the buff's element
    /// (the same BaseDamage a skill hit uses), times the buff's piles (Poisoned stacks to 6). (inferred) No hit, float
    /// or critical roll: the descriptions give a fixed share of the applier's damage.</summary>
    private long PeriodicAmount(Fighter attacker, Fighter defender, BuffRuleTables.PeriodicRule rule, int piles) =>
        Math.Max(1, (long)Math.Round(Math.Max(0, BaseDamage(attacker, defender, rule.Row, 0, out _)) * Math.Max(1, piles)));

    /// <summary>A tick on a player: the protected HP transition (shields, lethal prevention) every hit takes, one
    /// 31101 from the applier without a skill (skillId 0, showPoint 0: the number shows, no hit effect) and the HP.</summary>
    private async Task PeriodicPlayerDamageAsync(GameSession p, BuffService.PeriodicEffect effect, ActiveBuff buff, Fighter attacker)
    {
        var role = p.Role;
        if (role == null || role.Id != effect.TargetId || p.Dead || p.SceneLoading || p.Progress == null) return;
        long scene = p.WorldScene, generation = p.SceneGeneration;
        var stats = _character.Stats(p);
        var defender = new Fighter(0, 0, 0, 0, p.Progress.Level, 1) { Def = stats[105], Mdef = stats[106] };
        var rule = effect.Rule;
        long damage = PeriodicAmount(attacker, defender, rule, buff.PileCount);
        var committed = await CommitPlayerDamageAsync(p, damage, rule.Row,
            () => ReferenceEquals(p.Role, role) && p.WorldScene == scene && p.SceneGeneration == generation, sourceElement: rule.Element);
        if (committed == null) return;
        (damage, long hp, _, long shieldDamage) = committed.Value;
        try
        {
            await ToPlayerAndViewersAsync(p, SkillHit, new SkillHitInfo
            {
                SrcID = effect.Source!.Uid, SessionId = ++_sessionIds, SrcElement = rule.Element,
                Dsts = { new DamageInfo { DstID = role.Id, Damage = damage, SheildDamage = shieldDamage, DstElement = 1, IsDead = hp == 0 } },
            });
            await _character.SetHpSpAsync(p, null, null);
            if (effect.Source.Player is { } applier && applier.Role?.Id == effect.Source.Uid)
                await RecordDamageStatisticAsync(applier, new Romsg.DamageStatistics
                {
                    DamageSourceId = applier.Role.Id, DamageTargetId = role.Id, DamageValue = damage, DamageBeShield = shieldDamage,
                    BuffId = buff.ConfigId,
                });
            await FightingAsync(p);
            if (hp == 0) await PlayerDiesAsync(p);
        }
        finally { if (hp == 0) ReleaseDeathPublication(p); }
    }

    /// <summary>A tick on a monster from a player's buff: the ordinary hit settlement (MVP/event damage, kill
    /// rewards to the applier). (inferred) The applier must still be in that scene: a tick with no one to credit is
    /// skipped, as nothing can be rewarded for it.</summary>
    private async Task PeriodicMonsterDamageAsync(MonsterUnit m, BuffService.PeriodicEffect effect, ActiveBuff buff, Fighter attacker)
    {
        var s = effect.Source!.Player;
        var role = s?.Role;
        if (s == null || role == null || role.Id != effect.Source.Uid || s.SceneLoading || s.Dead || s.WorldScene != m.WorldScene
            || m.Dead || m.Hp <= 0 || !CanHitMonster(s, m)) return;
        var attachment = s.Attachment;
        long generation = s.SceneGeneration, scene = s.WorldScene;
        bool Current() => ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment) && !s.SceneLoading
            && s.SceneGeneration == generation && s.WorldScene == scene && _sessions.All().Contains(s);
        var rule = effect.Rule;
        var defender = MonsterFighter(m);
        long amount = PeriodicAmount(attacker, defender, rule, buff.PileCount);
        long before = m.Hp;
        m.Hp = Math.Max(0, before - amount);
        bool lethal = m.Hp == 0; // reserved before the awaited settlement, like a skill hit
        try
        {
            try { await SettleMonsterHitAsync(s, m, before, amount); }
            catch (Exception e) when (e is not EventSettlementOutcomeUnknownException)
            {
                _log.LogDebug(e, "Periodic tick of {Buff} on {Uid} not settled", buff.ConfigId, m.Uid);
                return;
            }
            var hit = new SkillHitInfo
            {
                SrcID = role.Id, SessionId = ++_sessionIds, SrcElement = rule.Element,
                Dsts = { new DamageInfo { DstID = m.Uid, Damage = amount, DstElement = m.Template.Element, IsDead = m.Hp == 0 } },
            };
            foreach (var viewer in m.Viewers.Append(s).Distinct().ToArray()) await viewer.SendGameAsync(SkillHit, hit);
            await RecordDamageStatisticAsync(s, new Romsg.DamageStatistics
            {
                DamageSourceId = role.Id, DamageTargetId = m.Uid, DamageValue = amount, BuffId = buff.ConfigId,
            });
            if (m.Hp == 0 && !m.DeathDurable) return; // another hit is still committing that death
            await _world.ToUnitViewersAsync(m, AoiAttrChange, MonsterService.Hp(m));
            await FightingAsync(m);
            if (m.Hp > 0)
            {
                if (Current()) _monsters.Engage(m, s);
                return;
            }
            if (!lethal) return;
            _log.LogInformation("{Name} killed {Monster} ({Npc}) with {Buff}", role.Name, m.Uid, m.Template.NpcId, buff.ConfigId);
            await _monsters.KillAsync(m);
            foreach (var other in _sessions.All().Where(o => o.Attack?.Target == m))
            {
                other.Attack = null;
                await other.SendGameAsync(SkillEnd, new SkillEndNotify
                {
                    SrcID = other.Role!.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                    InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                });
            }
            await PublishDurableKillAsync(s, m, Current);
        }
        finally
        {
            if (lethal && m.Hp == 0 && m.DeathDurable)
            {
                if (!m.Dead)
                    try { await _monsters.KillAsync(m); }
                    catch (Exception e) { _log.LogError(e, "Could not finalize periodic kill of {Uid}", m.Uid); }
                await PublishDurableKillAsync(s, m, Current);
                await FinalizeLethalPublicationAsync(m);
            }
        }
    }

    /// <summary>A heal tick (Breath Medicine, Tension Relax): HP up to the maximum and a heal number on 31101.</summary>
    private async Task PeriodicHealAsync(GameSession p, BuffService.PeriodicEffect effect, ActiveBuff buff)
    {
        if (p.Role == null || p.Role.Id != effect.TargetId || p.Progress == null) return;
        using var held = await CharacterService.Lock(p);
        if (p.Role == null || p.Role.Id != effect.TargetId || p.Dead || p.SceneLoading) return;
        var rule = effect.Rule;
        var stats = _character.Stats(p);
        long amount = rule.HealFlat + (rule.HealRatioBp != 0 && rule.HealAttr > 0 ? stats[rule.HealAttr] * rule.HealRatioBp / 10000 : 0);
        amount *= Math.Max(1, buff.PileCount);
        var guardian = _buffs.GuardianAttributes(p);
        amount = ModifiedHealing(guardian, guardian, amount);
        long actual = Math.Min(amount, Math.Max(0, stats[101] - stats[2000]));
        if (actual <= 0) return;
        await _character.SetHpSpAsync(p, stats[2000] + actual, null);
        await ToPlayerAndViewersAsync(p, SkillHit, new SkillHitInfo
        {
            SrcID = p.Role.Id, SessionId = ++_sessionIds,
            Dsts = { new DamageInfo { DstID = p.Role.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
        });
    }
}
