using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task GuardianWildRoseReactiveSlowsAHostilePlayerAndCannotHealThatEnemy()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await EquipGuardian(alice, 20003, 1620003, pieces: 40);
        await EquipGuardian(alice, 12001, 1612001, slot: 2);
        var source = Session("alice");
        var target = Session("bob");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, target])));
        foreach (var (bot, session) in new[] { (alice, source), (bob, target) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await Gm("bob", "!lvl 50");
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
            await Character.SetHpSpAsync(target, long.MaxValue, null);
        });
        long speed = Character.Stats(target)[113];
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2346101, TargetId = target.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2355101));
        Assert.True(Character.Stats(target)[113] < speed);
        Assert.Equal(2000, Assert.Single(target.Buffs, b => b.ConfigId == 2355101).DurationMs);
        // Recovery targets must use the same hostility predicate as damage, including private event matches.
        await Loop.InvokeAsync(() =>
        {
            source.Pets!.SetupPlans[source.Pets.CurrentPlan].SetUpPets.Remove(1);
            return Task.CompletedTask;
        });
        await EquipGuardian(alice, 11001, 1611001);
        long hp = Character.Stats(target)[2000];
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2356101);
        Assert.Equal(hp, Character.Stats(target)[2000]);
    }

    [ClientDataFact]
    public async Task GuardianClockDispelsControlAndItsImmunityRejectsNewControl()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 23002, 1623002, pieces: 40);
        var s = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => buffs.AddAsync(s, GuardianPetRules.StunBuff, 20000, 999999));
        Assert.True(buffs.HasForbidden(s, 3));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2352301 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.Buffs.Any(x => x.ConfigId == GuardianPetRules.ControlImmunityBuff));
        await Loop.InvokeAsync(async () =>
        {
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
            Assert.False(buffs.HasForbidden(s, 3));
            await buffs.AddAsync(s, GuardianPetRules.StunBuff, 20000, 999999);
            Assert.DoesNotContain(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
            await buffs.RemoveAsync(s, s.Buffs.Single(b => b.ConfigId == GuardianPetRules.ControlImmunityBuff));
            await buffs.AddAsync(s, GuardianPetRules.StunBuff, 20000, 999999);
            Assert.Contains(s.Buffs, b => b.ConfigId == GuardianPetRules.StunBuff);
        });
    }

    [ClientDataFact]
    public async Task GuardianMonsterStatDebuffsChangeCombatValuesAndAreNotSharedBetweenMonsters()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var target = await Loop.InvokeAsync(async () =>
        {
            var template = monsters.Template(5000001) ?? throw new InvalidOperationException("Missing exported monster");
            return await monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
                new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId,
                customTemplate: template with { Atk = 1000, Matk = 800, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Cannot create test target");
        });
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        long original = combat.MonsterStats(target)[103];
        await Loop.InvokeAsync(() => buffs.AddGuardianAsync(target, 2324301, 5000, Session("alice").Role!.Id));
        Assert.True(combat.MonsterStats(target)[103] < original);
        Assert.Equal(original, target.Template.Atk);
        await Loop.InvokeAsync(() => buffs.ClearUnitAsync(target));
        Assert.Equal(original, combat.MonsterStats(target)[103]);
    }

    [ClientDataFact]
    public async Task GuardianBoboShieldTakesGoatDurationButNotChocoBuffDuration()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 13001, 1613001);
        await EquipGuardian(alice, 23001, 1623001, slot: 2, pieces: 40);
        await EquipGuardian(alice, 10001, 1610001, slot: 3);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2354001 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2354101));
        var shield = Assert.Single(Session("alice").Buffs, b => b.ConfigId == 2354101);
        Assert.Equal(5500, shield.DurationMs);
        Assert.True(shield.ShieldRemaining > 0);
    }

    [ClientDataFact]
    public async Task GuardianFairyReactiveGivesRealCastSpeedAndExpires()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 11001, 1611001);
        await EquipGuardian(alice, 21001, 1621001, slot: 2, pieces: 40);
        var s = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var row = JsonSerializer.SerializeToElement(new { _iChantFix = 100, _iChantVar = 2000 });
        Task<int> Chant() => Loop.InvokeAsync(() => Task.FromResult(combat.ChantMs(s, row)));
        Assert.Equal(2100, await Chant()); // no guardian buff: the configured fixed plus variable time
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2348301));
        Assert.Equal(10000, await Loop.InvokeAsync(() => Task.FromResult(Assert.Single(s.Buffs, b => b.ConfigId == 2348301).DurationMs)));
        Assert.True(await Chant() < 2100);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().RemoveAsync(s, s.Buffs.Single(b => b.ConfigId == 2348301)));
        Assert.Equal(2100, await Chant());
    }

    [ClientDataFact]
    public void GuardianAllExportedFamiliesHaveAnUnambiguousChildAndSupportedAttributeReactionsValidateAtEveryStar()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        int count = 0;
        foreach (var stage in data.Table("PetStarConfig").Values)
        {
            var wrapper = data.Row("SkillConfig", GameData.Int(stage, "_iProtectSkill"));
            if (wrapper == null) continue;
            var child = GuardianPetRules.Child(data, wrapper.Value);
            Assert.True(child.HasValue, $"Unresolved guardian for pet {GameData.Int(stage, "_iPetID")}, wrapper {GameData.Int(wrapper.Value, "_iID")}");
            var skill = new GuardianPetRules.Skill(2, (int)GameData.Int(stage, "_iPetID"), (int)GameData.Int(stage, "_iStar"),
                (int)GameData.Int(stage, "_iStage"), 0, wrapper.Value, child.Value,
                data.Row("SkillConfig", GameData.Int(stage, "_iPassiveMain")), GuardianPetRules.Classify(child.Value) ?? GuardianPetRules.Kind.Unsupported);
            Assert.NotEqual(GuardianPetRules.Kind.Unsupported, skill.Kind);
            Assert.NotEqual(GuardianPetRules.Reaction.None, skill.Reactive);
            if (skill.Reactive is GuardianPetRules.Reaction.CastSpeed or GuardianPetRules.Reaction.WaterResistance
                or GuardianPetRules.Reaction.PhysicalAttackBuff or GuardianPetRules.Reaction.DamageBuff or GuardianPetRules.Reaction.DefenceBuff)
                Assert.True(GuardianPetRules.ReactiveBuff(data, skill) > 0, $"Mismatching attribute reaction for {skill.PetId}, star {skill.Star}");
            count++;
        }
        Assert.Equal(1176, count);
    }
}
