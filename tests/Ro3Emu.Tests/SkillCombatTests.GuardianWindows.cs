using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static readonly int[] WindowZeroDamage = [0];
    private static readonly int[] WindowNegligibleDamage = [1];

    [ClientDataFact]
    public async Task GuardianDullahanWindowProcsFromOwnerAttackAndNeverRecurses()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 32002, 1632002, pieces: 500);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2327401, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2327201));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var proc = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2327501);
        Assert.True(Assert.Single(proc.Dsts).Damage > 0);
        Assert.True(target.Hp > 90000); // no recursively generated auxiliary chain
    }

    [ClientDataFact]
    public async Task GuardianPiametteHealsOnlyOnceAfterNonlethalLowHpHit()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 31201, 1631201, pieces: 500);
        var source = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var attacker = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2332201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2332101));
        await Task.Delay(100); // registration follows the buff publication
        var row = JsonSerializer.SerializeToElement(new
        { _iID = 789124, _iSkillHit = 10000, _iDmgType = 1, _kDamageParam1 = WindowZeroDamage, _kDamageParam2 = WindowNegligibleDamage });
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, 10, null));
        await Loop.InvokeAsync(() => combat.MonsterSkillHitAsync(attacker, row, [source], attacker.Uid));
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 2332101 && h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal((long)Math.Floor(Character.Stats(source)[101] * .05), Assert.Single(heal.Dsts).Damage);
        long before = Character.Stats(source)[2000];
        await Loop.InvokeAsync(() => combat.MonsterSkillHitAsync(attacker, row, [source], attacker.Uid));
        Assert.True(Character.Stats(source)[2000] < before);
    }

    [ClientDataFact]
    public async Task GuardianShinobiUtilityAllowsAndCleansesControlledRecoveryCast()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 11001, 1611001);
        await EquipGuardian(alice, 33101, 1633101, slot: 2, pieces: 500);
        var source = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => buffs.AddAsync(source, GuardianPetRules.StunBuff, 10000, 99999));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
        Assert.Contains(source.Buffs, b => b.ConfigId == GuardianPetRules.ControlImmunityBuff);
    }
}
