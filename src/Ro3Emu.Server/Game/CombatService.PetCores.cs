using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static void RecordGuardianDamage(ActiveSkillCast action, long targetId, long damage)
    {
        if (action.GuardianDamage is not { } totals || damage <= 0) return;
        long prior = totals.GetValueOrDefault(targetId);
        totals[targetId] = damage > long.MaxValue - prior ? long.MaxValue : prior + damage;
    }

    /// <summary>Zherlthsh core _iDescription literal arguments give count and share of total primary damage.
    /// (inferred) One extra hit per surviving damaged target after the final primary pulse; no second ATK/DEF roll,
    /// no recursive guardian reaction. A player's shield/HP transition still protects the separate extra hit.</summary>
    private async Task GuardianCoreDamageAsync(GameSession s, ActiveSkillCast action)
    {
        if (action.GuardianDamage == null) return;
        foreach (var passive in _pets.Guardians(s).Where(g => !g.IsPrimary && g.Core != null
            && g.Reactive == GuardianPetRules.Reaction.DamageBuff).ToArray())
        {
            if (passive.Core is not { } core) continue;
            var literals = GuardianPetRules.DescriptionLiterals(core);
            if (literals.Length != 2 || literals[0] is <= 0 or > GuardianPetRules.MaxCoreHits || literals[1] <= 0) continue;
            foreach (var (targetId, total) in action.GuardianDamage.ToArray())
                for (int i = 0; i < (int)literals[0]; i++)
                {
                    if (!CurrentCastMap(s, action) || !_pets.GuardianEquipped(s, passive)) return;
                    long amount = (long)Math.Clamp(Math.Floor(total * literals[1] / 100), 0, long.MaxValue);
                    var proc = GuardianFixedAction(s, passive, core, action, targetId, amount);
                    await ApplyEffectsAsync(s, proc, action.Origin);
                }
        }
    }

    private ActiveSkillCast GuardianFixedAction(GameSession s, GuardianPetRules.Skill passive, JsonElement core, ActiveSkillCast parent,
        long targetId, long damage)
    {
        var row = new JsonObject
        {
            ["_iID"] = GameData.Int(core, "_iID"), ["_iSystemType"] = GuardianPetRules.PetSkillType, ["_iRangeType"] = SkillRelease.SingleTarget,
            ["_iTargetMax"] = 1, ["_iRelease"] = SkillRelease.Lock, ["_iDistanceMax"] = 100000,
            ["_kDamageParam1"] = new JsonArray(0), ["_kDamageParam2"] = new JsonArray(1),
            ["_ro3GuardianFixedHit"] = damage,
        };
        return new ActiveSkillCast
        {
            Id = parent.Id, OwnerId = parent.OwnerId, WorldScene = parent.WorldScene, SceneGeneration = parent.SceneGeneration,
            Attachment = parent.Attachment, Row = JsonSerializer.SerializeToElement(row), Origin = parent.Origin,
            Request = new CastSkillReq { SkillId = (int)GameData.Int(core, "_iID"), TargetId = targetId, Face = parent.Request.Face },
            StartMs = Now, ChantMs = 0, ActionMs = 1,
            SourceEligible = () => CurrentCastMap(s, parent) && _pets.GuardianEquipped(s, passive),
        };
    }

    private async Task GuardianHopeCoreAsync(GameSession s, GuardianPetRules.Skill passive, ActiveSkillCast action,
        int defenceBuff, int duration, Func<bool> current)
    {
        if (passive.Core is not { } core) return;
        foreach (var ally in GuardianReactionAllies(s, (float)GuardianPetRules.CoreDescription(passive, 0),
            (int)GuardianPetRules.CoreDescription(passive, 1)))
            if (current() && !ally.Dead && !ally.SceneLoading && ally.WorldScene == action.WorldScene)
                await _buffs.AddGuardianAsync(ally, defenceBuff, duration, action.OwnerId);
        if (!current()) return;
        // BuffConfig candidate chain of the normal reactive lists the HP-lock controller and DEF payload.
        // (inferred) Validate the threshold before binding its otherwise server-only controller to the HP transition.
        if (passive.Passive is not { } row || _data.Row("BuffConfig", GameData.Int(row, "_iID")) is not { } controller) return;
        int lockId = (int)GuardianPetRules.Numbers(controller, "_kLogicData").FirstOrDefault();
        if (_data.Row("BuffConfig", lockId) is not { } hpLock
            || GuardianPetRules.Numbers(hpLock, "_kDescData").FirstOrDefault() != GuardianPetRules.CoreDescription(passive, 2)) return;
        int skillId = (int)GameData.Int(core, "_iID");
        if (!ReserveGuardianReaction(s, skillId, (int)(GuardianPetRules.CoreDescription(passive, 3) * 1000))) return;
        await _buffs.AddGuardianAsync(s, lockId, duration, action.OwnerId,
            hpFloorBasisPoints: (int)(GuardianPetRules.CoreDescription(passive, 2) * 100));
    }

    /// <summary>Hope's core says HP cannot fall below the configured share while Divine Hope is active.
    /// (inferred) Absorb shields first; HP lock cannot heal someone who was already below the threshold.</summary>
    private long GuardianHpFloor(GameSession target, long currentHp)
    {
        int percent = target.Buffs.Where(b => b.IsGuardianOwned && b.ExpireMs > Now)
            .Select(b => b.HpFloorBasisPoints).DefaultIfEmpty(0).Max();
        return Math.Min(currentHp, (long)Math.Ceiling(_character.Stats(target)[AttributeId.MaxHp] * percent / 10000.0));
    }
}
