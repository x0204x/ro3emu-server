using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private async Task GuardianFinalReactionAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action)
    {
        double Desc(int index) => GuardianPetRules.Description(pet, index);
        switch (pet.Reactive)
        {
            case GuardianPetRules.Reaction.ScoutDetection:
                int buff = GuardianPetRules.BuffAtStar(_data, GuardianGroup.ScoutDetection, pet.Star);
                if (_data.Row("BuffConfig", buff) is { } detector && Numbers(detector, "_kLogicData").FirstOrDefault() == Desc(0) * 100)
                    await _buffs.AddGuardianAsync(source, buff, (int)(Desc(1) * 1000), action.OwnerId);
                break;
            case GuardianPetRules.Reaction.CatVulnerability:
                int curse = GuardianPetRules.BuffAtStar(_data, GuardianGroup.CatVulnerability, pet.Star);
                if (pet.Passive is not { } passive || _data.Row("BuffConfig", curse) is not { } payload) break;
                var changes = GameData.Pairs(payload, "_kChangeAttri").ToArray();
                if (changes.Length != 1 || changes[0] != (AttributeId.PerResDmg1, -Desc(2) * 100)) break;
                var area = JsonSerializer.SerializeToElement(new
                {
                    _iID = GameData.Int(passive, "_iID"), _iRangeType = SkillRelease.Area, _iRelease = SkillRelease.FixCircle,
                    _iTargetMax = (int)Desc(1), _kRange = new[] { Desc(0) * 100 },
                    _kBuff2 = new[] { new[] { curse, 10000, (int)(Desc(3) * 1000) } },
                });
                await ApplyEffectsAsync(source, GuardianProcAction(source, pet, action, area), source.Position);
                break;
            case GuardianPetRules.Reaction.OwlLightning:
                if (GuardianSkillAtStar(GuardianGroup.OwlLightning, pet.Star) is not { } lightning) break;
                // The auxiliary says 42% at star 1, while this reactive's description explicitly says 40%.
                // (inferred) Prefer the description for this named reaction, retaining auxiliary element/type.
                var row = GuardianPetRules.PulseRow(lightning, new(0, Desc(4) * 100));
                for (int i = 1; i <= (int)Math.Clamp(Desc(3), 0, GuardianPetRules.MaxRepeats); i++)
                    ScheduleRuntime(action.WorldScene, TimeSpan.FromSeconds(i * Desc(0)), async () =>
                    {
                        if (!CurrentCastMap(source, action) || !_pets.GuardianEquipped(source, pet)) return;
                        long target = GuardianRandomEnemy(source, pet, action, source.Position, Desc(2));
                        if (target != 0) await ApplyEffectsAsync(source, GuardianProcAction(source, pet, action, row, target), source.Position);
                    });
                break;
        }
    }

    private async Task GuardianKillCurseAsync(GameSession source, GuardianWindow window, Vec3 position)
    {
        if (window.Effect is not { } effect || window.Remaining <= 0) return;
        window.Remaining--; // reserve before a kill in this auxiliary can publish another reaction
        var node = GuardianPetRules.CopyObject(effect);
        node["_iRelease"] = SkillRelease.DragCircle; // description: around the defeated enemy, not the owner
        await ApplyEffectsAsync(source, GuardianProcAction(source, window.Pet, window.Cast,
            JsonSerializer.SerializeToElement(node)), position);
    }

    private Task ConsumeGuardianDamageChargeAsync(GameSession source)
    {
        // Cat's next instances of OWNER damage. Pet primaries and auxiliary chains never consume this charge.
        var buff = source.Buffs.FirstOrDefault(b => b.IsGuardianOwned && b.ExpireMs > Now && b.ConfigId / 100 == GuardianGroup.WildcatCharge);
        if (buff == null) return Task.CompletedTask;
        var pet = _pets.Guardians(source).FirstOrDefault(p => p.IsPrimary && GuardianPetRules.Script(p.Effect) == "PetSkill_2_2_KuangBaoYueYeMao");
        int maximum = pet == null ? 1 : (int)Numbers(pet.Active, "_kDescData").ElementAtOrDefault(3);
        var charges = _guardianDamageCharges.GetOrCreateValue(source);
        int used = charges.GetValueOrDefault(buff.Guid) + 1;
        charges.Clear(); charges[buff.Guid] = used;
        return used >= maximum ? _buffs.ConsumeGuardianGroupAsync(source, GuardianGroup.WildcatCharge) : Task.CompletedTask;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameSession, Dictionary<long, int>> _guardianDamageCharges = [];
}
