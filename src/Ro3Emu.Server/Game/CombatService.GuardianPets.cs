using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private readonly PetService _pets;

    /// <summary>Hooks the guardian rules into the shared services: the pet wheel, hit and damage callbacks, unit
    /// attributes and controls, and the hidden-target check of monster aggro.</summary>
    private void WireGuardians()
    {
        _skills.PetWheel = _pets.GuardianWheel;
        _pets.Changed.Add(SyncGuardianWheelAsync);
        _scene.Loaded.Add(SyncGuardianWheelAsync);
        DealtAsync.Add(GuardianDamageReactionAsync);
        DealtAsync.Add(GuardianWindowMonsterHitAsync);
        DealtAsync.Add((_, target, _, damage) => damage > 0 ? _buffs.ConsumeGuardianGroupAsync(target, GuardianGroup.CatVulnerability) : Task.CompletedTask);
        TakenAsync.Add((_, target, amount) => GuardianTakenAsync(target, amount));
        DamageAdjusters.Add((s, _, row, _, damage) => GuardianOutgoingDamage(s, row, damage));
        DamageAdjusters.Add((s, target, _, _, damage) => GuardianControlledDamage(s, GuardianControlTarget(target), damage));
        _buffs.UnitChanged.Add(PublishMonsterAttributesAsync);
        _monsters.Forbidden = _buffs.UnitForbidden;
        _monsters.TargetVisible = (m, p) => !Stealthed(p) && _buffs.CanSeePosition(m, p.Position);
    }

    private Task SyncGuardianWheelAsync(GameSession session) => session.SkillPlans == null
        ? Task.CompletedTask : _skills.SyncAsync(session);

    private async Task CastGuardianAsync(GameSession s, CastSkillReq req)
    {
        var skill = _pets.Guardians(s).FirstOrDefault(g => g.IsPrimary && g.Id == req.SkillId && g.Kind != GuardianPetRules.Kind.Unsupported);
        int error = skill == null ? ErrorCodes.SkillNotLearned : GuardianError(s, skill, req);
        if (error != 0)
        {
            await RefuseCastAsync(s, req, error, 0);
            return;
        }
        if (skill != null) await BeginGuardianAsync(s, skill, req);
    }

    private int GuardianError(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req)
    {
        if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading || s.SceneRecoveryRequired
            || !_sessions.All().Contains(s) || s.Attachment?.Cancelled == true) return ErrorCodes.SkillBadState;
        if (!_pets.GuardianEquipped(s, skill)) return ErrorCodes.SkillNotLearned;
        if (req.OpUnitId != 0 && req.OpUnitId != s.Role.Id) return ErrorCodes.SkillRequirements;
        if (!float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos)) return ErrorCodes.Parameter;
        // (inferred) A family whose effect removes control, or Shinobi with a control to cleanse, may still be cast while
        // controlled: otherwise its cleanse could never be used when it is needed.
        if ((_buffs.HasForbidden(s, ForbiddenState.CastSkill) || _buffs.HasForbidden(s, ForbiddenState.NormalAttack)) && !GuardianPetRules.DispelsControl(skill)
            && !GuardianShinobiDispel(s, skill).Any()
            || MatchSkillRule?.Invoke(s, skill.Id) == false) return ErrorCodes.SkillBadState;
        if (GameData.Int(skill.Active, "_iBasicType") != 1 || GameData.Int(skill.Effect, "_iBasicType") != 1)
            return ErrorCodes.SkillPassive;
        // These families have no configured cost. Never let an extra cast bypass an unfamiliar cost shape.
        if (GuardianPetRules.HasCost(skill.Active) || GuardianPetRules.HasCost(skill.Effect)) return ErrorCodes.SkillRequirements;
        int group = (int)GameData.Int(skill.Active, "_iSkillID"), mode = (int)GameData.Int(skill.Active, "_iMode");
        if (Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now)
            return ErrorCodes.SkillCooldown;
        return skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff
            ? ValidateTarget(s, GuardianPetRules.TargetRow(skill), req, s.Position, 0) : 0;
    }

    private async Task BeginGuardianAsync(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req)
    {
        skill = GuardianPetRules.Prepare(_data, skill, _pets.Guardians(s));
        var action = GuardianAction(s, skill, req);
        await StartCooldownAsync(s, skill.Active); // Reserve before publication can yield to another request.
        if (!await PrepareGuardianOwnerAsync(s, skill, action)) return;
        await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = action.Id, IsAutoCast = req.IsAutoCast });
        if (!CurrentCastMap(s, action)) return;
        await SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = action.OwnerId, SkillID = skill.EffectId, PetConfigId = skill.PetId,
            TargetId = action.Request.TargetId, TargetPos = action.Request.TargetPos, Face = req.Face,
            SessionId = action.Id, SkillMs = action.ActionMs, StartTime = action.StartMs,
        }, extra: true); // LC_Scene_Sync._useExtraSkill requires child type 41 + petConfigId.
        ScheduleGuardianPulses(s, skill, action);
        if (CurrentCastMap(s, action)) await SyncGuardianWheelAsync(s);
    }

    private ActiveSkillCast GuardianAction(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req)
    {
        var (targetId, targetPos) = GuardianRequestTarget(s, skill, req);
        return new ActiveSkillCast
        {
            Id = ++_sessionIds, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Row = GuardianPetRules.DamageRow(skill, _character.Stats(s)),
            Request = new CastSkillReq
            {
                SkillId = skill.EffectId, TargetId = targetId, TargetPos = targetPos, Face = req.Face, IsExtraSkill = true
            },
            Origin = s.Position.Clone(), StartMs = Now, ChantMs = 0,
            ActionMs = (int)Math.Max(1, GameData.Int(skill.Effect, "_iActionTime")),
            SourceEligible = () => _pets.GuardianEquipped(s, skill),
            GuardianDamage = [],
            GuardianGate = new(1, 1),
            GuardianOnce = [],
        };
    }

    /// <summary>A damage or debuff guardian's target passed <see cref="ValidateTarget"/>. A support guardian acts around
    /// its owner (its pulses choose recipients), so the request's target is only shown: viewers get the owner, or a
    /// player the owner sees within the skill's range, never an arbitrary client value.</summary>
    private (long TargetId, Vec3 TargetPos) GuardianRequestTarget(GameSession s, GuardianPetRules.Skill skill, CastSkillReq req)
    {
        if (skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff)
            return (req.TargetId, req.TargetPos?.Clone() ?? s.Position.Clone());
        float reach = Range(GuardianPetRules.TargetRow(skill)) + 1.5f;
        long targetId = req.TargetId == s.Role!.Id || _world.Viewers(s).Any(v => v.Role?.Id == req.TargetId
            && !v.Dead && PlayerDistance(s, v) <= reach) ? req.TargetId : s.Role.Id;
        var targetPos = req.TargetPos is { } pos
            && MonsterService.Distance(s.Position.X, s.Position.Z, pos.X, pos.Z) <= reach ? pos.Clone() : s.Position.Clone();
        return (targetId, targetPos);
    }

    /// <summary>The owner-side effects before publication: dispels, Death Witch's own buffs, Shinobi's immunity.
    /// False when the cast stopped being current meanwhile.</summary>
    private async Task<bool> PrepareGuardianOwnerAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action)
    {
        bool deathWitch = GuardianPetRules.Script(skill.Effect) == DeathWitchScript;
        if (GuardianPetRules.DispelsControl(skill)) await _buffs.DispelControlsAsync(s, allDebuffs: deathWitch);
        if (deathWitch)
            foreach (var tuple in Tuples(skill.Active, "_kBuff1"))
                if (CurrentCastMap(s, action) && RollBuff(tuple, out int id, out int ms))
                    await _buffs.AddGuardianAsync(s, id, ms, action.OwnerId);
        foreach (var shinobi in GuardianShinobiDispel(s, skill).ToArray())
        {
            if (!CurrentCastMap(s, action)) return false;
            await _buffs.DispelControlsAsync(s);
            if (CurrentCastMap(s, action) && _pets.GuardianEquipped(s, shinobi))
                await _buffs.AddGuardianAsync(s, GuardianPetRules.ControlImmunityBuff,
                    (int)(GuardianPetRules.Description(shinobi, 0) * 1000), action.OwnerId);
        }
        return CurrentCastMap(s, action);
    }

    private const string DeathWitchScript = "Siwanliandao";

    private void ScheduleGuardianPulses(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action)
    {
        var template = _data.SkillTemplate(GameData.Int(skill.Effect, "_iScriptShow"));
        var timed = GuardianPetRules.Pulses(_data, skill)
            .Select((pulse, index) => (Pulse: pulse, Index: index,
                Delay: ImpactOffset(template, pulse.Point, 0, action.ActionMs) + pulse.DelayMs.GetValueOrDefault()))
            .OrderBy(p => p.Delay).ThenBy(p => p.Index).ToArray();
        if (timed.Length == 0) return;
        int first = timed[0].Index, last = timed[^1].Index;
        // One callback per instant, in configured order: the loop's timer queue does not keep the order of equal
        // deadlines, and the last pulse (a core's share) must see every earlier one.
        foreach (var instant in timed.GroupBy(p => p.Delay))
        {
            var batch = instant.ToArray();
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(instant.Key), async () =>
            {
                foreach (var (pulse, index, _) in batch)
                    await ResolveGuardianAsync(s, skill, action, pulse, index == first, index == last);
            });
        }
    }

    private IEnumerable<GuardianPetRules.Skill> GuardianShinobiDispel(GameSession source, GuardianPetRules.Skill primary) =>
        _pets.Guardians(source).Where(p => !p.IsPrimary && p.Reactive == GuardianPetRules.Reaction.ShinobiDispel
            && GuardianPetRules.ReactionTriggered(p.Reactive, primary.Effect));

    private async Task ResolveGuardianAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action, GuardianPetRules.Pulse pulse, bool first, bool last)
    {
        if (action.GuardianGate is not { } gate) return;
        await gate.WaitAsync();
        try { await ResolveGuardianPulseAsync(s, skill, action, pulse, first, last); }
        finally { gate.Release(); }
    }

    private async Task ResolveGuardianPulseAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action, GuardianPetRules.Pulse pulse, bool first, bool last)
    {
        if (!CurrentCastMap(s, action)) return;
        var pulseRow = GuardianPetRules.PulseRow(action.Row, pulse, skill.Active);
        var request = action.Request.Clone();
        if (GuardianPetRules.Script(skill.Effect) == "PetSkill_2_2_EXiaoNanJue")
            request.TargetId = GuardianOwlTarget(s, skill, action);
        var hit = new ActiveSkillCast
        {
            Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration,
            Attachment = action.Attachment, Request = request, Row = pulseRow,
            Origin = action.Origin, StartMs = action.StartMs, ChantMs = 0, ActionMs = action.ActionMs,
            SourceEligible = () => CurrentCastMap(s, action),
            GuardianDamage = [],
        };
        if (skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Debuff)
            await ApplyEffectsAsync(s, hit, action.Request.TargetPos ?? action.Origin);
        foreach (var (targetId, amount) in hit.GuardianDamage) RecordGuardianDamage(action, targetId, amount);
        if (CurrentCastMap(s, action)) await GuardianHitReactionsAsync(s, skill, action, hit);
        if (CurrentCastMap(s, action) && hit.GuardianDamage.Count > 0 && GuardianPetRules.Script(skill.Effect) == "JingZhunShouShu")
        {
            // Aliot: the heal-looking auxiliary's coefficient disagrees with the description; (inferred) the description's
            // radius, target count and coefficient are used.
            var desc = Numbers(skill.Active, "_kDescData");
            if (desc.Length >= 5) await GuardianAreaHealAsync(s, skill, action, (float)desc[2], (int)desc[3], desc[4]);
        }
        if (CurrentCastMap(s, action) && pulse.HealCoefficient is { } heal && hit.GuardianDamage.Count > 0)
        {
            var stats = _character.Stats(s);
            long amount = (long)Math.Floor(Math.Max(stats[AttributeId.Atk], stats[AttributeId.Matk]) * heal / 100 * hit.GuardianDamage.Count);
            await GuardianHealAsync(s, s, skill.EffectId, action.Id, GuardianPetRules.DirectHealing(skill.Effect, amount),
                () => CurrentCastMap(s, action));
        }
        if (first && CurrentCastMap(s, action)) await GuardianShinobiMoveAsync(s, skill, action);
        if (first && CurrentCastMap(s, action)) await GuardianSupportAsync(s, skill, action);
        if (first && CurrentCastMap(s, action)) await GuardianSpecialSupportAsync(s, skill, action);
        if (first && CurrentCastMap(s, action)) await GuardianReactionsAsync(s, skill, action);
        if (last && CurrentCastMap(s, action)) await GuardianCoreDamageAsync(s, action);
    }

    private GameSession[] GuardianAllies(GameSession s, GuardianPetRules.Skill skill)
    {
        if (GuardianPetRules.Script(skill.Effect) is "moqihuanrao" or "shengguanglingyu" or "BaXiLiSiKe_HuZhu" or "renfamizong") return [s];
        bool orc = GuardianPetRules.Script(skill.Effect) == "PetSkill_2_2_ShouRenZhanShiZhang";
        if (skill.Kind is not (GuardianPetRules.Kind.Heal or GuardianPetRules.Kind.Buff) && !orc) return [s];
        float radius = (float)(Numbers(skill.Effect, "_kRange").FirstOrDefault() / 100);
        int maximum = (int)Math.Max(1, GameData.Int(skill.Effect, "_iTargetMax"));
        if (orc)
        {
            radius = (float)Numbers(skill.Active, "_kDescData").ElementAtOrDefault(3);
            maximum = (int)Numbers(skill.Active, "_kDescData").ElementAtOrDefault(4);
        }
        // (inferred) Ally means non-hostile player; owner first, then distance/ID. Original team scope is unverified.
        return [.. _world.PlayersIn(s.WorldScene).Where(p => p.Role != null && !p.Dead && !p.SceneLoading && !p.SceneRecoveryRequired
                && PlayerHostile?.Invoke(s, p) != true && PlayerHostile?.Invoke(p, s) != true && !CanPlayerHit(s, p) && !CanPlayerHit(p, s)
                && Distance(s.Position, p.Position) <= radius)
            .OrderBy(p => p == s ? 0 : 1).ThenBy(p => Distance(s.Position, p.Position))
            .ThenBy(p => p.Role!.Id).Take(maximum)];
    }

    private async Task GuardianSupportAsync(GameSession s, GuardianPetRules.Skill skill, ActiveSkillCast action)
    {
        var ownerStats = _character.Stats(s);
        if (GuardianPetRules.Script(skill.Effect) == "PetSkill_2_2_KuangBaoYueYeMao") _guardianDamageCharges.Remove(s);
        foreach (var recipient in GuardianAllies(s, skill))
        {
            var current = GuardianRecipientCurrent(s, recipient, action);
            foreach (var tuple in Tuples(skill.Effect, "_kBuff1"))
            {
                if (recipient != s && GuardianPetRules.Script(skill.Effect) == "ShengGuangJiangLin") continue;
                if (!current()) break;
                if (!RollBuff(tuple, out int buffId, out int ms)) continue;
                long shield = GuardianShield(skill, buffId, ownerStats, _character.Stats(recipient));
                if (GuardianPetRules.Script(skill.Effect) == "xianzhenchongfeng")
                    shield *= 1 + Math.Min((int)Numbers(skill.Active, "_kDescData").ElementAtOrDefault(6), action.GuardianDamage?.Count ?? 0);
                await _buffs.AddGuardianAsync(recipient, buffId, ms, action.OwnerId, shield);
            }
            if (!current() || skill.Kind != GuardianPetRules.Kind.Heal) continue;
            long amount = (long)Math.Max(0, Math.Floor(Math.Max(ownerStats[AttributeId.Atk], ownerStats[AttributeId.Matk])
                * Numbers(skill.Effect, "_kDamageParam1").FirstOrDefault() / 10000));
            await GuardianHealAsync(s, recipient, skill.EffectId, action.Id, GuardianPetRules.DirectHealing(skill.Effect, amount), current);
        }
    }

    private long GuardianShield(GuardianPetRules.Skill skill, int buffId, StatSheet owner, StatSheet recipient)
    {
        // Goat BuffConfig._kDamageFormulaExtra/_kDamageParamExtra explicitly uses MaxHP.
        // Bobo's ordinary description uses adaptive ATK; its tower HP formula must not leak into this path.
        // (inferred) Floor and replace-on-refresh use the shared finite support-buff lifecycle.
        if ((skill.Kind == GuardianPetRules.Kind.Shield || GuardianPetRules.Script(skill.Effect) == "xianzhenchongfeng")
            && GuardianPetRules.Script(skill.Effect) != "BoLiPaoPao"
            && _data.Row("BuffConfig", buffId) is { } buff)
            return PetCombatRules.AttributeAmount(buff, owner, recipient);
        return GuardianPetRules.Script(skill.Effect) == "BoLiPaoPao"
            ? (long)Math.Max(0, Math.Floor(Math.Max(owner[AttributeId.Atk], owner[AttributeId.Matk])
                * Numbers(skill.Active, "_kDescData").FirstOrDefault() / 100)) : 0;
    }

    /// <summary>A recipient chosen before the loop's awaits: still the same attached, living role in the cast's scene.</summary>
    private static Func<bool> GuardianRecipientCurrent(GameSession source, GameSession recipient, ActiveSkillCast action)
    {
        var role = recipient.Role;
        var attachment = recipient.Attachment;
        long generation = recipient.SceneGeneration;
        return () => CurrentCastMap(source, action) && role != null && ReferenceEquals(recipient.Role, role)
            && !recipient.Dead && !recipient.SceneLoading && recipient.SceneGeneration == generation
            && recipient.WorldScene == action.WorldScene && ReferenceEquals(recipient.Attachment, attachment)
            && attachment?.Cancelled != true;
    }

    private async Task GuardianHealAsync(GameSession source, GameSession recipient, int skillId, long castId, long amount, Func<bool> current)
    {
        if (!current() || source.Role == null || recipient.Role == null) return;
        foreach (var modifier in ExtraHeal) amount = modifier(source, amount);
        var stats = _character.Stats(recipient);
        amount = ModifiedHealing(_buffs.GuardianAttributes(source), _buffs.GuardianAttributes(recipient), amount);
        long actual = Math.Min(Math.Max(0, amount), Math.Max(0, stats[AttributeId.MaxHp] - stats[AttributeId.Hp]));
        long owner = source.Role!.Id, target = recipient.Role!.Id;
        await _character.SetHpSpAsync(recipient, stats[AttributeId.Hp] + actual, null);
        if (!current()) return;
        await ToPlayerAndViewersAsync(source, SkillHit, new SkillHitInfo
        {
            SrcID = owner, SkillId = skillId, SessionId = castId, RootCtxGuid = castId,
            ShowPoint = HitEffect(_data.Row("SkillConfig", skillId) is { } row ? GameData.Int(row, "_iScriptShow") : 0),
            Dsts = { new DamageInfo { DstID = target, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
        });
        if (!current()) return;
        foreach (var observer in Healed) observer(source, actual);
        foreach (var observer in HealedAsync)
        {
            if (!current()) break;
            await observer(source, actual);
        }
    }
}
