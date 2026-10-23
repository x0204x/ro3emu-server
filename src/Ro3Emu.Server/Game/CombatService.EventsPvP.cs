using System.Text.Json;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Player targets for the configured Kafra/Dragon private matches. The event owner supplies hostility and
/// settlement; casts reuse SkillConfig ownership, SP, cooldown, range, hit moment, shared damage, HP and death.
/// Reusing the emulator's inferred damage formula for player defenders is (inferred); scope never extends to
/// ordinary world scenes or arbitrary player ids. AOEs share one configured target cap with monster targets.
/// </summary>
public sealed partial class CombatService
{
    private sealed record EventAttack(GameSession Target, int SkillId, long NextSwing, float StartX, float StartZ);
    private readonly Dictionary<GameSession, EventAttack> _eventAttacks = new();
    private Func<GameSession, GameSession, bool>? _eventCanFight;
    private Func<GameSession, GameSession, Task>? _eventPlayerKilled;

    public void SetEventPvPHandlers(Func<GameSession, GameSession, bool> canFight,
        Func<GameSession, GameSession, Task> playerKilled)
    { _eventCanFight = canFight; _eventPlayerKilled = playerKilled; }

    private bool CanEventHit(GameSession source, GameSession target) => source != target
        && source.Role != null && target.Role != null && !source.Dead && !target.Dead
        && !source.SceneLoading && !target.SceneLoading && source.InstanceId != 0
        && source.InstanceId == target.InstanceId && _eventCanFight?.Invoke(source, target) == true;

    private bool CanPlayerHit(GameSession source, GameSession target) =>
        CanEventHit(source, target) || CanTerritoryHit(source, target);

    private GameSession? EventTarget(GameSession source, long id) => _sessions.All()
        .FirstOrDefault(target => target.Role?.Id == id && CanPlayerHit(source, target) && GuardianCanObserve(source, target));

    private static float PlayerDistance(GameSession a, GameSession b) =>
        MonsterService.Distance(a.Position.X, a.Position.Z, b.Position.X, b.Position.Z);

    private GameSession[] EventAreaTargets(GameSession source, JsonElement row)
    {
        float radius = (row.TryGetProperty("_kRange", out var range) && range.ValueKind == JsonValueKind.Array
            && range.GetArrayLength() > 0 ? (float)(Data.GameData.Number(range[0]) ?? 300) : 300) / 100f;
        return _sessions.All().Where(p => CanPlayerHit(source, p) && PlayerDistance(source, p) <= radius + .5f).ToArray();
    }

    private async Task SwingEventAsync(GameSession source, GameSession target, int skillId, long session)
    {
        if (source.Role == null || source.Progress == null) return;
        using var held = await CharacterService.Lock(source);
        if (!CanPlayerHit(source, target) || !GuardianCanObserve(source, target) || source.SkillCast is { Cancelled: false }
            || EffectiveSkillRow(source, skillId) is not { } row || !Learned(source, row)
            || !SkillModeAllowed(source, row) || _buffs.HasForbidden(source, ForbiddenState.NormalAttack)) return;
        var request = new CastSkillReq
        {
            SkillId = (int)Data.GameData.Int(row, "_iID"), TargetId = target.Role!.Id,
            TargetPos = target.Position.Clone(), Face = MonsterService.Angle(source.Position.X, source.Position.Z, target.Position.X, target.Position.Z),
        };
        if (!await AdmitAutomaticCostAsync(source, row, request, session)) { _eventAttacks.Remove(source); return; }
        await StartCastAsync(source, row, request, session);
    }
    private async Task HitEventPlayersAsync(GameSession source, GameSession[] targets, JsonElement row,
        int skillId, int show, long session, Func<bool> currentSource, Action<long, long>? onHit = null)
    {
        if (FirstNumber(row, "_kDamageParam1") is not > 0 && FirstNumber(row, "_kDamageParam2") is not > 0) return;
        foreach (var target in targets)
        {
            if (!currentSource()) break;
            if (!CanPlayerHit(source, target)) continue;
            bool eventFight = CanEventHit(source, target);
            var attackerRole = source.Role!;
            var targetRole = target.Role!;
            long sceneId = source.WorldScene;
            var (damage, result) = PlayerHitDamage(source, target, row);
            var committed = await CommitPlayerDamageAsync(target, damage, row,
                () => currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)
                    && CanPlayerHit(source, target));
            if (committed == null) continue;
            (damage, long hp, long effective, long shieldDamage) = committed.Value;
            onHit?.Invoke(targetRole.Id, damage);
            if (effective > 0 && hp > 0) await _buffs.BreakHitControlsAsync(target);
            try
            {
                if (effective > 0 && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId))
                {
                    foreach (var handler in PvpDealt) handler(source, target, effective, hp == 0);
                    foreach (var handler in PvpDealtAsync) await handler(source, target, effective, hp == 0);
                }
                var hit = new SkillHitInfo
                {
                    SrcID = attackerRole.Id, SkillId = skillId, ShowPoint = show, Split = HitSplit(row),
                    SessionId = session, SrcElement = 1,
                    Dsts = { new DamageInfo { DstID = targetRole.Id, Damage = damage, SheildDamage = shieldDamage, HitResult = result,
                    DstElement = 1, IsDead = hp == 0 } }
                };
                var sourceViewers = CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)
                    ? _world.Viewers(source).Append(source) : Enumerable.Empty<GameSession>();
                foreach (var viewer in sourceViewers.Concat(_world.Viewers(target)).Append(target).Distinct())
                    await viewer.SendGameAsync(SkillHit, hit);
                if (!ReferenceEquals(target.Role, targetRole)) continue;
                await _character.SetHpSpAsync(target, null, null); // publish current HP after the serialized reservation
                if (!ReferenceEquals(target.Role, targetRole)) continue;
                if (CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)) await FightingAsync(source);
                await FightingAsync(target);
                if (hp == 0)
                {
                    if (!ReferenceEquals(target.Role, targetRole)) continue;
                    await PlayerDiesAsync(target);
                    if (effective <= 0 || !CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)) continue;
                    if (eventFight && _eventPlayerKilled != null) await _eventPlayerKilled(source, target);
                    foreach (var handler in PlayerKilled)
                    {
                        if (!CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)) break;
                        await handler(source, target);
                    }
                }
                await GuardianPvpFollowUpsAsync(source, target, targetRole, row, effective, hp == 0,
                    () => currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId));
            }
            finally { if (hp == 0) ReleaseDeathPublication(target); }
        }
    }

    private void TickEventPvP(long now)
    {
        foreach (var (source, attack) in _eventAttacks.ToArray())
        {
            if (!CanPlayerHit(source, attack.Target) || !GuardianCanObserve(source, attack.Target) || PlayerDistance(source, attack.Target) > 30
                || MonsterService.Distance(attack.StartX, attack.StartZ, source.Position.X, source.Position.Z) > 1.5f)
            { _eventAttacks.Remove(source); continue; }
            if (now < attack.NextSwing || source.SkillCast is { Cancelled: false } || _data.Row("SkillConfig", attack.SkillId) is not { } row
                || PlayerDistance(source, attack.Target) > Range(row) + 1.5f) continue;
            _eventAttacks[source] = attack with { NextSwing = now + SkillMs(source, row) };
            _loop.PostRuntime(() => SwingEventAsync(source, attack.Target, attack.SkillId, ++_sessionIds));
        }
    }

    /// <summary>Guardian reactions to a committed player hit, after the hit, its HP and any death are published, so an
    /// extra guardian hit that kills the target never precedes the hit that caused it. The victim's own reactions
    /// answer the committed hit whatever became of the attacker's cast; the attacker's need it still current.</summary>
    private async Task GuardianPvpFollowUpsAsync(GameSession source, GameSession target, Accounts.Role targetRole, JsonElement row,
        long effective, bool killed, Func<bool> current)
    {
        if (effective <= 0 || !ReferenceEquals(target.Role, targetRole)) return;
        await GuardianTakenAsync(target, effective);
        if (!current()) return;
        await GuardianWindowHitAsync(source, targetRole.Id, target.Position, row, effective, killed);
        if (current()) await GuardianPlayerDamageReactionAsync(source, target, row, effective, current);
    }

    /// <summary>One player's damage from another's skill: the hit with a guardian's crit rules, the guardian factors and
    /// the restraints; a guardian's fixed hit replaces the amount.</summary>
    private (long Damage, DamageHitResult Result) PlayerHitDamage(GameSession source, GameSession target, JsonElement row)
    {
        var attack = _character.Stats(source); var defend = _character.Stats(target);
        var a = new Fighter(attack[AttributeId.Atk], attack[AttributeId.Matk], attack[AttributeId.Hit], attack[AttributeId.Cri],
            source.Progress!.Level, 1);
        var b = new Fighter(0, 0, 0, 0, target.Progress!.Level, 1)
        { Def = defend[AttributeId.Def], Mdef = defend[AttributeId.Mdef], Flee = defend[AttributeId.Flee], ResCri = defend[AttributeId.ResCri] };
        var (damage, result) = Damage(a, b, row, bonusCrit: GuardianPetRules.CriticalChance(row),
            forceCrit: GuardianPetRules.CriticalAtHp(row, defend[AttributeId.Hp], defend[AttributeId.MaxHp]));
        damage = GuardianPetRules.DirectDamage(row, damage);
        damage = GuardianOutgoingDamage(source, row, damage);
        damage = GuardianControlledDamage(source, GuardianControlTarget(target), damage);
        if (result != DamageHitResult.Miss) damage = ApplyPlayerRestraints(source, target, damage);
        return row.TryGetProperty("_ro3GuardianFixedHit", out var fixedValue)
            ? (fixedValue.GetInt64(), DamageHitResult.Normal) : (damage, result);
    }
}
