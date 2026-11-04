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
    private readonly Dictionary<GameSession, EventAttack> _eventAttacks = [];
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
        .FirstOrDefault(target => target.Role?.Id == id && CanPlayerHit(source, target) && CanSee(source, target));

    private static float PlayerDistance(GameSession a, GameSession b) =>
        MonsterService.Distance(a.Position.X, a.Position.Z, b.Position.X, b.Position.Z);

    private GameSession[] EventAreaTargets(GameSession source, JsonElement row)
    {
        float radius = (row.TryGetProperty("_kRange", out var range) && range.ValueKind == JsonValueKind.Array
            && range.GetArrayLength() > 0 ? (float)(Data.GameData.Number(range[0]) ?? 300) : 300) / 100f;
        return [.. _sessions.All().Where(p => CanPlayerHit(source, p) && PlayerDistance(source, p) <= radius + .5f)];
    }

    private async Task SwingEventAsync(GameSession source, GameSession target, int skillId, long session)
    {
        if (source.Role == null || source.Progress == null) return;
        using var held = await CharacterService.Lock(source);
        if (!CanPlayerHit(source, target) || !CanSee(source, target) || !SwingPlayedOut(source.SkillCast, Now)
            || EffectiveSkillRow(source, skillId) is not { } row || !Learned(source, row)
            || !SkillModeAllowed(source, row) || _buffs.HasForbidden(source, ForbiddenState.NormalAttack)) return;
        // 與對魔物續擊使用同一已知條件；在成本／動作前終止失去資格的 PvP 迴圈。
        if (!KnownSkillConditionsMet(source, row, null, true)) { _eventAttacks.Remove(source); return; }
        var request = new CastSkillReq
        {
            SkillId = (int)Data.GameData.Int(row, "_iID"), TargetId = target.Role!.Id,
            TargetPos = target.Position.Clone(), Face = MonsterService.Angle(source.Position.X, source.Position.Z, target.Position.X, target.Position.Z),
        };
        if (!await AdmitAutomaticCostAsync(source, row, request, session)) { _eventAttacks.Remove(source); return; }
        await StartCastAsync(source, row, request, session);
    }
    private async Task HitEventPlayersAsync(GameSession source, GameSession[] targets, JsonElement row,
        int skillId, int show, long session, Func<bool> currentSource, Action<long, long>? onHit = null, Vec3? center = null,
        long scheduledBulletTarget = 0, Action<long, long, DamageHitResult>? onCommittedHit = null,
        Action<GameSession, SkillHitInfo, long>? onPublishedHit = null)
    {
        if (FirstNumber(row, "_kDamageParam1") is not > 0 && FirstNumber(row, "_kDamageParam2") is not > 0)
        {
            foreach (var target in targets) await PvpHitDebuffsAsync(source, target, row, currentSource);
            return;
        }
        foreach (var target in targets)
        {
            if (!currentSource()) break;
            if (!CanPlayerHit(source, target)) continue;
            bool eventFight = CanEventHit(source, target);
            var attackerRole = source.Role!;
            var targetRole = target.Role!;
            long sceneId = source.WorldScene;
            long sourceGeneration = source.SceneGeneration, targetGeneration = target.SceneGeneration;
            var sourceAttachment = source.Attachment;
            var targetAttachment = target.Attachment;
            bool SameHitEpoch() => source.SceneGeneration == sourceGeneration && target.SceneGeneration == targetGeneration
                && ReferenceEquals(source.Attachment, sourceAttachment) && sourceAttachment?.Cancelled != true
                && ReferenceEquals(target.Attachment, targetAttachment) && targetAttachment?.Cancelled != true;
            var victimCurrent = CaptureVictimHit(target);
            var (damage, result) = PlayerHitDamage(source, target, row);
            if (damage > 0) foreach (var adjust in PvpDamageAdjusters) damage = Math.Max(0, adjust(source, row, result, damage));
            var committed = await CommitPlayerDamageAsync(target, damage, row,
                () => currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)
                    && CanPlayerHit(source, target), sourceElement: AttackElement(source, row),
                    directHit: !new Data.Config.SkillConfig(row).IsFixedHit);
            if (committed == null) continue;
            (damage, long hp, long effective, long shieldDamage) = committed.Value;
            onCommittedHit?.Invoke(targetRole.Id, damage + shieldDamage, result);
            onHit?.Invoke(targetRole.Id, damage);
            try
            {
                if (effective > 0 && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId))
                {
                    foreach (var handler in PvpDealt) handler(source, target, effective, hp == 0);
                    foreach (var handler in PvpDealtAsync) await handler(source, target, effective, hp == 0);
                    foreach (var handler in PvpHitLanded) await handler(source, target, row, effective, hp == 0);
                    await ReactToHitAsync(target, null, source, row, effective);
                    await HolyLightBreaksKyrieAsync(target, row);
                }
                var hit = new SkillHitInfo
                {
                    SrcID = attackerRole.Id, SkillId = skillId, ShowPoint = show, Split = HitSplit(row),
                    SessionId = session, SrcElement = AttackElement(source, row),
                    Dsts = { new DamageInfo { DstID = targetRole.Id, Damage = damage, SheildDamage = shieldDamage, HitResult = result,
                    DstElement = DefenceElement(target), IsDead = hp == 0 } }
                };
                var sourceViewers = CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)
                    ? _world.Viewers(source).Append(source) : [];
                var hitViewers = sourceViewers.Concat(_world.Viewers(target)).Append(target).Distinct().ToArray();
                await SendHitBulletsAsync(hitViewers, row, hit, attackerRole.Id, scheduledBulletTarget);
                foreach (var viewer in hitViewers) await viewer.SendGameAsync(SkillHit, hit);
                if (!ReferenceEquals(target.Role, targetRole)) continue;
                await _character.SetHpSpAsync(target, null, null); // publish current HP after the serialized reservation
                if (!ReferenceEquals(target.Role, targetRole)) continue;
                if (CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)) await FightingAsync(source);
                await FightingAsync(target);
                // The knockback or pull of a hit that left the target alive, as the same skill moves a monster.
                if (hp > 0 && currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId))
                    await DisplacePlayerAsync(source, target, row, skillId, center);
                if (hp > 0) await PvpHitDebuffsAsync(source, target, row,
                    () => currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId));
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
                if (effective > 0 && victimCurrent())
                    foreach (var handler in PlayerTakenPublished)
                    {
                        if (!victimCurrent()) break;
                        try { await handler(target, effective, victimCurrent); }
                        catch (Exception ex) { _log.LogError(ex, "Victim reaction failed after publication for {Target}", targetRole.Id); }
                    }
                if (SameHitEpoch() && currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId))
                    onPublishedHit?.Invoke(target, hit, effective);
                if (effective > 0)
                    foreach (var handler in PvpSkillHitPublished)
                    {
                        if (!SameHitEpoch() || !CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId)) break;
                        try { await handler(source, target, row, effective, result); }
                        catch (Exception ex) { _log.LogError(ex, "Player hit follow-up failed after publication for {Target}", targetRole.Id); }
                    }
                await GuardianPvpFollowUpsAsync(source, target, targetRole, row, effective, hp == 0,
                    () => currentSource() && CurrentPlayerHit(source, target, attackerRole, targetRole, sceneId));
            }
            finally { if (hp == 0) ReleaseDeathPublication(target); }
        }
    }

    /// <summary>The row's _kBuff2 on the player it hit, rolled as on a monster. A guardian effect has its own path
    /// (GuardianPlayerDebuffsAsync). After the hit, so the hit does not break a Sleep it brought itself.</summary>
    private async Task PvpHitDebuffsAsync(GameSession source, GameSession target, JsonElement row, Func<bool> current)
    {
        if (IsGuardianEffect(row) || Script(row) == "HolyLight2") return; // Signum Crucis reaches Undead and Demons, and players are Demi-Human
        foreach (var tuple in HostileBuffs(row))
            if (current() && CanPlayerHit(source, target) && RollHostileBuff(tuple, source, null, target, null, out int id, out int duration))
                await _buffs.AddAsync(target, id, duration, source.Role!.Id);
    }

    /// <summary>A card's status on the player it hit: the card rolled its own chance, and (inferred) the status then meets the target's
    /// BuffTagConfig resistance as a skill's _kBuff2 would (<see cref="RollHostileBuff"/>).</summary>
    internal async Task LandCardStatusAsync(GameSession source, GameSession target, int id, int ms)
    {
        if (RollHostileBuff(JsonSerializer.SerializeToElement(new[] { id, 10000, ms }), source, null, target, null, out int buff, out int duration))
            await _buffs.AddAsync(target, buff, duration, source.Role!.Id);
    }

    private static IEnumerable<JsonElement> HostileBuffs(JsonElement row) => Tuples(row, "_kBuff2");

    private void TickEventPvP(long now)
    {
        foreach (var (source, attack) in _eventAttacks.ToArray())
        {
            if (!CanPlayerHit(source, attack.Target) || !CanSee(source, attack.Target) || PlayerDistance(source, attack.Target) > 30
                || MonsterService.Distance(attack.StartX, attack.StartZ, source.Position.X, source.Position.Z) > 1.5f)
            { _eventAttacks.Remove(source); continue; }
            if (now < attack.NextSwing || !SwingPlayedOut(source.SkillCast, now) || _data.Row("SkillConfig", attack.SkillId) is not { } row
                || PlayerDistance(source, attack.Target) > Range(row) + 1.5f) continue;
            _eventAttacks[source] = attack with { NextSwing = NextSwing(attack.NextSwing, now, SkillMs(source, row)) };
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
            source.Progress!.Level, AttackElement(source, row))
        {
            FixedDamage = (source.GmCombat & GmCombatSwitches.FixedDamage) != 0,
            Attributes = PlayerAttributes(source, attack), Race = PlayerRace, Size = PlayerSize,
        };
        var b = new Fighter(0, 0, 0, 0, target.Progress!.Level, DefenceElement(target))
        {
            Def = defend[AttributeId.Def], Mdef = defend[AttributeId.Mdef], Flee = defend[AttributeId.Flee], ResCri = defend[AttributeId.ResCri],
            FixedDamage = (target.GmCombat & GmCombatSwitches.FixedDamage) != 0,
            Attributes = PlayerAttributes(target, defend), Race = PlayerRace, Size = PlayerSize,
        };
        int branch = AuthoredSourceDamageBranch(source, row) ?? (Script(row) == "Soul_Strike" && b.Element == 10
            || Script(row) is "Magnus_Exorcismus" or "Magnus_ExorcismusBD" && b.Element is 8 or 10 ? 1
            : Math.Max(SonicBlowBranch(source, defend[AttributeId.Hp], defend[AttributeId.MaxHp], row), DamageBranch(source, null, row)));
        var (damage, result) = Damage(a, b, row, branch, bonusCrit: GuardianPetRules.CriticalChance(row),
            forceCrit: GuardianPetRules.CriticalAtHp(row, defend[AttributeId.Hp], defend[AttributeId.MaxHp]));
        damage = GuardianPetRules.DirectDamage(row, damage);
        damage = GuardianOutgoingDamage(source, row, damage);
        damage = GuardianControlledDamage(source, GuardianControlTarget(target), damage);
        if (result != DamageHitResult.Miss) damage = ApplyPlayerRestraints(source, target, damage);
        if (row.TryGetProperty("_ro3GuardianFixedHit", out var fixedValue))
        { damage = fixedValue.GetInt64(); result = DamageHitResult.Normal; }
        if (damage > 0)
            foreach (var adjust in PvpOutgoingDamageAdjusters) damage = Math.Max(0, adjust(source, target, row, result, damage));
        return (AdjustIncomingSkillDamage(target, row, result, damage), result);
    }
}
