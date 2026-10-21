using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static readonly int[] CoreZeroCoefficient = [0];
    private static readonly int[] CoreLethalDamage = [1000000];

    [ClientDataFact]
    public async Task GuardianOnlyFirstUtilityKingOwnsCoreAndChangingOrderInvalidatesCapturedCore()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 11001, 1611001);
        await EquipGuardian(alice, 33401, 1633401, slot: 2, pieces: 500);
        await EquipGuardian(alice, 31401, 1631401, slot: 3, pieces: 500);
        var s = Session("alice");
        var pets = _app.Services.GetRequiredService<PetService>();
        await Loop.InvokeAsync(() =>
        {
            var original = pets.Guardians(s).ToArray();
            var fairy = Assert.Single(original, p => p.Core != null);
            Assert.Equal(33401, fairy.PetId);
            var plan = s.Pets!.SetupPlans[s.Pets.CurrentPlan];
            plan.SetUpPets[2] = 31401;
            plan.SetUpPets[3] = 33401;
            Assert.Equal(31401, Assert.Single(pets.Guardians(s), p => p.Core != null).PetId);
            Assert.False(pets.GuardianEquipped(s, fairy));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task GuardianFairyCoreImmunityEndsWhenItsFiniteShieldIsConsumed()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 11001, 1611001);
        await EquipGuardian(alice, 33401, 1633401, slot: 2, pieces: 500);
        var s = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2318601));
        await Loop.InvokeAsync(async () =>
        {
            var shield = Assert.Single(s.Buffs, b => b.ConfigId == 2318601);
            Assert.True(shield.ShieldDebuffImmunity);
            Assert.Equal((long)Math.Floor(Character.Stats(s)[101] * .05), shield.ShieldRemaining);
            await buffs.AddAsync(s, GuardianPetRules.StunBuff, 10000, 99999);
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
            Assert.Equal(0, await buffs.AbsorbAsync(s, shield.ShieldRemaining));
            await buffs.AddAsync(s, GuardianPetRules.StunBuff, 10000, 99999);
            Assert.Contains(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
        });
    }

    [ClientDataFact]
    public async Task GuardianHopeCorePreventsLethalDamageAndDoesNotCreateShieldDamage()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 11001, 1611001);
        await EquipGuardian(alice, 31401, 1631401, slot: 2, pieces: 500);
        var s = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2316601));
        var attacker = await Target("alice", stationary: true);
        var hit = JsonSerializer.SerializeToElement(new
        { _iID = 789123, _iSkillHit = 10000, _iDmgType = 1, _kDamageParam1 = CoreZeroCoefficient, _kDamageParam2 = CoreLethalDamage });
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(s, long.MaxValue, null);
            await combat.MonsterSkillHitAsync(attacker, hit, [s], attacker.Uid);
            Assert.False(s.Dead);
            Assert.Equal((long)Math.Ceiling(Character.Stats(s)[101] * .05), Character.Stats(s)[2000]);
        });
        var damage = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 789123);
        Assert.Equal(0, Assert.Single(damage.Dsts).SheildDamage);
    }

    [ClientDataFact]
    public async Task GuardianWanderCoreSlashesThenDoublesDefenceDebuffAndStunsHighHpTarget()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 11001, 1611001);
        await EquipGuardian(alice, 32401, 1632401, slot: 2, pieces: 500);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2317601);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == target.Uid && b.Buffs.Any(x => x.ConfigId == GuardianPetRules.StunBuff));
        var buffs = _app.Services.GetRequiredService<BuffService>();
        Assert.Contains(buffs.UnitAttributes(target), p => p.Id == 1052 && p.Value == -200);
        Assert.True(buffs.UnitForbidden(target, 3));
    }

    [ClientDataFact]
    public async Task GuardianZherCoreAddsOneShareOfPrimaryDamageWithoutRecursing()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 20003, 1620003, pieces: 40);
        await EquipGuardian(alice, 30401, 1630401, slot: 2, pieces: 500);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2346101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2346201);
        var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2315601);
        long primaryDamage = Assert.Single(first.Dsts, d => d.DstID == target.Uid).Damage;
        long coreDamage = Assert.Single(extra.Dsts, d => d.DstID == target.Uid).Damage;
        Assert.Equal((long)Math.Floor(primaryDamage * .4), coreDamage);
        Assert.Equal(100000 - primaryDamage - coreDamage, target.Hp);
    }
}
