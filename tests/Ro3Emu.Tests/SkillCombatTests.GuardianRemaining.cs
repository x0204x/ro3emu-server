using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static readonly int[] RemainingZeroDamage = [0];
    private static readonly int[] RemainingNegligibleDamage = [1];

    [ClientDataFact]
    public async Task GuardianScoutStacksDefenceOnlyAfterDamageAndCapsAtConfiguredMaximum()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 23003, 1623003, pieces: 500);
        var source = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var enemy = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2341201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 2341101));
        await Task.Delay(100);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 2341201);
        var row = JsonSerializer.SerializeToElement(new
        {
            _iID = 998841, _iSkillHit = 10000, _iDmgType = 1,
            _kDamageParam1 = RemainingZeroDamage, _kDamageParam2 = RemainingNegligibleDamage
        });
        await Loop.InvokeAsync(async () =>
        {
            for (int i = 0; i < 7; i++) await combat.MonsterSkillHitAsync(enemy, row, [source], enemy.Uid);
        });
        var defence = Assert.Single(source.Buffs, b => b.ConfigId == 2341201);
        Assert.Equal(5, defence.PileCount);
        Assert.Contains(defence.Changes, c => c.Attr == 1052 && c.Value == 750);
    }

    [ClientDataFact]
    public async Task GuardianCatPrimaryHasFourHitsAndConsumesOwnerBonusOnce()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 30201, 1630201, pieces: 500);
        var source = Session("alice");
        var enemy = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { enemy.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2301301, TargetId = enemy.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        for (int i = 0; i < 4; i++)
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2301401 && h.Dsts.Any(d => d.Damage > 0));
        Assert.Contains(source.Buffs, b => b.ConfigId == 2301401);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemy.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser, b => b.UnitId == source.Role!.Id);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 2301401);
    }

    [ClientDataFact]
    public async Task GuardianMummyKillCastsCurseAtVictimAndDoesNotRecurse()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 30003, 1630003, pieces: 500);
        var nearby = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { nearby.Hp = 100000; return Task.CompletedTask; });
        var enemy = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { enemy.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2312101, TargetPos = enemy.Pos });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 2312101));
        await Task.Delay(100);
        await Loop.InvokeAsync(() => { enemy.Hp = 1; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemy.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var curse = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2312601);
        Assert.Equal(Session("alice").Role!.Id, curse.SrcID);
    }

    [ClientDataFact]
    public async Task GuardianOwlReactiveProducesFiveSeparateLightningHits()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 10001, 1610001);
        await EquipGuardian(alice, 30002, 1630002, slot: 2, pieces: 500);
        var enemy = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { enemy.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2353301 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        for (int i = 0; i < 5; i++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2311501);
            Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        }
    }

    [ClientDataFact]
    public async Task GuardianShinobiMovesOwnerAndDamageEndsStealth()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 33101, 1633101, pieces: 500);
        var source = Session("alice");
        var before = source.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2302201, Face = 0 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 2302201));
        Assert.True(MonsterService.Distance(before.X, before.Z, source.Position.X, source.Position.Z) > 0);
        Assert.NotEqual(0UL, (ulong)source.StatusBits & 2UL);
        var enemy = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemy.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser, n => n.UnitId == source.Role!.Id);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 2302201);
    }
}
