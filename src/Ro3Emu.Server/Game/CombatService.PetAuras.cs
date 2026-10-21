using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // Baphomet Jr.'s description requires the target to be slowed BEFORE this hit, not by its first Buff2 tuple.
    private const string KuangFengXiJuan = "KuangFengXiJuan";

    private static bool GuardianAllowsHostileBuff(JsonElement row, JsonElement tuple, bool previouslySlowed) =>
        GuardianPetRules.Script(row) != KuangFengXiJuan || tuple.GetArrayLength() == 0
            || (int)(GameData.Number(tuple[0]) ?? 0) / 100 != BuffService.RootGroup || previouslySlowed;

    /// <summary>Named primary scripts with periodic effects described by their wrapper. Coefficients, range,
    /// duration and interval are read from that star's description/linked auxiliary, never tower support rows.
    /// (inferred) First periodic tick follows one interval; Angeling's unspecified heal interval is one second.</summary>
    private async Task GuardianSpecialSupportAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action)
    {
        GuardianPrimaryWindows(source, pet, action);
        var desc = Numbers(pet.Active, "_kDescData");
        bool Current() => CurrentCastMap(source, action);
        switch (GuardianPetRules.Script(pet.Effect))
        {
            case "GuoShuGui_HuZhu" when desc.Length >= 7:
                long extra = (long)(Math.Floor((action.GuardianDamage?.Count ?? 0) / Math.Max(1, desc[5])) * desc[6]);
                await GuardianHealAsync(source, source, pet.EffectId, action.Id, extra, Current);
                for (int second = 1; second <= desc[1]; second++)
                    ScheduleRuntime(action.WorldScene, TimeSpan.FromSeconds(second), () => Current()
                        ? GuardianHealAsync(source, source, pet.EffectId, action.Id, (long)desc[0], Current) : Task.CompletedTask);
                break;
            // Angeling: (inferred) one heal a second over the description's radius, not the linked halo's.
            case "ShengGuangJiangLin" when desc.Length >= 7:
                for (int second = 1; second <= desc[6]; second++)
                    ScheduleRuntime(action.WorldScene, TimeSpan.FromSeconds(second), () =>
                        GuardianAreaHealAsync(source, pet, action, (float)desc[3], (int)GameData.Int(pet.Effect, "_iTargetMax"), desc[5]));
                break;
            // Incubus: the aura hits with the linked emodiyu auxiliary, so its range is that row's _kRange, which differs from
            // the description's.
            case "moqihuanrao" when desc.Length >= 7:
                if (GuardianLinkedAuxiliary(pet.Active, pet.Star, "emodiyu") is { } damage)
                    ScheduleGuardianAura(source, pet, action, damage, desc[6], desc[1]);
                break;
            case "shengguanglingyu" when desc.Length >= 6:
                if (GuardianLinkedAuxiliary(pet.Active, pet.Star, "shengguangjianglin2") is { } shield)
                    for (double second = desc[0]; desc[0] > 0 && second <= desc[5]; second += desc[0])
                        ScheduleRuntime(action.WorldScene, TimeSpan.FromSeconds(second), () =>
                            GuardianAreaShieldAsync(source, pet, action, shield, (float)desc[1], (int)desc[2] + 1));
                break;
        }
    }

    private JsonElement? GuardianLinkedAuxiliary(JsonElement owner, int star, string script)
    {
        var candidates = Numbers(owner, "_kAutoLearnSkill").Select(id => _data.Row("SkillConfig", (long)id))
            .Where(r => r is { } row && GuardianPetRules.Script(row) == script && GameData.Int(row, "_iLevel") == star).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private void ScheduleGuardianAura(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast parent,
        JsonElement effect, double interval, double duration)
    {
        if (interval <= 0 || duration <= 0 || duration / interval > GuardianPetRules.MaxAuraPulses) return;
        for (double second = interval; second <= duration + .000001; second += interval)
            ScheduleRuntime(parent.WorldScene, TimeSpan.FromSeconds(second), async () =>
            {
                if (!CurrentCastMap(source, parent) || !_pets.GuardianEquipped(source, pet)) return;
                var proc = GuardianProcAction(source, pet, parent, effect);
                await ApplyEffectsAsync(source, proc, source.Position);
            });
    }

    private async Task GuardianAreaHealAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action,
        float radius, int maximum, double coefficient)
    {
        bool Current() => CurrentCastMap(source, action) && _pets.GuardianEquipped(source, pet);
        if (!Current()) return;
        var stats = _character.Stats(source);
        long amount = (long)Math.Floor(Math.Max(stats[AttributeId.Atk], stats[AttributeId.Matk]) * coefficient / 100);
        amount = GuardianPetRules.DirectHealing(pet.Effect, amount);
        foreach (var (ally, allyCurrent) in GuardianReactionAllies(source, radius, maximum)
            .Select(ally => (ally, GuardianRecipientCurrent(source, ally, action))).ToArray())
            if (Current() && allyCurrent())
                await GuardianHealAsync(source, ally, pet.EffectId, action.Id, amount, () => Current() && allyCurrent());
    }

    private async Task GuardianAreaShieldAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action,
        JsonElement effect, float radius, int maximum)
    {
        foreach (var ally in GuardianReactionAllies(source, radius, maximum))
            foreach (var tuple in Tuples(effect, "_kBuff1"))
            {
                if (!CurrentCastMap(source, action) || !_pets.GuardianEquipped(source, pet)) return;
                if (ally.Dead || ally.SceneLoading || ally.WorldScene != action.WorldScene
                    || !RollBuff(tuple, out int id, out int duration) || _data.Row("BuffConfig", id) is not { } buff) continue;
                long amount = PetCombatRules.AttributeAmount(buff, _character.Stats(source), _character.Stats(ally));
                await _buffs.AddGuardianAsync(ally, id, duration, action.OwnerId, amount);
            }
    }
}
