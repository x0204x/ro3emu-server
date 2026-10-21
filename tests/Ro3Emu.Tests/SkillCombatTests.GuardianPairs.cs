using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task GuardianFairyHealsAndBuffsWhileChocoAndRockerReactFromUtility()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 21001, 1621001, pieces: 40);
        await EquipGuardian(alice, 10001, 1610001, slot: 2);
        await EquipGuardian(alice, 11001, 1611001, slot: 3);
        var s = Session("alice");
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(s, 1, null));
        var stats = Character.Stats(s);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2348001 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2348101);
        Assert.Equal((long)Math.Floor(Math.Max(stats[103], stats[104]) * 1.8), Assert.Single(heal.Dsts).Damage);
        var reactive = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2356301);
        Assert.Equal((long)Math.Floor(stats[101] * .012), Assert.Single(reactive.Dsts).Damage);
        Assert.Equal(5500, Assert.Single(s.Buffs, b => b.ConfigId == 2348101).DurationMs);
        Assert.True(Character.Stats(s)[113] > stats[113]);
        Assert.DoesNotContain(s.Buffs, b => b.ConfigId == 2348301); // Fairy primary does not activate its reactive.
    }

    [ClientDataFact]
    public async Task GuardianGoatUsesHpShieldAndTriggersBoboDefenceButNotChocoBuffDuration()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 23001, 1623001, pieces: 40);
        await EquipGuardian(alice, 13001, 1613001, slot: 2);
        await EquipGuardian(alice, 10001, 1610001, slot: 3);
        var s = Session("alice");
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(s, 1, null));
        var stats = Character.Stats(s);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2342201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2354301));
        var shield = Assert.Single(s.Buffs, b => b.ConfigId == 2342301);
        Assert.Equal((long)Math.Floor(stats[101] * .025), shield.ShieldRemaining);
        Assert.Equal(3000, shield.DurationMs); // Goat is Buff+Shield, but shield tag 3004 is not Choco's 3001.
        Assert.Contains(shield.Changes, c => c.Attr == 1230 && c.Value == 250);
        var defence = Assert.Single(s.Buffs, b => b.ConfigId == 2354301);
        Assert.Equal(5000, defence.DurationMs);
        Assert.Contains(defence.Changes, c => c.Attr == 1052 && c.Value == 500);
        Assert.Contains(defence.Changes, c => c.Attr == 1062 && c.Value == 500);
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(3, await _app.Services.GetRequiredService<BuffService>().AbsorbAsync(s, shield.ShieldRemaining + 3));
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == 2342301); // reduction leaves with the spent shield.
        });
    }

    [ClientDataFact]
    public async Task GuardianLavaDamageTriggersWildRoseSlowOnASurvivingMonster()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 20003, 1620003, pieces: 40);
        await EquipGuardian(alice, 12001, 1612001, slot: 2);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        int speed = target.SpeedCm(MonsterService.NowMs);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2346101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2346201);
        Assert.Contains(hit.Dsts, d => d.DstID == target.Uid && d.Damage > 0 && !d.IsDead);
        Assert.Equal(1500, target.SlowMovementBp);
        Assert.InRange(target.SlowMovementExpireMs - MonsterService.NowMs, 500, 2000);
        Assert.True(target.SpeedCm(MonsterService.NowMs) < speed);
    }

    [ClientDataFact]
    public async Task GuardianGoatReductionAppliesToRangedPhysicalHitOnlyWhileShieldExists()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 50");
        var s = Session("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var ranged = data.Table("SkillConfig").Values.First(r => GameData.Int(r, "_iDistanceType") == 2
            && GameData.Int(r, "_iDmgType") == 1 && GuardianPetRules.Numbers(r, "_kDamageParam1").Any(v => v > 0));
        var target = await Loop.InvokeAsync(async () =>
        {
            var template = monsters.Template(5000001);
            Assert.NotNull(template);
            var unit = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
                new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId,
                customTemplate: template with { NormalSkill = (int)GameData.Int(ranged, "_iID"), RoamDistance = 0 });
            Assert.NotNull(unit);
            combat.IncomingDamageAdjusters.Add((_, _, _, _) => Task.FromResult(400L));
            await buffs.AddGuardianAsync(s, 2342301, 20000, s.Role!.Id, 5);
            await monsters.Attack!(unit, s);
            return unit;
        });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == target.Uid);
        Assert.Equal(385, Assert.Single(hit.Dsts).Damage); // 400 - 2.5%, then the last 5 shield points.
        Assert.Equal(5, Assert.Single(hit.Dsts).SheildDamage);
        Assert.DoesNotContain(s.Buffs, b => b.ConfigId == 2342301);
        await Loop.InvokeAsync(() => monsters.Attack!(target, s));
        var unprotected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == target.Uid);
        Assert.Equal(400, Assert.Single(unprotected.Dsts).Damage);
    }
}
