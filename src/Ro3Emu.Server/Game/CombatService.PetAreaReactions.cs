using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private async Task GuardianAreaReactionAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action, int buff)
    {
        double Desc(int index) => GuardianPetRules.Description(pet, index);
        if (pet.Reactive == GuardianPetRules.Reaction.IsisReduction)
        {
            foreach (var (ally, allyCurrent) in GuardianReactionAllies(source, (float)Desc(1), (int)Desc(2) + 1)
                .Select(ally => (ally, GuardianRecipientCurrent(source, ally, action))).ToArray())
                if (allyCurrent() && _pets.GuardianEquipped(source, pet))
                    await _buffs.AddGuardianAsync(ally, buff, (int)(Desc(0) * 1000), action.OwnerId);
            return;
        }
        if (pet.Passive is not { } passive) return;
        bool daedre = pet.Reactive == GuardianPetRules.Reaction.DaedreDebuff;
        if (daedre && !ReserveGuardianReaction(source, (int)GameData.Int(passive, "_iID"), (int)(Desc(6) * 1000))) return;
        var row = JsonSerializer.SerializeToElement(new
        {
            _iID = GameData.Int(passive, "_iID"), _iDmgType = 1, _iRangeType = SkillRelease.Area, _iRelease = SkillRelease.FixCircle,
            _iTargetMax = (int)Desc(daedre ? 2 : 1), _kRange = new[] { Desc(daedre ? 1 : 0) * 100 },
            _kBuff2 = new[] { new[] { buff, 10000, (int)(Desc(daedre ? 3 : 4) * 1000) } },
        });
        await ApplyEffectsAsync(source, GuardianProcAction(source, pet, action, row), source.Position);
    }

    private async Task GuardianAdditionalReactionAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action)
    {
        if (pet.Passive is not { } passive) return;
        if (pet.Reactive is GuardianPetRules.Reaction.ScoutDetection or GuardianPetRules.Reaction.OwlLightning
            or GuardianPetRules.Reaction.CatVulnerability)
        { await GuardianFinalReactionAsync(source, pet, action); return; }
        if (pet.Reactive == GuardianPetRules.Reaction.SoheeReduction)
        {
            if (_data.Row("BuffConfig", GameData.Int(passive, "_iID")) is not { } controller) return;
            int buff = (int)Numbers(controller, "_kLogicData").FirstOrDefault();
            if (_data.Row("BuffConfig", buff) is { } payload
                && Numbers(payload, "_kLogicData").FirstOrDefault() == GuardianPetRules.Description(pet, 1) * 100)
                await _buffs.AddGuardianAsync(source, buff, (int)(GuardianPetRules.Description(pet, 0) * 1000), action.OwnerId);
            return;
        }
        string script = pet.Reactive switch
        {
            GuardianPetRules.Reaction.SuccubusSilence => "emochenmo",
            GuardianPetRules.Reaction.ChimeraDebuff => "kaimeilabeidong",
            GuardianPetRules.Reaction.WildcatShield => "yemaobeidong",
            GuardianPetRules.Reaction.IncubusAura => "emodiyu",
            _ => "",
        };
        if (GuardianLinkedAuxiliary(passive, pet.Star, script) is not { } effect) return;
        if (pet.Reactive == GuardianPetRules.Reaction.WildcatShield)
            await GuardianWildcatShieldAsync(source, pet, action, effect);
        else if (pet.Reactive == GuardianPetRules.Reaction.IncubusAura)
            await GuardianIncubusReactiveAsync(source, pet, action, effect);
        else
        {
            // Silence/debuff descriptions contain no damage: discard the auxiliary's unused placeholder values.
            var node = GuardianPetRules.CopyObject(effect);
            node["_kDamageParam1"] = new JsonArray(); node["_kDamageParam2"] = new JsonArray();
            await ApplyEffectsAsync(source, GuardianProcAction(source, pet, action, JsonSerializer.SerializeToElement(node)), source.Position);
        }
    }

    private async Task GuardianWildcatShieldAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action, JsonElement effect)
    {
        var proc = GuardianProcAction(source, pet, action, effect);
        // Description explicitly says members of the enemy team, so monsters do not grant this reactive shield.
        // (inferred) The configured shield amount counts once per enemy player in the area, up to the description's cap.
        int count = Math.Min((int)GuardianPetRules.Description(pet, 2), EventAreaTargets(source, proc.Row).Length);
        if (count <= 0) return;
        foreach (var tuple in Tuples(effect, "_kBuff1"))
            if (CurrentCastMap(source, proc) && RollBuff(tuple, out int id, out int duration) && _data.Row("BuffConfig", id) is { } buff)
            {
                var stats = _character.Stats(source);
                long amount = PetCombatRules.AttributeAmount(buff, stats, stats) * count;
                await _buffs.AddGuardianAsync(source, id, duration, action.OwnerId, amount);
            }
    }

    private async Task GuardianIncubusReactiveAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action, JsonElement effect)
    {
        if (pet.Passive is not { } passive || _data.Row("BuffConfig", GameData.Int(passive, "_iID")) is not { } controller) return;
        int buffId = (int)Numbers(controller, "_kLogicData").FirstOrDefault();
        if (_data.Row("BuffConfig", buffId) is not { } buff) return;
        var changes = GameData.Pairs(buff, "_kChangeAttri").ToArray();
        if (changes.Length != 2 || changes.Any(c => c.Id is not (AttributeId.PerResAtkDmg1 or AttributeId.PerResMatkDmg1)
            || c.Value != GuardianPetRules.Description(pet, 1) * 100)) return;
        await _buffs.AddGuardianAsync(source, buffId, (int)(GuardianPetRules.Description(pet, 0) * 1000), action.OwnerId);
        if (CurrentCastMap(source, action)) ScheduleGuardianAura(source, pet, action, effect,
            GuardianPetRules.Description(pet, 2), GuardianPetRules.Description(pet, 0));
    }

    private long GuardianNormalAttackResistance(GameSession target, JsonElement skill, long damage)
    {
        if (GameData.Int(skill, "_iIsNormalAtk") != 1 || damage <= 0) return damage;
        double reduction = target.Buffs.Where(b => b.IsGuardianOwned && b.ExpireMs > Now)
            .Select(b => _data.Row("BuffConfig", b.ConfigId)).OfType<JsonElement>()
            .Where(row => GameData.Int(row, "_iBuffID") == GuardianGroup.SoheeReduction)
            .Sum(row => Numbers(row, "_kLogicData").FirstOrDefault());
        return (long)Math.Clamp(Math.Floor(damage * Math.Max(0, 1 - reduction / 10000)), 0, long.MaxValue);
    }
}
