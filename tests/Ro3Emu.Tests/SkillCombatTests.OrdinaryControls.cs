using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(902001, 902601)]
    [InlineData(902101, 902801)]
    [InlineData(902201, 0)]
    public async Task OrdinaryPlayerControlsBreakOnPositiveDamageWithoutChangingFollowupOwnership(int control, int followup)
    {
        await using var alice = await InWorld("ControlHit");
        var player = Session("controlhit");
        var monster = await Target("controlhit", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var normal = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", monster.Template.NormalSkill)!.Value;
        await Loop.InvokeAsync(async () =>
        {
            monster.StaysOutOfBattle = true;
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            await buffs.AddAsync(player, control, 5000, monster.Uid);
            Assert.False(Assert.Single(player.Buffs, b => b.ConfigId == control).IsGuardianOwned);
            long before = Character.Stats(player)[2000];
            Assert.Equal(1, await combat.MonsterSkillHitAsync(monster, normal, [player], monster.Uid));
            Assert.InRange(Character.Stats(player)[2000], 1, before - 1);
            Assert.DoesNotContain(player.Buffs, b => b.ConfigId == control);
            if (followup != 0)
            {
                var next = Assert.Single(player.Buffs, b => b.ConfigId == followup);
                Assert.False(next.IsGuardianOwned);
                Assert.Equal(monster.Uid, next.SourceId);
            }
            else Assert.DoesNotContain(player.Buffs, b => b.ConfigId is 902601 or 902801);
        });
    }

    [ClientDataFact]
    public async Task OrdinaryPlayerControlImmunityRejectsAnOrdinaryStun()
    {
        await using var alice = await InWorld("ControlImmune");
        var player = Session("controlimmune");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(player, 903201, 5000, player.Role!.Id);
            Assert.False(Assert.Single(player.Buffs, b => b.ConfigId == 903201).IsGuardianOwned);
            await buffs.AddAsync(player, 901601, 5000, 99999);
            Assert.DoesNotContain(player.Buffs, b => b.ConfigId == 901601);
            Assert.False(buffs.HasForbidden(player, ForbiddenState.Walk));
            Assert.False(buffs.HasForbidden(player, ForbiddenState.CastSkill));
        });
    }

    [ClientDataFact]
    public async Task OrdinaryMonsterStunStopsMovementNormalAttacksAndSkillDamage()
    {
        await using var alice = await InWorld("MonsterStun");
        var player = Session("monsterstun");
        var monster = await Target("monsterstun", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var normal = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", monster.Template.NormalSkill)!.Value;
        await Loop.InvokeAsync(async () =>
        {
            monster.StaysOutOfBattle = true;
            monster.Moving = true;
            monster.FromX = monster.X; monster.FromY = monster.Y; monster.FromZ = monster.Z;
            monster.ToX = monster.X + 2; monster.ToY = monster.Y; monster.ToZ = monster.Z;
            monster.StartMs = MonsterService.NowMs; monster.EndMs = monster.StartMs + 2000;
            await buffs.AddSupportAsync(monster, 901601, 5000, player.Role!.Id);
            Assert.True(buffs.UnitForbidden(monster, ForbiddenState.Walk));
            Assert.False(buffs.UnitForbidden(monster, ForbiddenState.CastSkill));
            Assert.True(buffs.UnitActionBlocked(monster, ForbiddenState.CastSkill));
            Assert.Equal(1UL << 18, buffs.UnitStaggerMask(monster));
            var entity = monster.Entity();
            buffs.PopulateEntity(monster, entity);
            Assert.Equal(1UL << 18, entity.ObjStagger.StaggerMask);
            Assert.True(buffs.UnitForbidden(monster, ForbiddenState.NormalAttack));
            monsters.Tick(monster, _app.Services.GetRequiredService<World>().Map(monster.WorldScene));
            Assert.False(monster.Moving);
            long before = Character.Stats(player)[2000];
            await monsters.Attack!(monster, player);
            Assert.Equal(0, await combat.MonsterSkillHitAsync(monster, normal, [player], monster.Uid));
            Assert.Equal(before, Character.Stats(player)[2000]);
        });
    }

    [ClientDataFact]
    public async Task OrdinaryPlayerFreezeRejectsSkillThroughStaggerWithoutInventingAForbiddenBit()
    {
        await using var alice = await InWorld("FrozenCaster");
        await Gm("frozencaster", "!job 2100");
        await Teach("frozencaster", 12004);
        var player = Session("frozencaster");
        var target = await Target("frozencaster", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            target.StaysOutOfBattle = true;
            await buffs.AddAsync(player, 902001, 5000, target.Uid);
            Assert.False(buffs.HasForbidden(player, ForbiddenState.CastSkill));
            Assert.True(buffs.ActionBlocked(player, ForbiddenState.CastSkill));
            Assert.False(buffs.ActionBlocked(player, ForbiddenState.CastSkill, BuffService.ControlReleaseSkillGroup));
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBadState, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Null(player.SkillCast);
    }
}
