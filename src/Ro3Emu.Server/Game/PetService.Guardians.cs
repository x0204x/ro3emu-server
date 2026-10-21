using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class PetService
{
    private const int PetFunction = 33; // FunctionConfig: pet system.
    private const int LineUpSlots = 1; // SlotOpen source: 1 the lineup (PetSetLimitConfig._iCondition1), 2 support
    /// <summary>Observers of a committed pet state (including plan and star changes).</summary>
    internal List<Func<GameSession, Task>> Changed { get; } = [];

    internal IEnumerable<GuardianPetRules.Skill> Guardians(GameSession s)
    {
        if (s.Pets is not { } state || s.Progress == null || s.TowerAttempt != null || !_functions.IsOpen(s, PetFunction)
            || !state.SetupPlans.TryGetValue(state.CurrentPlan, out var plan)) yield break;
        var seen = new HashSet<int>();
        bool coreSelected = false;
        foreach (var (slot, id) in plan.SetUpPets.OrderBy(kv => kv.Key))
            if (SlotOpen(s, LineUpSlots, slot) && seen.Add(id) && state.ListPet.TryGetValue(id, out var pet) && StarRow(pet) is { } star
                && _data.Row("SkillConfig", GameData.Int(star, "_iProtectSkill")) is { } active
                && GuardianPetRules.Child(_data, active) is { } effect)
            {
                // UI_Sys_Pet_LineUpWidgetCtrl._updatePetSetUpList: first Pet King in utility order alone gets Core.
                var core = slot > GuardianPetRules.PrimarySlot && !coreSelected
                    ? _data.Row("SkillConfig", GameData.Int(star, "_iCorePassiveSkill")) : null;
                if (core != null) coreSelected = true;
                yield return new(slot, id, pet.Star, pet.StarProgress, state.CurrentPlan, active, effect,
                    _data.Row("SkillConfig", GameData.Int(star, "_iPassiveMain")),
                    GuardianPetRules.Classify(effect) ?? GuardianPetRules.Kind.Unsupported)
                { Core = core };
            }
    }

    internal bool GuardianEquipped(GameSession s, GuardianPetRules.Skill skill) =>
        s.Pets is { } state && s.Progress != null && s.TowerAttempt == null && _functions.IsOpen(s, PetFunction)
        && state.CurrentPlan == skill.Plan && SlotOpen(s, LineUpSlots, skill.Slot)
        && state.SetupPlans.TryGetValue(state.CurrentPlan, out var plan) && plan.SetUpPets.GetValueOrDefault(skill.Slot) == skill.PetId
        && state.ListPet.TryGetValue(skill.PetId, out var pet) && pet.Star == skill.Star && pet.StarProgress == skill.Stage
        && Guardians(s).Any(g => g.PetId == skill.PetId && g.Slot == skill.Slot
            && (g.Core is { } current ? GameData.Int(current, "_iID") : 0) == (skill.Core is { } prior ? GameData.Int(prior, "_iID") : 0));

    /// <summary>LC_System_Skill reads whellSkills[SkillWheelPet], not the separate petSkill field.
    /// UI_Sys_Pet_LineUp_List_CellWidgetCtrl._initSkillWidget selects Protect only for index 1; utility slots are Reactive.
    /// Client normalpet auto-battle sends 31102; there is no independent server polling caster.</summary>
    internal SkillWheelSlot? GuardianWheel(GameSession session)
    {
        var selected = Guardians(session).FirstOrDefault(g => g.IsPrimary && g.Kind != GuardianPetRules.Kind.Unsupported);
        return selected == null ? null : new SkillWheelSlot { SkillConfigId = selected.Id, Enable = true };
    }

    private async Task PublishGuardianChangeAsync(GameSession session)
    {
        foreach (var handler in Changed) await handler(session);
    }
}
