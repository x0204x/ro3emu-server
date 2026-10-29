using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Allied companions in combat (CompanionUnit). A companion's cast is shown like a monster's (31100 UseInSkill, then
/// 31101 SkillHit) and deals the skill's damage from its monster template through the shared formula; the hit is
/// settled on the monster exactly like a player's (hit bookkeeping, durable kill, kill observers), credited to a present
/// participant of the run (inferred: the original credited its own script). A monster answers with its normal attack;
/// a companion at 0 HP dies and its owner removes it. Separate from the tower pet paths (CombatService.TowerPets).
/// </summary>
public sealed partial class CombatService
{
    /// <summary>(inferred) A hit lands at 40% of the cast's action, as for tower pets: the server hit point is not in the client.</summary>
    private const double CompanionHitShare = 0.4;
    /// <summary>EffectData 1000002, the hit effect a monster's normal swing shows on players and tower pets too.</summary>
    private const int DefaultHitEffect = 1000002;
    /// <summary>The window CompanionDps averages a rotation over.</summary>
    private const long CompanionDpsWindowMs = 600_000;

    private void WireCompanions() => _monsters.AttackCompanion = MonsterAttackCompanionAsync;

    internal async Task CompanionAttackAsync(CompanionUnit companion, MonsterUnit target, CompanionUnit.Skill skill)
    {
        if (!Reaches(companion, target, skill, 1f) || SkillConfig.Get(_data, skill.Id) is not { } config) return;
        long castId = ++_sessionIds;
        await _world.ToUnitViewersAsync(companion, UseInSkill, new UseInSkillInfo
        {
            SrcID = companion.Uid, SkillID = skill.Id, TargetId = target.Uid, TargetPos = target.Pos, Face = companion.Face,
            SessionId = castId, SkillMs = skill.ActionMs, StartTime = Now,
        });
        ScheduleRuntime(companion.WorldScene, TimeSpan.FromMilliseconds(skill.ActionMs * CompanionHitShare),
            () => CompanionHitAsync(companion, target, skill, config.Row, castId));
    }

    private static bool Reaches(CompanionUnit companion, MonsterUnit target, CompanionUnit.Skill skill, float slack) =>
        !companion.Dead && companion.Hp > 0 && !companion.Removed && !target.Dead && target.Hp > 0
        && target.InstanceId == companion.InstanceId && target.WorldScene == companion.WorldScene
        && MonsterService.Distance(companion.X, companion.Z, target.X, target.Z) <= skill.Range + slack;

    private static Fighter CompanionFighter(MonsterTemplate t) =>
        new(t.Atk, t.Matk, t.Hit, t.Cri, t.Level, t.Element) { Def = t.Def, Mdef = t.Mdef, Flee = t.Flee };

    private async Task CompanionHitAsync(CompanionUnit companion, MonsterUnit target, CompanionUnit.Skill skill, JsonElement row, long castId)
    {
        if (!Reaches(companion, target, skill, 2f) || companion.Credit() is not { Role: { } role } player) return;
        var attachment = player.Attachment;
        long generation = player.SceneGeneration;
        bool Current() => ReferenceEquals(player.Role, role) && ReferenceEquals(player.Attachment, attachment)
            && !player.SceneLoading && player.SceneGeneration == generation && player.WorldScene == target.WorldScene;
        var (damage, result) = Damage(CompanionFighter(companion.Template), MonsterFighter(target), row);
        long previousHp = target.Hp;
        target.Hp = Math.Max(0, target.Hp - damage);
        bool lethal = target.Hp == 0; // reserved before the awaited settlement, as a player's hit does
        await SettleMonsterHitAsync(player, target, previousHp, damage);
        var hit = new SkillHitInfo
        {
            SrcID = companion.Uid, SkillId = skill.Id, ShowPoint = HitEffect(new SkillConfig(row).ScriptShow),
            SessionId = castId, SrcElement = companion.Template.Element,
            Dsts = { new DamageInfo { DstID = target.Uid, Damage = damage, HitResult = result, DstElement = target.Template.Element,
                IsDead = target.Hp == 0 } },
        };
        foreach (var viewer in target.Viewers.Union(companion.Viewers).ToArray()) await viewer.SendGameAsync(SkillHit, hit);
        if (target.Hp == 0 && !target.DeathDurable) return; // another cast's uncommitted death
        await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
        if (target.Hp > 0)
        {
            _monsters.Engage(target, companion);
            await FightingAsync(target);
            return;
        }
        if (lethal) await CompanionKillAsync(player, target, castId, Current);
    }

    /// <summary>A companion's kill, published as a player's: the corpse, the attackers' interrupted swings, the kill observers
    /// (the dungeon's wave and step completion and statistics).</summary>
    private async Task CompanionKillAsync(GameSession player, MonsterUnit target, long castId, Func<bool> current)
    {
        try
        {
            await _monsters.KillAsync(target);
            foreach (var other in _sessions.All().Where(o => o.Attack?.Target == target))
            {
                other.Attack = null;
                await other.SendGameAsync(SkillEnd, new SkillEndNotify
                {
                    SrcID = other.Role!.Id, SessionId = castId, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                    InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                });
            }
            await PublishDurableKillAsync(player, target, current);
        }
        finally
        {
            if (target.Dead) await FinalizeLethalPublicationAsync(target);
        }
    }

    private async Task MonsterAttackCompanionAsync(MonsterUnit monster, CompanionUnit companion)
    {
        if (companion.Dead || companion.Hp <= 0 || monster.Dead || monster.Hp <= 0) return;
        long castId = ++_sessionIds;
        await _world.ToUnitViewersAsync(monster, UseInSkill, new UseInSkillInfo
        {
            SrcID = monster.Uid, SkillID = monster.Template.NormalSkill, TargetId = companion.Uid, TargetPos = companion.Pos,
            Face = monster.Face, SessionId = castId, SkillMs = monster.Template.SkillMs, StartTime = Now,
        });
        ScheduleRuntime(monster.WorldScene, TimeSpan.FromSeconds(monster.Template.HitPointSeconds), async () =>
        {
            if (companion.Dead || companion.Hp <= 0 || companion.Removed || monster.Dead || monster.Hp <= 0
                || monster.WorldScene != companion.WorldScene
                || MonsterService.Distance(monster.X, monster.Z, companion.X, companion.Z) > monster.Template.AttackRange + 2f) return;
            await DamageCompanionAsync(monster, companion, SkillConfig.Get(_data, monster.Template.NormalSkill)?.Row,
                monster.Template.NormalSkill, castId, DefaultHitEffect);
        });
    }

    /// <summary>A monster's hit on a companion (its normal attack, or a boss area skill at the boss skills' damage
    /// percent): the row's damage against the companion's template, its HP and death.</summary>
    private async Task DamageCompanionAsync(MonsterUnit monster, CompanionUnit companion, JsonElement? row, int skillId,
        long castId, int showPoint, int damagePercent = 100)
    {
        var t = companion.Template;
        var defender = new Fighter(0, 0, 0, 0, t.Level, t.Element) { Def = t.Def, Mdef = t.Mdef, Flee = t.Flee };
        var (damage, result) = row is { } r ? Damage(MonsterFighter(monster), defender, r)
            : (Math.Max(1, monster.Template.Atk), DamageHitResult.Normal);
        damage = MonsterBuffDamage(monster, row, ScaleMonsterDamage(damage, monster.DamageRatio));
        if (damagePercent != 100) damage = damagePercent <= 0 ? 0 : ScaleMonsterDamage(damage, damagePercent * 100);
        companion.Hp = Math.Max(0, companion.Hp - damage);
        companion.Dead = companion.Hp == 0; // reserved before the awaited broadcasts
        var hit = new SkillHitInfo
        {
            SrcID = monster.Uid, SkillId = skillId, ShowPoint = showPoint, SessionId = castId,
            SrcElement = monster.Template.Element,
            Dsts = { new DamageInfo { DstID = companion.Uid, Damage = damage, HitResult = result, DstElement = t.Element,
                IsDead = companion.Dead } },
        };
        await _world.ToUnitViewersAsync(monster, SkillHit, hit);
        await _world.ToUnitViewersAsync(companion, AoiAttrChange, companion.HpChange());
        if (!companion.Dead) return;
        await _world.ToUnitViewersAsync(companion, EntityBaseChange, companion.StateChange());
        await companion.Fell(companion);
    }

    /// <summary>
    /// A companion's expected damage per second against a monster, standing in reach: its rotation (CompanionUnit.Pick,
    /// NextAttackMs) over a long window, each cast's expected damage from the same formula as its hits. The reconstructed
    /// story bosses fought beside companions are tuned with it (DungeonTests.StoryBossTuning).
    /// </summary>
    internal double CompanionDps(CompanionUnit.Kit kit, MonsterTemplate target)
    {
        var attacker = CompanionFighter(kit.Template);
        var defender = new Fighter(0, 0, target.Flee, 0, target.Level, target.Element) { Def = target.Def, Mdef = target.Mdef, Flee = target.Flee };
        var ready = new Dictionary<int, long>();
        double total = 0;
        for (long now = 0; now < CompanionDpsWindowMs;)
        {
            if (CompanionUnit.Pick(kit, ready, now, 0) is not { } skill)
            {
                now = ready.Values.Where(v => v > now).DefaultIfEmpty(now + 1).Min();
                continue;
            }
            if (SkillConfig.Get(_data, skill.Id) is { } row) total += ExpectedDamage(attacker, defender, row.Row);
            ready[skill.Id] = now + skill.CooldownMs;
            now = CompanionUnit.NextAttackMs(kit, skill, now);
        }
        return total * 1000.0 / CompanionDpsWindowMs;
    }
}
