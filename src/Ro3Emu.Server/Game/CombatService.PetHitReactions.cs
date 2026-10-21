using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private async Task GuardianHitReactionsAsync(GameSession source, GuardianPetRules.Skill primary, ActiveSkillCast parent, ActiveSkillCast hit)
    {
        if (hit.GuardianDamage is not { Count: > 0 }) return;
        if (GuardianPetRules.Script(primary.Effect) == "xingxingshike" && parent.GuardianOnce?.Add(-primary.PetId) == true)
            foreach (long target in hit.GuardianDamage.Keys.ToArray())
                await GuardianTargetBuffAsync(source, primary, parent, target, GuardianPetRules.KnockUpBuff, 100,
                    (int)(Numbers(primary.Active, "_kDescData").ElementAtOrDefault(6) * 1000));
        foreach (var pet in _pets.Guardians(source).Where(p => !p.IsPrimary).ToArray())
        {
            if (!CurrentCastMap(source, parent) || !_pets.GuardianEquipped(source, pet)) return;
            bool offense = GuardianPetRules.Category(primary.Effect, GuardianPetRules.OffensiveCategory);
            bool control = GuardianPetRules.Category(primary.Effect, GuardianPetRules.ControlCategory);
            bool debuff = GuardianPetRules.Category(primary.Effect, GuardianPetRules.DebuffCategory);
            bool matches = pet.Reactive switch
            {
                GuardianPetRules.Reaction.RainbowExtraHit or GuardianPetRules.Reaction.NightmareControl => offense || control,
                GuardianPetRules.Reaction.SarahExtraHit => offense || debuff,
                GuardianPetRules.Reaction.MummyCurse or GuardianPetRules.Reaction.SilverVulnerability => offense,
                GuardianPetRules.Reaction.MedusaPetrify => control,
                _ => false,
            };
            if (!matches) continue;
            if (pet.Reactive == GuardianPetRules.Reaction.RainbowExtraHit
                && !ReserveGuardianReaction(source, pet.PetId, (int)(GuardianPetRules.Description(pet, 2) * 1000))) continue;
            if (pet.Reactive == GuardianPetRules.Reaction.MummyCurse && parent.GuardianOnce?.Add(pet.PetId) != true) continue;
            foreach (long targetId in hit.GuardianDamage.Keys.ToArray())
            {
                if (!CurrentCastMap(source, parent) || !_pets.GuardianEquipped(source, pet)) break;
                await GuardianExtraReactionOnTargetAsync(source, pet, parent, targetId);
                if (pet.Reactive == GuardianPetRules.Reaction.MummyCurse) break;
            }
        }
    }

    private async Task GuardianExtraReactionOnTargetAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast parent, long targetId)
    {
        double Desc(int index) => GuardianPetRules.Description(pet, index);
        switch (pet.Reactive)
        {
            case GuardianPetRules.Reaction.RainbowExtraHit:
                if (GuardianSkillAtStar(GuardianGroup.RainbowFire, pet.Star) is { } fire)
                    for (int i = 0; i < (int)Math.Clamp(Desc(0), 0, GuardianPetRules.MaxRepeats) && CurrentCastMap(source, parent); i++)
                        await ApplyEffectsAsync(source, GuardianProcAction(source, pet, parent, fire, targetId), source.Position);
                break;
            case GuardianPetRules.Reaction.SarahExtraHit:
                if (GuardianSkillAtStar(GuardianGroup.SarahFist, pet.Star) is { } fist && pet.Passive is { } passive)
                    await GuardianAuxiliaryBurstAsync(source, pet, parent, fist, targetId,
                        (int)GuardianPetRules.DescriptionLiterals(passive).FirstOrDefault(1));
                break;
            case GuardianPetRules.Reaction.MummyCurse:
                if (GuardianSkillAtStar(GuardianGroup.MummyHitCurse, pet.Star) is { } curse)
                {
                    var center = FindMonster(source.WorldScene, targetId)?.Pos ?? EventTarget(source, targetId)?.Position;
                    if (center != null) await ApplyEffectsAsync(source, GuardianProcAction(source, pet, parent, curse), center);
                }
                break;
            case GuardianPetRules.Reaction.NightmareControl:
                await GuardianTargetBuffAsync(source, pet, parent, targetId, GuardianPetRules.RootBuff, 100, (int)(Desc(0) * 1000));
                await GuardianTargetBuffAsync(source, pet, parent, targetId, GuardianPetRules.BlindBuff, 100, (int)(Desc(0) * 1000));
                break;
            case GuardianPetRules.Reaction.MedusaPetrify:
                await GuardianTargetBuffAsync(source, pet, parent, targetId, GuardianPetRules.PetrifyBuff, Desc(0), (int)(Desc(1) * 1000));
                RegisterGuardianWindow(source, new GuardianWindow
                {
                    Pet = pet, Cast = parent, Kind = GuardianWindowKind.ControlledBonus,
                    Expires = Now + (long)(Desc(2) * 1000), Coefficient = Desc(3),
                });
                break;
            case GuardianPetRules.Reaction.SilverVulnerability:
                await GuardianTargetBuffAsync(source, pet, parent, targetId, GuardianPetRules.ReactiveBuff(_data, pet), 100, (int)(Desc(0) * 1000));
                break;
        }
    }

    private async Task GuardianAuxiliaryBurstAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast parent,
        JsonElement effect, long targetId, int count)
    {
        count = Math.Clamp(count, 1, GuardianPetRules.MaxRepeats);
        var row = GuardianPetRules.PulseRow(effect, new(0, Numbers(effect, "_kDamageParam1").FirstOrDefault() / count));
        for (int i = 0; i < count && CurrentCastMap(source, parent) && _pets.GuardianEquipped(source, pet); i++)
            await ApplyEffectsAsync(source, GuardianProcAction(source, pet, parent, row, targetId), source.Position);
    }

    private async Task GuardianTargetBuffAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast parent,
        long targetId, int buffId, double chance, int duration)
    {
        if (!CurrentCastMap(source, parent) || !_pets.GuardianEquipped(source, pet) || buffId <= 0) return;
        var monster = FindMonster(source.WorldScene, targetId);
        var player = EventTarget(source, targetId);
        var tuple = JsonSerializer.SerializeToElement(new[] { (double)buffId, chance * 100, duration });
        if (monster is { Dead: true } || monster?.Hp <= 0 || monster == null && player == null
            || !RollHostileBuff(tuple, source, null, player, monster, out int id, out int ms)) return;
        if (monster != null) await _buffs.AddGuardianAsync(monster, id, ms, parent.OwnerId);
        else if (player != null) await _buffs.AddGuardianAsync(player, id, ms, parent.OwnerId);
    }
}
