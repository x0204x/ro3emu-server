using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A boss special skill (MonsterService.BossSkills picks it) carried out on the same wire as every other cast: 31100 (or
/// 31106 for an _iSkillEffectLevel row) with the row's chant and action, so the client plays the show with its warning
/// indicators; the boss panel's 31125 for an _iImportantSkill boss; then a 31101 at each attack point of the show, the
/// damage from the shared formula (MonsterSkillHitAsync) and the row's _kBuff2 on the players hit. Where it lands follows
/// BossSkill.Area, resolved at the hit, so a player who leaves the marked area in time is not hit; a ground area stays
/// where it was marked. _kBuff1 goes on the boss, a summon spawns its minions, a charge moves the boss to its target
/// first (31112). Hit timing and charge distance are (inferred).
/// </summary>
public sealed partial class CombatService
{
    private const float ChargeStopShort = 1.5f; // a charge ends this far in front of its target (inferred)

    private void WireBossSkills()
    {
        _monsters.CastBossSkill = CastBossSkillAsync;
        _monsters.BossSkillsEnded = EndBossSkillsAsync;
    }

    private static bool BossCastCurrent(MonsterUnit boss, long serial) =>
        !boss.Dead && boss.Hp > 0 && boss.BossSkills is { } rt && rt.CastSerial == serial;

    private async Task CastBossSkillAsync(MonsterUnit boss, GameSession? player, TowerPetUnit? pet, BossSkill skill, long serial)
    {
        if (!BossCastCurrent(boss, serial)) return;
        var rt = boss.BossSkills!;
        var options = _monsters.BossOptions;
        long now = Now;
        long session = ++_sessionIds;
        rt.ActiveSession = session;
        var role = player?.Role;
        bool hasTarget = player != null ? role != null && !player.Dead && player.WorldScene == boss.WorldScene
            : pet != null && !pet.Dead && pet.WorldScene == boss.WorldScene;
        var targetPos = player != null ? player.Position.Clone() : pet?.Pos ?? boss.Pos;
        if (skill.ChargeEffect > 0 && hasTarget)
        {
            await ChargeAsync(boss, skill, targetPos);
            if (!BossCastCurrent(boss, serial)) return;
        }
        float face = hasTarget ? MonsterService.Angle(boss.X, boss.Z, targetPos.X, targetPos.Z) : boss.Face;
        boss.Face = face;
        var origin = boss.Pos;
        var center = skill.Area == BossSkillArea.AroundTarget && hasTarget ? targetPos : origin;
        long targetId = !hasTarget || skill.Area is not (BossSkillArea.Target or BossSkillArea.AroundTarget) ? 0
            : role?.Id ?? pet!.Uid;
        await SendMonsterTimelineAsync(boss, new UseInSkillInfo
        {
            SrcID = boss.Uid, SkillID = skill.Id, TargetId = targetId, TargetPos = hasTarget ? targetPos : origin, Face = face,
            SessionId = session, ChantMs = skill.ChantMs, SkillMs = skill.ActionMs, StartTime = now,
        }, highLevel: skill.HighLevel);
        if (!skill.Once && skill.CdMs > 0) await BossCooldownStartedAsync(boss, skill.Id, now + skill.CdMs);
        await FightingAsync(boss);
        int show = HitEffect(GameData.Int(skill.Row, "_iScriptShow"));
        for (int i = 0; i < skill.HitMs.Length; i++)
        {
            bool first = i == 0;
            ScheduleRuntime(boss.WorldScene, TimeSpan.FromMilliseconds(skill.HitMs[i]), () =>
                BossSkillHitAsync(boss, player, role, pet, skill, serial, session, origin, center, face, show, first, options));
        }
        _log.LogDebug("Boss {Npc} casts {Skill} (phase {Phase})", boss.Template.NpcId, skill.Id, rt.Phase);
    }

    private async Task BossSkillHitAsync(MonsterUnit boss, GameSession? player, Role? role, TowerPetUnit? pet, BossSkill skill,
        long serial, long session, Vec3 origin, Vec3 center, float face, int show, bool first, BossSkillOptions options)
    {
        if (!BossCastCurrent(boss, serial)) return;
        var rt = boss.BossSkills!;
        if (first)
        {
            foreach (var (buff, chance, ms) in skill.SelfBuffs)
                if (ms > 0 && Rolls.Proc(10000) < chance) await _buffs.AddSupportAsync(boss, buff, ms, boss.Uid);
        }
        if (skill.SummonNpc > 0)
        {
            // A summon only spawns: its row's damage belongs to the minions' own attacks (inferred).
            if (first) await SummonAsync(boss, rt, skill, player);
            return;
        }
        if (!skill.HitsPlayers) return;
        var victims = new List<GameSession>();
        var pets = new List<TowerPetUnit>();
        if (skill.Area == BossSkillArea.Target)
        {
            // The locked target, unless it left the skill's reach or the scene meanwhile.
            if (player != null && role != null && ReferenceEquals(player.Role, role) && !player.Dead && !player.SceneLoading
                && player.WorldScene == boss.WorldScene && MonsterService.Distance(boss, player) <= skill.Reach + 2f) victims.Add(player);
            else if (pet != null && !pet.Dead && pet.Hp > 0 && pet.WorldScene == boss.WorldScene
                && MonsterService.Distance(boss.X, boss.Z, pet.X, pet.Z) <= skill.Reach + 2f) pets.Add(pet);
        }
        else
        {
            float reach = skill.Area == BossSkillArea.Rect ? MathF.Sqrt(skill.Radius * skill.Radius + skill.Width * skill.Width) : skill.Radius;
            var near = new List<GameSession>();
            _world.CollectPlayers(boss.WorldScene, center.X, center.Z, reach + 0.5f, near);
            foreach (var p in near)
                if (p.Role != null && !p.Dead && !p.SceneLoading && p.WorldScene == boss.WorldScene
                    && InBossArea(skill, origin, center, face, p.Position.X, p.Position.Z)) victims.Add(p);
            if (boss.InstanceId != 0)
                foreach (var unit in _world.Map(boss.WorldScene).Near(center.X, center.Z, reach + 0.5f))
                    if (unit is TowerPetUnit tp && !tp.Dead && tp.Hp > 0 && InBossArea(skill, origin, center, face, tp.X, tp.Z)) pets.Add(tp);
            victims.Sort((a, b) => MonsterService.Distance(center.X, center.Z, a.Position.X, a.Position.Z)
                .CompareTo(MonsterService.Distance(center.X, center.Z, b.Position.X, b.Position.Z)));
            if (victims.Count > skill.MaxTargets) victims.RemoveRange(skill.MaxTargets, victims.Count - skill.MaxTargets);
            int room = Math.Max(0, skill.MaxTargets - victims.Count);
            if (pets.Count > room) pets.RemoveRange(room, pets.Count - room);
        }
        if (victims.Count > 0)
            await MonsterSkillHitAsync(boss, skill.Row, victims, boss.Uid, session, skill.Damages ? options.DamagePercent : 0, bossDebuffsOnly: true);
        foreach (var tp in pets)
        {
            if (!BossCastCurrent(boss, serial)) break;
            if (!tp.Dead && tp.Hp > 0 && skill.Damages) await DamageTowerPetAsync(boss, tp, skill.Row, skill.Id, session, show, options.DamagePercent);
        }
    }

    /// <summary>Whether (x, z) is inside the skill's area: a circle around the boss or its marked point, a fan or a
    /// rectangle ahead of the boss in the direction it faced when the cast began.</summary>
    internal static bool InBossArea(BossSkill skill, Vec3 origin, Vec3 center, float face, float x, float z)
    {
        switch (skill.Area)
        {
            case BossSkillArea.AroundSelf:
            case BossSkillArea.AroundTarget:
                return MonsterService.Distance(center.X, center.Z, x, z) <= skill.Radius + 0.1f;
            case BossSkillArea.Fan:
                {
                    float dx = x - origin.X, dz = z - origin.Z;
                    if (MathF.Sqrt(dx * dx + dz * dz) > skill.Radius + 0.1f) return false;
                    if (dx * dx + dz * dz < 0.25f) return true; // standing on the boss
                    double radians = face * Math.PI / 180;
                    double forward = dx * Math.Sin(radians) + dz * Math.Cos(radians), side = dx * Math.Cos(radians) - dz * Math.Sin(radians);
                    return Math.Atan2(Math.Abs(side), forward) <= skill.AngleDeg * Math.PI / 360;
                }
            case BossSkillArea.Rect:
                {
                    float dx = x - origin.X, dz = z - origin.Z;
                    double radians = face * Math.PI / 180;
                    double forward = dx * Math.Sin(radians) + dz * Math.Cos(radians), side = dx * Math.Cos(radians) - dz * Math.Sin(radians);
                    return forward >= -0.5 && forward <= skill.Radius + 0.1f && Math.Abs(side) <= skill.Width / 2 + 0.1f;
                }
            default:
                return false;
        }
    }

    /// <summary>A charge or leap: the boss moves to just short of its target (within the skill's reach) on the show's
    /// displacement effect, as a player skill's caster move (31112, MoveType 1).</summary>
    private async Task ChargeAsync(MonsterUnit boss, BossSkill skill, Vec3 to)
    {
        float dx = to.X - boss.X, dz = to.Z - boss.Z, length = MathF.Sqrt(dx * dx + dz * dz);
        float go = Math.Min(skill.Reach, length - ChargeStopShort);
        if (go < 0.5f) return;
        var start = boss.Pos;
        var end = WalkableEnd(boss.SceneEntry, start, dx / length * go, dz / length * go);
        boss.Moving = false;
        boss.X = end.X; boss.Y = end.Y; boss.Z = end.Z;
        await _world.ToUnitViewersAsync(boss, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)boss.Uid, StartPos = start, EndPos = end, SkillConfigId = skill.Id, EffectId = skill.ChargeEffect,
            MoveType = 1, StartTimestampMs = Now, Face = MonsterService.Angle(start.X, start.Z, end.X, end.Z), MoveTimeMs = skill.ChargeMs,
        });
        await _world.MoveUnitAsync(boss, new MovementChangeNtf { Uid = boss.Uid, MovementInfo = boss.CurrentMovement(Now) });
    }

    /// <summary>
    /// The summon's minions around the boss (3 m), fighting its target. A minion whose NPC has a _kRespawnSkill blast
    /// (Self-Destruct Poring's Poring Explosion) stands, starts it at once and blasts the players around it when its life
    /// ends, as MvpService's field-MVP summons; at most three summons' worth live at once (inferred cap).
    /// </summary>
    private async Task SummonAsync(MonsterUnit boss, BossSkillRuntime rt, BossSkill skill, GameSession? target)
    {
        rt.Summons.RemoveAll(m => m.Removed);
        if (rt.Summons.Count(m => !m.Dead) >= skill.SummonCount * 3 || _monsters.Template(skill.SummonNpc) is not { } template) return;
        var blast = skill.SummonBlast > 0 ? _data.Row("SkillConfig", skill.SummonBlast) : null;
        if (blast != null) template = template with { SpeedCm = 0, AttackRange = 0, RoamDistance = 0 };
        for (int i = 0; i < skill.SummonCount; i++)
        {
            double angle = Random.Shared.NextDouble() * Math.PI * 2;
            var at = new MapPoint(boss.X + (float)Math.Cos(angle) * 3, boss.Y, boss.Z + (float)Math.Sin(angle) * 3, boss.Face);
            if (await _monsters.SpawnSingleAsync(boss.RuntimeMap, skill.SummonNpc, at, customTemplate: template) is not { } minion) continue;
            if (boss.Dead || !ReferenceEquals(boss.BossSkills, rt))
            {
                minion.Dead = true;
                await _world.RemoveUnitAsync(minion);
                return;
            }
            rt.Summons.Add(minion);
            if (blast is { } row)
                await SendMonsterTimelineAsync(minion, new UseInSkillInfo
                {
                    SrcID = minion.Uid, SkillID = skill.SummonBlast, TargetPos = minion.Pos, Face = minion.Face,
                    SessionId = ++_sessionIds, SkillMs = (int)GameData.Int(row, "_iActionTime"), StartTime = Now,
                });
            else if (target != null && !target.Dead) _monsters.Engage(minion, target);
            ScheduleRuntime(boss.WorldScene, TimeSpan.FromMilliseconds(skill.SummonLifeMs), () => SummonLifeEndsAsync(rt, minion, skill));
        }
        _log.LogInformation("Boss {Npc} summons {Count} x {Minion}", boss.Template.NpcId, skill.SummonCount, skill.SummonNpc);
    }

    private async Task SummonLifeEndsAsync(BossSkillRuntime rt, MonsterUnit minion, BossSkill skill)
    {
        if (minion.Dead) return;
        if (skill.SummonBlast > 0 && _data.Row("SkillConfig", skill.SummonBlast) is { } row)
        {
            float radius = BossSkillRadius(row);
            int max = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
            var near = new List<GameSession>();
            _world.CollectPlayers(minion.WorldScene, minion.X, minion.Z, radius + 0.5f, near);
            near.RemoveAll(p => p.Role == null || p.Dead || p.SceneLoading);
            near.Sort((a, b) => MonsterService.Distance(minion, a).CompareTo(MonsterService.Distance(minion, b)));
            if (near.Count > max) near.RemoveRange(max, near.Count - max);
            await MonsterSkillHitAsync(minion, row, near, minion.Uid, damagePercent: _monsters.BossOptions.DamagePercent);
        }
        // The corpse stays in Summons until it is removed: the boss's end removes any that are still there.
        if (!minion.Dead) await _monsters.KillAsync(minion);
    }

    /// <summary>A blast row's radius: _kRange in cm when real, else the default.</summary>
    private float BossSkillRadius(JsonElement row) => Numbers(row, "_kRange") is { Length: > 0 } range && range[0] >= 100
        ? (float)range[0] / 100 : _monsters.BossOptions.DefaultRadius;

    /// <summary>The boss's specials ended: interrupt the cast in flight (31103, its warnings cleared) and despawn its summons.</summary>
    private async Task EndBossSkillsAsync(MonsterUnit boss, BossSkillRuntime rt, bool interrupt)
    {
        if (interrupt && rt.ActiveSession != 0)
            await _world.ToUnitViewersAsync(boss, SkillEnd, new SkillEndNotify
            {
                SrcID = boss.Uid, SessionId = rt.ActiveSession, Type = SkillEndNotify.Types.SkillEndType.Interrupted, ClearEffect = true,
                InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.State },
            });
        // Removed at once, corpses included: a dungeon room releases its scene right after its boss leaves it.
        foreach (var minion in rt.Summons.ToArray())
        {
            minion.Dead = true;
            await _world.RemoveUnitAsync(minion);
        }
        rt.Summons.Clear();
    }
}
