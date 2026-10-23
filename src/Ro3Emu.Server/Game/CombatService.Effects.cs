using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>An owned skill NPC/trigger. NPCConfig identity and MG_Entity.owner survive AOI re-entry.</summary>
public sealed class SkillObject : WorldUnit
{
    public required GameSession OwnerSession { get; init; }
    public required long OwnerId { get; init; }
    public required int NpcId { get; init; }
    public required int EntityType { get; init; }
    public required ActiveSkillCast Cast { get; init; }
    public required long ExpiresMs { get; init; }
    public long NextTickMs { get; set; }
    public int IntervalMs { get; init; }
    public float Radius { get; init; }
    public bool Trap { get; init; }
    public bool Busy { get; set; }
    public bool Removing { get; set; }
    public int Charges { get; set; }
    public int ProtectionDistanceType { get; init; }
    public HashSet<int> Skills { get; } = new();

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = OwnerId,
        AiMasterEntityId = (ulong)OwnerId, EntityCamp = 1, State = EntityState.Alive, BornTimeMs = Cast.StartMs,
        Face = Face, MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs },
        NotifyType = EntityBase.Types.NotifyType.All,
    };
}

public sealed partial class CombatService
{
    private long _skillObjectIds = 0x600000000000L;

    private async Task ResolveCastAsync(GameSession s, ActiveSkillCast action)
    {
        var row = action.Row;
        if (!CurrentCastMap(s, action)) return;
        foreach (var handler in CastLanded)
        {
            await handler(s, row);
            if (!CurrentCastMap(s, action)) return;
        }
        if (Script(row) == "Resurrection")
        {
            var target = FindPlayer(s, action.Request.TargetId, dead: true);
            if (target == null || target.SceneLoading || _buffs.HasForbidden(target, ForbiddenState.Revive)
                || s.WorldState.ResurrectionCastId != action.Id
                || Distance(s.Position, target.Position) > Range(row) + 1.5f) return;
            s.WorldState.ResurrectionCastId = 0;
            if (action.Request.SkillId == PublicSkill("WorldTreeSkillId"))
            {
                if (ReviveItemSkill != null && await ReviveItemSkill(s, target, row))
                    await _buffs.NotifyWorldTreeReviveAsync(s, target.Role!.Name);
            }
            else if (ReviveOffer != null) await ReviveOffer(s, target, row);
            return;
        }
        if (await CreateSkillObjectAsync(s, action))
        {
            // A field resolves its effects later; "After using [Meteor Storm] ..." talent procs centre on its target point.
            if (!action.Request.IsExtraSkill)
                foreach (var handler in CastResolved)
                {
                    if (!CurrentCastMap(s, action)) break;
                    await handler(s, action, [], new SkillHitInfo());
                }
            return;
        }
        if (!CurrentCastMap(s, action)) return;
        // SkillConfig._bMovement is enum_SkillLockMoveType (may the caster walk while casting), not a displacement.
        await MoveCasterAsync(s, action);
        if (!CurrentCastMap(s, action)) return;
        await ApplyEffectsAsync(s, action, action.Request.TargetPos ?? action.Origin);
    }

    private List<MonsterUnit> SelectEnemies(GameSession s, ActiveSkillCast action, Vec3 center, float? radiusOverride = null)
    {
        var row = action.Row;
        int max = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
        if (GameData.Int(row, "_iRangeType") != 2 && radiusOverride == null)
        {
            var target = FindMonster(s.WorldScene, action.Request.TargetId);
            return target != null && target.Hp > 0 && CanHitMonster(s, target) && Relation(row, false, race: target.Template.Race) ? [target] : [];
        }
        if (radiusOverride == null && GameData.Int(row, "_iRelease") is not (SkillRelease.Lock or SkillRelease.DragRect
            or SkillRelease.DragCircle)) center = action.Origin;
        float height = GameData.Int(row, "_iAreaHeightRange") / 100f;
        // A radius override is a plain circle around the given center; otherwise the row's own area.
        bool InShape(MonsterUnit m) => radiusOverride is { } radius
            ? (height <= 0 || MathF.Abs(m.Y - center.Y) <= height) && Distance(center, m.Pos) <= radius + 0.1f
            : SkillAreaContains(row, action.Origin, center, action.Request.Face, m.Pos);
        return _world.Map(s.WorldScene).Units.OfType<MonsterUnit>().Where(m => m.Hp > 0 && CanHitMonster(s, m) && Relation(row, false, race: m.Template.Race) && InShape(m))
            .OrderBy(m => Distance(center, m.Pos)).ThenBy(m => m.Uid).Take(max).ToList();
    }

    private async Task ApplyEffectsAsync(GameSession s, ActiveSkillCast action, Vec3 center, SkillObject? field = null,
        IReadOnlyList<MonsterUnit>? chosenTargets = null, IReadOnlyList<GameSession>? chosenAllies = null)
    {
        if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene || s.SceneLoading
            || s.SceneGeneration != action.SceneGeneration || field?.Removing == true
            || !ReferenceEquals(s.Attachment, action.Attachment) || action.Attachment?.Cancelled == true
            || action.SourceEligible?.Invoke() == false) return;
        var row = action.Row;
        var castingRole = s.Role;
        bool CurrentCaster() => ReferenceEquals(s.Role, castingRole) && !s.SceneLoading
            && s.SceneGeneration == action.SceneGeneration && (field != null ? !field.Removing : !action.Cancelled)
            && s.WorldScene == action.WorldScene && _sessions.All().Contains(s) && (action.SourceEligible?.Invoke() ?? true);
        bool CanApplyMoreEffects() => CurrentCaster() && ReferenceEquals(s.Attachment, action.Attachment)
            && action.Attachment?.Cancelled != true;
        // A published death still owes its callbacks after the cast/field is cancelled.
        // A cancelled but unchanged lease can finish committed consequences under its admitted owner.
        bool CurrentKillRecipient() => ReferenceEquals(s.Role, castingRole) && !s.SceneLoading
            && s.SceneGeneration == action.SceneGeneration && s.WorldScene == action.WorldScene
            && ReferenceEquals(s.Attachment, action.Attachment)
            && _sessions.All().Contains(s);
        string script = Script(row);
        var targets = chosenTargets?.Where(m => m.Hp > 0 && CanHitMonster(s, m)).ToList()
            ?? SelectEnemies(s, action, center, field?.Trap == true ? field.Radius : null);
        var playerTargets = EffectPlayerTargets(s, action, center);
        var alreadySlowed = GuardianPreviouslySlowed(row, targets, playerTargets);
        if (playerTargets.Length > 0)
        {
            int maxTargets = checked((int)Math.Max(1, GameData.Int(row, "_iTargetMax")));
            var selected = targets.Select(m => (Id: m.Uid, Distance: MonsterService.Distance(m, s)))
                .Concat(playerTargets.Select(p => (Id: p.Role!.Id, Distance: PlayerDistance(s, p))))
                .OrderBy(p => p.Distance).ThenBy(p => p.Id).Take(maxTargets).Select(p => p.Id).ToHashSet();
            targets.RemoveAll(m => !selected.Contains(m.Uid));
            await HitEventPlayersAsync(s, playerTargets.Where(p => selected.Contains(p.Role!.Id)).ToArray(),
                row, action.Request.SkillId, HitEffect(GameData.Int(row, "_iScriptShow")), action.Id, CanApplyMoreEffects,
                action.GuardianDamage == null ? null : (id, amount) => RecordGuardianDamage(action, id, amount));
            if (IsGuardianEffect(row))
                await GuardianPlayerDebuffsAsync(s, row, playerTargets.Where(p => selected.Contains(p.Role!.Id)),
                    alreadySlowed, castingRole.Id, CanApplyMoreEffects);
        }
        if (!CanApplyMoreEffects()) return; // player publication can yield before any monster hit is committed
        // Allies a caller chose (a talent heal's proc row has no selector template) make the cast friendly.
        bool friendly = chosenAllies != null || Relation(row, true, true) || Relation(row, true);
        var allies = new List<GameSession>();
        if (friendly)
        {
            if (chosenAllies != null) allies.AddRange(chosenAllies.Where(p => !p.Dead && p.WorldScene == s.WorldScene));
            else if (GameData.Int(row, "_iRelease") == 1)
            {
                var ally = action.Request.TargetId == s.Role.Id ? s : FindPlayer(s, action.Request.TargetId);
                if (ally != null && Relation(row, true, ally == s)) allies.Add(ally);
            }
            else
            {
                float radius = field?.Radius ?? (float)(Numbers(row, "_kRange").FirstOrDefault(0) / 100);
                allies.AddRange(_world.PlayersIn(s.WorldScene).Where(p => !p.Dead && Relation(row, true, p == s)
                    && (p == s || radius > 0 && Distance(p.Position, center) <= radius))
                    .OrderBy(p => Distance(p.Position, center)).Take((int)Math.Max(1, GameData.Int(row, "_iTargetMax"))));
                if (allies.Count == 0 && Relation(row, true, true)) allies.Add(s);
            }
        }
        var stats = _character.Stats(s);
        var attacker = new Fighter(stats[103], stats[104], stats[109], stats[111], s.Progress!.Level, 1);
        var hit = new SkillHitInfo
        {
            SrcID = field?.Uid ?? action.SourceId, SkillId = action.Request.SkillId, SessionId = action.Id,
            RootCtxGuid = action.Id, ComboIndex = action.Request.ComboIndex, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
            Split = HitSplit(row),
            SrcElement = (int)Math.Max(1, GameData.Int(row, "_iEleType")),
        };
        var killed = new List<MonsterUnit>();
        var processed = new List<MonsterUnit>();
        if (TargetCountRow != null && targets.Count > 0) row = TargetCountRow(s, row, targets.Count);
        bool damage = Numbers(row, "_kDamageParam1").Any(v => v > 0) || Numbers(row, "_kDamageParam2").Any(v => v > 0);
        try
        {
            foreach (var target in targets)
            {
                if (!CanApplyMoreEffects()) break; // a previous target's awaited callback may have invalidated the caster
                if (target.Dead || target.Hp <= 0) continue; // another awaited cast may already own this death source
                processed.Add(target);
                if (damage)
                {
                    var (amount, result) = MonsterHitDamage(s, attacker, target, row, script);
                    long beforeHp = target.Hp;
                    target.Hp = Math.Max(0, beforeHp - amount);
                    long effective = beforeHp - target.Hp;
                    RecordGuardianDamage(action, target.Uid, amount);
                    if (target.Hp == 0) killed.Add(target); // reserve before awaited damage callbacks
                    if (effective > 0 && target.Hp > 0) await _buffs.BreakHitControlsAsync(target);
                    await SettleMonsterHitAsync(s, target, beforeHp, amount);
                    if (CanApplyMoreEffects())
                        foreach (var handler in DealtAsync)
                        {
                            if (!CanApplyMoreEffects()) break;
                            await handler(s, target, row, amount);
                        }
                    // The hit is already committed; an unchanged (even cancelled) lease
                    // still owns its damage callback, while a replacement lease does not.
                    if (effective > 0 && CurrentKillRecipient())
                        foreach (var handler in MonsterDamageApplied)
                        {
                            if (!CurrentKillRecipient()) break;
                            try { await handler(s, target, effective); }
                            catch (Exception ex) { _log.LogError(ex, "Monster damage callback failed for {Uid}", target.Uid); }
                        }
                    hit.Dsts.Add(new DamageInfo { DstID = target.Uid, Damage = amount, HitResult = result, DstElement = target.Template.Element, IsDead = target.Hp == 0 });
                }
                if (CanApplyMoreEffects())
                    foreach (var tuple in Tuples(row, "_kBuff2"))
                        if (CanApplyMoreEffects() && GuardianAllowsHostileBuff(row, tuple, alreadySlowed.Contains(target.Uid))
                            && RollHostileBuff(tuple, s, null, null, target, out int id, out int duration))
                            await _buffs.AddSupportAsync(target, id, duration, castingRole.Id,
                                attributeMultiplier: GuardianPetRules.BuffMultiplier(row), guardianOwned: IsGuardianEffect(row));
                if (CanApplyMoreEffects() && script is ("Provoke" or "Taunt")) _monsters.Engage(target, s);
                if (CanApplyMoreEffects() && !target.Dead && target.Hp > 0) await DisplaceTargetAsync(s, action, target);
            }
            // Buff1 is self for hostile selectors, but actual friend recipients for ally selectors (Detoxify, Blessing).
            foreach (var recipient in friendly ? allies : new List<GameSession> { s })
            {
                if (!CanApplyMoreEffects()) break;
                if (field == null)
                    foreach (var tuple in Tuples(row, "_kBuff1"))
                        if (CanApplyMoreEffects() && RollBuff(tuple, out int id, out int duration)
                            && SkillBuffAllowed?.Invoke(s, recipient, id) != false)
                            await _buffs.AddAsync(recipient, id, duration, castingRole.Id);
                if (friendly && damage)
                {
                    double multiplier = Numbers(row, "_kDamageParam1").FirstOrDefault(), flat = Numbers(row, "_kDamageParam2").FirstOrDefault();
                    // Heal's friendly branch is P1[0]/P2[0]; its enemy branch is [1] (description + select race mask).
                    long amount = (long)Math.Max(0, Math.Round(stats[104] * multiplier / 10000 + flat));
                    foreach (var modifier in ExtraHeal) amount = modifier(s, amount);
                    var before = _character.Stats(recipient);
                    amount = ModifiedHealing(_buffs.GuardianAttributes(s), _buffs.GuardianAttributes(recipient), amount);
                    long actual = Math.Min(amount, Math.Max(0, before[101] - before[2000]));
                    await _character.SetHpSpAsync(recipient, before[2000] + amount, null);
                    hit.Dsts.Add(new DamageInfo { DstID = recipient.Role!.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 });
                    foreach (var handler in Healed) handler(s, actual);
                    foreach (var handler in HealedAsync) await handler(s, actual);
                }
                if (script is "Detoxify" or "HealingSkills")
                {
                    // (inferred) cleanse exact described groups rather than removing every beneficial/hostile status.
                    int[] groups = script == "Detoxify" ? [9030] : [9023, 9025];
                    foreach (var buff in recipient.Buffs.Where(b => _data.Row("BuffConfig", b.ConfigId) is { } br && groups.Contains((int)GameData.Int(br, "_iBuffID"))).ToArray())
                        await _buffs.RemoveAsync(recipient, buff);
                }
            }
            if (hit.Dsts.Count > 0)
            {
                foreach (var result in hit.Dsts)
                {
                    if (!CurrentCaster()) break;
                    await RecordDamageStatisticAsync(s, new DamageStatistics
                    {
                        DamageSourceId = hit.SrcID, DamageTargetId = result.DstID, DamageValue = result.Damage,
                        DamageBeShield = result.SheildDamage, DamageType = result.DamageType, HitResult = result.HitResult, SkillId = hit.SkillId,
                    });
                }
                if (CurrentCaster() && hit.Dsts.Any(d => d.HitResult != DamageHitResult.Miss)) await _buffs.RecoverEnergyOnHitAsync(s, row);
                var sourceViewers = CurrentCaster() ? _world.Viewers(s).Append(s) : Enumerable.Empty<GameSession>();
                foreach (var viewer in processed.SelectMany(t => t.Viewers).Concat(sourceViewers).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
                if (CurrentCaster() && field?.Trap == true)
                    await ToPlayerAndViewersAsync(s, TrapDamage, new TrapDamageNtf { SkillConfigId = action.Request.SkillId, DamageValue = hit.Dsts.Sum(d => d.Damage), Pos = center.Clone() });
            }
            foreach (var target in processed)
            {
                if (!damage) continue;
                if (target.Hp == 0 && !target.DeathDurable) continue; // another cast is still committing that death
                await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
                await FightingAsync(target);
                if (target.Hp > 0 && CurrentCaster()) _monsters.Engage(target, s);
            }
            if (processed.Count > 0 && CurrentCaster()) await FightingAsync(s);
            foreach (var target in killed.Where(target => target.Hp == 0 && target.DeathDurable))
            {
                _log.LogInformation("{Name} killed {Monster} ({Npc})", castingRole.Name, target.Uid, target.Template.NpcId);
                await _monsters.KillAsync(target);
                foreach (var other in _sessions.All().Where(o => o.Attack?.Target == target))
                {
                    other.Attack = null;
                    await other.SendGameAsync(SkillEnd, new SkillEndNotify
                    {
                        SrcID = other.Role!.Id, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                        InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                    });
                }
                await PublishDurableKillAsync(s, target, CurrentKillRecipient);
                await FinalizeLethalPublicationAsync(target);
            }
        }
        finally
        {
            foreach (var target in killed.Where(target => target.Hp == 0 && target.DeathDurable))
            {
                if (!target.Dead)
                    try { await _monsters.KillAsync(target); }
                    catch (Exception ex) { _log.LogError(ex, "Could not finalize reserved monster death for {Uid}", target.Uid); }
                // A later target/effect can fail after this source committed. Its room observers must still run.
                await PublishDurableKillAsync(s, target, CurrentKillRecipient);
                await FinalizeLethalPublicationAsync(target);
            }
        }
        // A player's own cast: not a field, not an extra cast such as a talent proc, so procs never chain.
        if (field == null && !action.Request.IsExtraSkill)
            foreach (var handler in CastResolved)
            {
                if (!CanApplyMoreEffects()) break;
                await handler(s, action, processed, hit);
            }
    }

    /// <summary>
    /// An extra cast of a SkillConfig row on monsters the caller chose, through the ordinary effect path (damage, kills,
    /// statistics, 31101). It is an extra skill: it pays nothing, starts no cooldown and triggers no CastResolved.
    /// </summary>
    internal Task CastExtraAsync(GameSession s, JsonElement row, IReadOnlyList<MonsterUnit> targets)
    {
        if (s.Role == null || targets.Count == 0) return Task.CompletedTask;
        var target = targets[0];
        var action = new ActiveSkillCast
        {
            Id = ++_sessionIds, OwnerId = s.Role.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Row = ExtraRow(s, row),
            Request = new CastSkillReq
            {
                SkillId = (int)new SkillConfig(row).Id, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = s.Face,
                IsExtraSkill = true,
            },
            Origin = s.Position.Clone(), StartMs = Now, ChantMs = 0, ActionMs = 1,
        };
        return ApplyEffectsAsync(s, action, target.Pos.Clone(), chosenTargets: targets);
    }

    /// <summary>An extra cast of a friendly SkillConfig row (a heal) on the allies the caller chose; like
    /// <see cref="CastExtraAsync"/> it pays nothing and triggers no CastResolved.</summary>
    internal Task CastExtraOnAlliesAsync(GameSession s, JsonElement row, IReadOnlyList<GameSession> allies)
    {
        if (s.Role == null || allies.Count == 0) return Task.CompletedTask;
        var action = new ActiveSkillCast
        {
            Id = ++_sessionIds, OwnerId = s.Role.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Row = ExtraRow(s, row),
            Request = new CastSkillReq
            {
                SkillId = (int)new SkillConfig(row).Id, TargetId = allies[0].Role!.Id, TargetPos = allies[0].Position.Clone(),
                Face = s.Face, IsExtraSkill = true,
            },
            Origin = s.Position.Clone(), StartMs = Now, ChantMs = 0, ActionMs = 1,
        };
        return ApplyEffectsAsync(s, action, allies[0].Position.Clone(), chosenTargets: [], chosenAllies: allies);
    }

    /// <summary>A ground zone of an NPC that applies <paramref name="row"/> every interval within the row's area at
    /// <paramref name="center"/> (TalentProcs: Ember Zone). Like an extra cast it triggers no CastResolved.</summary>
    internal async Task CreateZoneAsync(GameSession s, JsonElement row, int npc, Vec3 center, int intervalMs, int durationMs)
    {
        if (s.Role == null || _data.Row("NPCConfig", npc) is not { } npcRow) return;
        var action = new ActiveSkillCast
        {
            Id = ++_sessionIds, OwnerId = s.Role.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Row = ExtraRow(s, row),
            Request = new CastSkillReq { SkillId = (int)new SkillConfig(row).Id, TargetPos = center.Clone(), Face = s.Face, IsExtraSkill = true },
            Origin = s.Position.Clone(), StartMs = Now, ChantMs = 0, ActionMs = 1,
        };
        var unit = new SkillObject
        {
            Uid = ++_skillObjectIds, SceneEntry = s.SceneEntry, InstanceId = s.InstanceId, RuntimeMap = s.RuntimeMap,
            OwnerSession = s, OwnerId = s.Role.Id, NpcId = npc, EntityType = (int)new NPCConfig(npcRow).NPCType, Cast = action,
            X = center.X, Y = center.Y, Z = center.Z, Face = s.Face, ExpiresMs = Now + durationMs,
            IntervalMs = Math.Max(50, intervalMs), NextTickMs = Now + intervalMs,
            Radius = (new SkillConfig(row).Range is [var range, ..] ? range : 150) / 100f,
        };
        s.SkillObjects.Add(unit);
        await _world.AddUnitAsync(unit);
    }

    /// <summary>
    /// An extra cast's row with the session's live modifiers. Not the per-id cached SkillParameterRules.Effective: a
    /// described hit (SkillConfig.AsDescribedHit) keeps its trigger's id, and that cache would return the trigger's row.
    /// </summary>
    internal JsonElement ExtraRow(GameSession s, JsonElement row) => SkillParameterRules.Effective(row,
        SkillParameterRules.Active(s, _data), [.. new SkillConfig(row).SkillCategory.Select(tag => (int)tag)]);

    /// <summary>Living players of this caster's scene within a radius of a point, nearest first (talent heals; every player
    /// counts as an ally, as a friendly area cast's selection does outside events).</summary>
    internal List<GameSession> AlliesNear(GameSession s, Vec3 center, float radius) =>
    [
        .. _world.PlayersIn(s.WorldScene).Where(p => !p.Dead && p.Role != null && Distance(p.Position, center) <= radius + 0.1f)
            .OrderBy(p => Distance(p.Position, center)).ThenBy(p => p.Role!.Id),
    ];

    /// <summary>Living monsters this caster may hit within a radius of a point, nearest first.</summary>
    internal List<MonsterUnit> MonstersNear(GameSession s, Vec3 center, float radius) =>
    [
        .. _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
            .Where(m => m.Hp > 0 && CanHitMonster(s, m) && Distance(center, m.Pos) <= radius + 0.1f)
            .OrderBy(m => Distance(center, m.Pos)).ThenBy(m => m.Uid),
    ];

    private bool RollBuff(JsonElement tuple, out int id, out int duration)
    {
        id = duration = 0;
        if (tuple.GetArrayLength() < 3) return false;
        id = (int)(GameData.Number(tuple[0]) ?? 0);
        duration = (int)(GameData.Number(tuple[2]) ?? 0);
        return id > 0 && Rolls.Proc(10000) < (GameData.Number(tuple[1]) ?? 0);
    }

    internal enum DisplaceKind { CasterForward, CasterBack, TargetPush, TargetPull }

    /// <summary>
    /// Skills whose show has a displacement EffectData (m_kPoint.m_bMovement) and whose description names the
    /// distance: _kDescData index, or -1 for a pull "in front of you". Who moves and which way comes from the
    /// description and the effect's curve name (击退 knockback, 拉人 pull); the server choice of skills is (inferred).
    /// ShieldsProtection's 1161341 knockback belongs to its periodic aura, which is not simulated.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (DisplaceKind Kind, int DescIndex)> Displacements =
        new Dictionary<string, (DisplaceKind, int)>
        {
            ["BackSlide"] = (DisplaceKind.CasterBack, 0),
            ["RetreatingShadows"] = (DisplaceKind.CasterBack, 0),
            ["RetreatingShadowsS1"] = (DisplaceKind.CasterBack, 0),
            ["WolvesDash"] = (DisplaceKind.CasterForward, 0),
            ["HighspeedSlide"] = (DisplaceKind.CasterForward, 0),
            ["HighspeedSlideMecha"] = (DisplaceKind.CasterForward, 0),
            ["PinpointAttack"] = (DisplaceKind.CasterForward, 3),
            ["ArrowRepel"] = (DisplaceKind.TargetPush, 3),
            ["PileBunker"] = (DisplaceKind.TargetPush, 5),
            ["PhantomThrust"] = (DisplaceKind.TargetPull, -1),
            ["PetSkill_2_2_BuChongJin"] = (DisplaceKind.TargetPush, 3),
            ["Pet_AncientTree_23452"] = (DisplaceKind.TargetPull, -1),
        };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (int Id, int Ms)> _movementEffects = new();

    /// <summary>
    /// The show's displacement EffectData: the lowest id in [scriptShow*100, scriptShow*100+99] with m_kPoint.m_bMovement
    /// (BackSlide 15104 -> 1510403, PileBunker 16314 -> 1631405 knockback). The client's SkillStartMove ignores a 31112
    /// without it (LC_PerformEffect_Agent.lua SkillStartMove returns on effectId nil/0).
    /// </summary>
    internal (int Id, int Ms) MovementEffect(long scriptShow) => _movementEffects.GetOrAdd(scriptShow, show =>
    {
        var (id, ms, _) = FindMovementEffect(_data, show);
        return (id, ms);
    });

    /// <summary>The show's displacement EffectData (see <see cref="MovementEffect"/>), with its move curve's name.</summary>
    internal static (int Id, int Ms, string Curve) FindMovementEffect(GameData data, long show)
    {
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        for (long id = show * 100; id < show * 100 + 100; id++)
            if (effects.TryGetValue(id.ToString(), out var e) && e.TryGetProperty("m_kPoint", out var point)
                && point.TryGetProperty("m_bMovement", out var moves) && moves.ValueKind == JsonValueKind.True)
                return ((int)id, point.TryGetProperty("m_iMovementTime", out var ms) && ms.TryGetInt32(out int t) ? t : 0,
                    point.TryGetProperty("m_kMoveCurveName", out var curve) && curve.ValueKind == JsonValueKind.String ? curve.GetString() ?? "" : "");
        return (0, 0, "");
    }

    private (DisplaceKind Kind, float Distance, int EffectId, int Ms)? Displacement(JsonElement row)
    {
        if (!Displacements.TryGetValue(Script(row), out var rule)) return null;
        var (effect, ms) = MovementEffect(GameData.Int(row, "_iScriptShow"));
        if (effect == 0) return null;
        float distance = rule.DescIndex < 0 ? 1 // (inferred) "pulling them in front of you": 1 m ahead of the caster
            : (float)Numbers(row, "_kDescData").ElementAtOrDefault(rule.DescIndex);
        return distance > 0 ? (rule.Kind, distance, effect, ms) : null;
    }

    private async Task MoveCasterAsync(GameSession s, ActiveSkillCast action)
    {
        if (Displacement(action.Row) is not { Kind: DisplaceKind.CasterForward or DisplaceKind.CasterBack } move) return;
        float face = action.Request.Face + (move.Kind == DisplaceKind.CasterBack ? 180 : 0);
        double angle = face * Math.PI / 180;
        var end = WalkableEnd(s.SceneEntry, s.Position, (float)Math.Sin(angle) * move.Distance, (float)Math.Cos(angle) * move.Distance);
        var start = s.Position.Clone();
        await ToPlayerAndViewersAsync(s, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)s.Role!.Id, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = move.EffectId, StartTimestampMs = Now, MoveType = 1, Face = action.Request.Face,
            MoveTimeMs = move.Ms > 0 ? move.Ms : action.ActionMs,
        });
        // MoveAsync updates persistent position and AOI membership from the same authoritative destination.
        s.PreviousPosition = start;
        s.Position = end;
        await _world.MoveAsync(s, new WalkReq { Pos = end, Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
    }

    private async Task DisplaceTargetAsync(GameSession s, ActiveSkillCast action, MonsterUnit target)
    {
        if (Displacement(action.Row) is not { Kind: DisplaceKind.TargetPush or DisplaceKind.TargetPull } move) return;
        var from = s.Position;
        float dx = target.X - from.X, dz = target.Z - from.Z;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.001f) return;
        // A push goes away from the caster; a pull stops the given distance in front of it.
        float distance = move.Kind == DisplaceKind.TargetPush ? move.Distance : -(length - move.Distance);
        if (move.Kind == DisplaceKind.TargetPull && length <= move.Distance) return;
        var start = target.Pos;
        var end = WalkableEnd(s.SceneEntry, start, dx / length * distance, dz / length * distance);
        target.Moving = false;
        target.X = end.X; target.Y = end.Y; target.Z = end.Z;
        await _world.ToUnitViewersAsync(target, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)target.Uid, StartPos = start, EndPos = end, SkillConfigId = action.Request.SkillId,
            EffectId = move.EffectId, MoveType = 2, StartTimestampMs = Now, Face = target.Face,
            MoveTimeMs = move.Ms > 0 ? move.Ms : 300,
        });
        await _world.MoveUnitAsync(target, new MovementChangeNtf { Uid = target.Uid, MovementInfo = target.CurrentMovement(Now) });
    }

    private Vec3 WalkableEnd(long sceneId, Vec3 start, float dx, float dz)
    {
        var nav = _worldData.Map(sceneId)?.Nav;
        var end = start.Clone();
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.25));
        for (int i = 1; i <= steps; i++)
        {
            float x = start.X + dx * i / steps, z = start.Z + dz * i / steps;
            if (nav != null && !nav.CanWalk(end.X, end.Y, end.Z, x, z)) break;
            end = new Vec3 { X = x, Y = nav?.WalkHeight(x, z, end.Y) ?? end.Y, Z = z };
        }
        return end;
    }

    /// <summary>
    /// Owned ground triggers use NPCConfig identities, not WidgetConfig. Fire_Wall/Safety_Wall/Pneuma tuples explicitly
    /// reference NPCs; Hunter trap NPCs match their localized skill names. Timing/limits below are inferred from the
    /// corresponding descriptions and _kLogicData, documented per family in docs/systems/skills-rest.md.
    /// </summary>
    private async Task<bool> CreateSkillObjectAsync(GameSession s, ActiveSkillCast action)
    {
        if (!CurrentCastMap(s, action)) return true;
        var row = action.Row;
        string script = Script(row);
        var logic = Numbers(row, "_kLogicData");
        int npc = 0, duration = 0, interval = 1000, protection = 0, charges = 0;
        bool trap = false;
        float radius = (float)(Numbers(row, "_kRange").FirstOrDefault(150) / 100);
        switch (script)
        {
            case "Fire_Wall" or "Quagmire" or "Fire_Pillar" or "Meteor_Storm" or "Lord_of_Vermilion" or "Storm_Gust" when logic.Length >= 3:
                npc = (int)logic[0]; interval = (int)logic[1]; duration = (int)logic[2]; break;
            case "Safety_Wall" or "Pneuma" when logic.Length >= 8:
                npc = (int)logic[5]; duration = (int)logic[2]; radius = (float)(logic[7] / 100);
                protection = script == "Safety_Wall" ? 1 : 2; charges = script == "Safety_Wall" ? (int)logic[1] : int.MaxValue; break;
            case "LandMine": npc = 22000016; trap = true; break;
            case "ClaymoreTrap": npc = 22000017; trap = true; break;
            case "FreezingTrap": npc = 22000018; trap = true; break;
            case "BlastMine": npc = 22000019; trap = true; break;
            default:
                if (script.Contains("Summon", StringComparison.OrdinalIgnoreCase))
                {
                    npc = logic.Select(v => (int)v).FirstOrDefault(id => _data.Row("NPCConfig", id) != null);
                    duration = 30000; // (inferred) bounded lifecycle for a named summon without an explicit lifetime tuple.
                }
                break;
        }
        if (npc == 0 || _data.Row("NPCConfig", npc) is not { } npcRow) return false;
        if (trap)
        {
            var desc = row.TryGetProperty("_kDescData", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().ToArray() : [];
            duration = desc.Length > 0 && double.TryParse(desc[^1].ToString(), System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? (int)(seconds * 1000) : 20000;
            radius = 3; interval = 100;
        }
        var point = action.Request.TargetPos ?? action.Origin;
        var unit = new SkillObject
        {
            Uid = ++_skillObjectIds, SceneEntry = s.SceneEntry, InstanceId = s.InstanceId, RuntimeMap = s.RuntimeMap,
            OwnerSession = s, OwnerId = s.Role!.Id,
            NpcId = npc, EntityType = (int)GameData.Int(npcRow, "_iNPCType"), Cast = action,
            X = point.X, Y = point.Y, Z = point.Z, Face = action.Request.Face, ExpiresMs = Now + Math.Max(1, duration),
            IntervalMs = Math.Max(50, interval), NextTickMs = Now, Radius = radius, Trap = trap,
            ProtectionDistanceType = protection, Charges = charges,
        };
        foreach (int skill in Numbers(npcRow, "_kSkills").Select(v => (int)v)) unit.Skills.Add(skill);
        s.SkillObjects.Add(unit);
        await _world.AddUnitAsync(unit);
        if (!CurrentCastMap(s, action)) { await RemoveSkillObjectAsync(unit); return true; }
        foreach (var tuple in Tuples(row, "_kBuff1"))
            if (RollBuff(tuple, out int buffId, out int buffDuration)) await _buffs.AddAsync(s, buffId, buffDuration, s.Role.Id);
        return true;
    }

    private async Task TickSkillObjectAsync(SkillObject unit)
    {
        if (unit.Busy || unit.Removing) return;
        unit.Busy = true;
        try
        {
            var owner = unit.OwnerSession;
            if (Now >= unit.ExpiresMs || owner.Role?.Id != unit.OwnerId || owner.SceneLoading || owner.Dead
                || owner.WorldScene != unit.WorldScene || owner.SceneGeneration != unit.Cast.SceneGeneration
                || !ReferenceEquals(owner.Attachment, unit.Cast.Attachment) || unit.Cast.Attachment?.Cancelled == true)
            {
                await RemoveSkillObjectAsync(unit);
                return;
            }
            if (Now < unit.NextTickMs) return;
            unit.NextTickMs = Now + unit.IntervalMs;
            if (unit.ProtectionDistanceType != 0) return;
            if (unit.Trap && !_world.Map(unit.WorldScene).Near(unit.X, unit.Z, 1.5f).OfType<MonsterUnit>().Any(m => !m.Dead && Distance(m.Pos, unit.Pos) <= 1.5f)) return;
            await ApplyEffectsAsync(owner, unit.Cast, unit.Pos, unit);
            if (unit.Trap) await RemoveSkillObjectAsync(unit);
        }
        finally { unit.Busy = false; }
    }

    private async Task RemoveSkillObjectAsync(SkillObject unit)
    {
        if (unit.Removing) return;
        unit.Removing = true;
        unit.OwnerSession.SkillObjects.Remove(unit);
        await _world.RemoveUnitAsync(unit);
    }

    /// <summary>Scene/role/disconnect teardown invalidates callbacks before despawning owned skill entities.</summary>
    public async Task ClearSkillsAsync(GameSession s)
    {
        _eventAttacks.Remove(s);
        s.WorldState.ResurrectionCastId = 0;
        s.Attack = null;
        if (s.SkillCast is { } cast) cast.Cancelled = true;
        s.SkillCast = null;
        foreach (var unit in s.SkillObjects.ToArray()) await RemoveSkillObjectAsync(unit);
    }

    private async Task<bool> GroundProtectsAsync(GameSession target, JsonElement? skill)
    {
        int distanceType = skill is { } row ? (int)GameData.Int(row, "_iDistanceType") : 1;
        var field = _world.Map(target.WorldScene).Units.OfType<SkillObject>().FirstOrDefault(u => !u.Removing && u.ExpiresMs > Now
            && u.ProtectionDistanceType == distanceType && u.Charges > 0 && Distance(u.Pos, target.Position) <= u.Radius);
        if (field == null) return false;
        if (--field.Charges == 0) await RemoveSkillObjectAsync(field);
        return true;
    }

    /// <summary>The event players a cast can hit: its area's, a guardian's own area selection, or its locked target.</summary>
    private GameSession[] EffectPlayerTargets(GameSession s, ActiveSkillCast action, Vec3 center) =>
        GameData.Int(action.Row, "_iRangeType") == SkillRelease.Area
            ? IsGuardianEffect(action.Row) ? GuardianPlayerTargets(s, action, center) : EventAreaTargets(s, action.Row)
            : EventTarget(s, action.Request.TargetId) is { } eventTarget
                && PlayerDistance(s, eventTarget) <= Range(action.Row) + 1.5f ? [eventTarget] : [];

    /// <summary>One monster's damage from a player's cast: the hit, the guardian factors, the shared adjusters and a
    /// guardian's resistance changes. A guardian's fixed hit takes only its direct-damage factor.</summary>
    private (long Amount, DamageHitResult Result) MonsterHitDamage(GameSession s, Fighter attacker, MonsterUnit target,
        JsonElement row, string script)
    {
        var (amount, result) = Damage(attacker, MonsterFighter(target), row, script == "Heal" ? 1 : SonicBlowBranch(s, target, row),
            GuardianPetRules.CriticalChance(row) + SkillParameterRules.CritChance(s, _data, row),
            GuardianPetRules.CriticalAtHp(row, target.Hp, target.Template.MaxHp));
        if (row.TryGetProperty("_ro3GuardianFixedHit", out var fixedValue))
            return (GuardianPetRules.DirectDamage(row, fixedValue.GetInt64()), DamageHitResult.Normal);
        amount = GuardianPetRules.DirectDamage(row, amount);
        if (amount > 0)
        {
            foreach (var modifier in DamageAdjusters) amount = Math.Max(0, modifier(s, target, row, result, amount));
            foreach (var extra in ExtraHitDamage) amount += Math.Max(0, extra(s, target, row, amount));
        }
        return (ApplyPetSupportResistance(amount, _buffs.GuardianUnitAttributes(target), row, attacker.Element), result);
    }

    /// <summary>Targets already slowed before this cast. Only Kuang Feng Xi Juan's second slow depends on that, so no
    /// other cast pays for the scan.</summary>
    private HashSet<long> GuardianPreviouslySlowed(JsonElement row, List<MonsterUnit> monsters, GameSession[] players)
    {
        if (GuardianPetRules.Script(row) != KuangFengXiJuan) return [];
        var slowed = monsters.Where(m => m.SlowMovementExpireMs > Now
            || _buffs.GuardianUnitAttributes(m).Any(c => c.Id == AttributeId.PerSpeed && c.Value < 0))
            .Select(m => m.Uid).ToHashSet();
        foreach (var player in players.Where(p => p.Buffs.Any(b => b.ExpireMs > Now
            && b.Changes.Any(c => c.Attr == AttributeId.PerSpeed && c.Value < 0)))) slowed.Add(player.Role!.Id);
        return slowed;
    }

    /// <summary>A guardian effect's hostile buffs on the players it hit.</summary>
    private async Task GuardianPlayerDebuffsAsync(GameSession s, JsonElement row, IEnumerable<GameSession> players,
        HashSet<long> alreadySlowed, long sourceRoleId, Func<bool> canApply)
    {
        foreach (var player in players.ToArray())
            foreach (var tuple in Tuples(row, "_kBuff2"))
                if (canApply() && CanPlayerHit(s, player)
                    && GuardianAllowsHostileBuff(row, tuple, alreadySlowed.Contains(player.Role!.Id))
                    && RollHostileBuff(GuardianPetRules.PlayerBuffTuple(row, tuple), s, null, player, null, out int id, out int duration))
                    await _buffs.AddGuardianAsync(player, id, duration, sourceRoleId,
                        attributeMultiplier: GuardianPetRules.BuffMultiplier(row));
    }
}
