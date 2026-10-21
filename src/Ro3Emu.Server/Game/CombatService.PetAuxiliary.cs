using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>Named auxiliary script and matching star level. Some passive rows omit their auto-learn link;
    /// that family-specific join is inferred and validated against the advertised coefficient by its caller.</summary>
    private JsonElement? GuardianAuxiliary(GuardianPetRules.Skill pet, string script)
    {
        var candidates = GuardianTables.Of(_data).Scripted(script, pet.Star);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private ActiveSkillCast GuardianProcAction(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast parent,
        JsonElement effect, long target = 0, double attributeMultiplier = 1)
    {
        var row = GuardianPetRules.CopyObject(GuardianPetRules.DamageRow(pet with { Effect = effect, Kind = GuardianPetRules.Kind.Damage },
            _character.Stats(source)));
        row["_ro3GuardianProc"] = true;
        row["_iSystemType"] = GuardianPetRules.PetSkillType;
        row["_ro3GuardianBuffMultiplier"] = attributeMultiplier;
        if (target != 0) { row["_iRangeType"] = SkillRelease.SingleTarget; row["_iTargetMax"] = 1; }
        return new ActiveSkillCast
        {
            Id = parent.Id, OwnerId = parent.OwnerId, WorldScene = parent.WorldScene, SceneGeneration = parent.SceneGeneration,
            Attachment = parent.Attachment, Row = JsonSerializer.SerializeToElement(row), Origin = source.Position.Clone(),
            Request = new CastSkillReq
            {
                SkillId = (int)GameData.Int(effect, "_iID"), TargetId = target,
                TargetPos = source.Position.Clone(), Face = parent.Request.Face
            },
            StartMs = Now, ChantMs = 0, ActionMs = Math.Max(1, (int)GameData.Int(effect, "_iActionTime")),
            SourceEligible = () => CurrentCastMap(source, parent) && _pets.GuardianEquipped(source, pet),
        };
    }

    private async Task GuardianWanderReactionAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast parent)
    {
        if (GuardianAuxiliary(pet, "Pet_Wanderman_23176") is not { } effect
            || Numbers(effect, "_kDamageParam1").FirstOrDefault() != GuardianPetRules.Description(pet, 2) * 100) return;
        var area = GuardianProcAction(source, pet, parent, effect);
        var enemies = SelectEnemies(source, area, source.Position).Select(m =>
            (Id: m.Uid, Hp: m.Hp, MaxHp: m.Template.MaxHp, Distance: Distance(source.Position, m.Pos)))
            .Concat(EventAreaTargets(source, area.Row).Select(p => (Id: p.Role!.Id, Hp: _character.Stats(p)[AttributeId.Hp],
                MaxHp: _character.Stats(p)[AttributeId.MaxHp], Distance: Distance(source.Position, p.Position))))
            .OrderBy(e => e.Distance).ThenBy(e => e.Id).Take((int)GameData.Int(effect, "_iTargetMax")).ToArray();
        foreach (var enemy in enemies)
        {
            if (!CurrentCastMap(source, area)) break;
            var proc = GuardianProcAction(source, pet, parent, effect, enemy.Id,
                pet.Core != null ? GuardianPetRules.CoreDescription(pet, 0) : 1);
            await ApplyEffectsAsync(source, proc, source.Position);
            if (pet.Core != null && CurrentCastMap(source, proc))
                await GuardianWanderCoreAsync(source, pet, proc, enemy.Id, enemy.Hp, enemy.MaxHp);
        }
    }

    private async Task GuardianWanderCoreAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action,
        long targetId, long beforeHp, long maxHp)
    {
        if (pet.Core is not { } core) return;
        var literals = GuardianPetRules.DescriptionLiterals(core);
        if (literals.Length < 3) return;
        var monster = FindMonster(source.WorldScene, targetId);
        var player = EventTarget(source, targetId);
        if (monster is { Dead: true } || monster?.Hp <= 0 || monster == null && player == null) return;
        bool highHp = beforeHp > maxHp * literals[0] / 100;
        if (highHp)
        {
            var tuple = JsonSerializer.SerializeToElement(new[] { GuardianPetRules.StunBuff, 10000, (int)(literals[1] * 1000) });
            if (RollHostileBuff(tuple, source, null, player, monster, out int stun, out int ms))
            {
                if (monster != null) await _buffs.AddGuardianAsync(monster, stun, ms, action.OwnerId);
                else if (player != null) await _buffs.AddGuardianAsync(player, stun, ms, action.OwnerId);
            }
        }
        if (!CurrentCastMap(source, action)) return;
        bool stunned = monster != null ? _buffs.UnitHasGroup(monster, GuardianPetRules.StunBuff / 100)
            : player?.Buffs.Any(b => b.ExpireMs > Now && b.ConfigId / 100 == GuardianPetRules.StunBuff / 100) == true;
        if (highHp && stunned) return;
        // Auxiliary SkillConfig -> BuffConfig controller -> PATK/MATK payload; validate the core's percentage.
        if (GuardianAuxiliary(pet, "Pet_Wanderman_23176") is not { } effect) return;
        int controllerId = (int)Numbers(effect, "_kLogicData").FirstOrDefault();
        if (_data.Row("BuffConfig", controllerId) is not { } controller) return;
        int attackId = (int)Numbers(controller, "_kLogicData").ElementAtOrDefault(1);
        if (_data.Row("BuffConfig", attackId) is not { } attack
            || GameData.Pairs(attack, "_kChangeAttri").Any(c => c.Id is not (AttributeId.BuffPerAtk or AttributeId.BuffPerMatk)
                || Math.Abs(c.Value + GuardianPetRules.CoreDescription(pet, 1) * 100) > 0.000001)) return;
        int duration = (int)(GuardianPetRules.CoreDescription(pet, 2) * 1000);
        if (monster != null) await _buffs.AddGuardianAsync(monster, attackId, duration, action.OwnerId);
        else if (player != null) await _buffs.AddGuardianAsync(player, attackId, duration, action.OwnerId);
    }
}
