using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class GuardianCertainProcRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override int Proc(int max) => 0;
    }
    [ClientDataFact]
    public async Task GuardianChimeraSpendsThreeControlChargesAndPulsesRealShields()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 33003, 1633003, pieces: 500);
        var s = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2309201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 910101 && x.PileCount == 3));
        await Loop.InvokeAsync(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await buffs.AddAsync(s, GuardianPetRules.StunBuff, 5000, 99999);
                Assert.DoesNotContain(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
            }
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == 910101);
            await buffs.AddAsync(s, GuardianPetRules.StunBuff, 5000, 99999);
            Assert.Contains(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
        });
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2309201));
        Assert.True(buffs.ShieldAmount(s) > 0);
    }

    [ClientDataFact]
    public async Task GuardianIncubusHasNoPlaceholderBurstAndAuraStopsWhenUnequipped()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 31003, 1631003, pieces: 500);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2308201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2308601 && h.Dsts.Any(d => d.DstID == target.Uid));
        long after = await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            s.Pets!.SetupPlans[s.Pets.CurrentPlan].SetUpPets.Remove(1);
            return Task.FromResult(target.Hp);
        });
        await Task.Delay(1200);
        Assert.Equal(after, target.Hp);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 2308101);
    }

    [ClientDataFact]
    public async Task GuardianTurtleDealsDamageAndHealsEachSecond()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 23004, 1623004, pieces: 500);
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () => { target.Hp = 100000; await Character.SetHpSpAsync(s, 1, null); });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2343201, TargetId = target.Uid, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var damage = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 2343101 && h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.True(Assert.Single(damage.Dsts, d => d.DstID == target.Uid).Damage > 0);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 2343101 && h.Dsts.Any(d => d.DamageType == DamageType.Heal && d.DstID == s.Role!.Id));
        long preparedHp = Character.Stats(s)[101] / 2; // survive the monster's counterattack until the scheduled tick
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(s, preparedHp, null));
        long expected = Math.Min(1000, Character.Stats(s)[101] - preparedHp);
        try
        {
            var tick = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SkillId == 2343101 && h.Dsts.Any(d => d.DamageType == DamageType.Heal && d.Damage == expected));
            Assert.Contains(tick.Dsts, d => d.Damage == expected);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Turtle periodic heal missing: expected={expected}, hp={Character.Stats(s)[2000]}/{Character.Stats(s)[101]}, dead={s.Dead}; "
                + string.Join("; ", alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
                    .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Select(h => h.ToString())));
        }
    }

    [ClientDataFact]
    public async Task GuardianBaphometOnlyRootsAnAlreadySlowedTarget()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 22001, 1622001, pieces: 500);
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianCertainProcRolls();
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        async Task Cast()
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2340001, TargetId = target.Uid, TargetPos = target.Pos });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2340101);
        }
        await Cast();
        Assert.False(buffs.UnitHasGroup(target, 9024));
        Assert.True(target.SpeedCm(MonsterService.NowMs) < target.Template.SpeedCm);
        await Loop.InvokeAsync(() => { s.SkillCds.Clear(); s.SkillModeCds.Clear(); return Task.CompletedTask; });
        await Cast();
        Assert.True(buffs.UnitHasGroup(target, 9024));
    }
}
