using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 1500, 4000)]
    [InlineData(5, 3500, 6000)]
    public async Task EmblemWindCutterUsesAuthoredBuffOnNormalAndSkillHitsWithoutStackingOrRetainingRevokedBonus(
        int level, int normalBonus, int boostedBonus)
    {
        await using var bot = await InWorld("EchoWind");
        await using var neighbour = await InWorld("EchoWindFriend");
        await Gm("echowind", "!lvl 99"); await Gm("echowind", "!job 1400");
        await Teach("echowind", 11303, level); await Teach("echowind", 11001);
        await HealingStat(bot, 103, 1000);
        var source = Session("echowind"); var friend = Session("echowindfriend");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        var target = await Target("echowind", stationary: true, immobile: true); await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(() => { friend.Position = source.Position.Clone(); return Task.CompletedTask; });
        async Task CastWind(int buffId)
        {
            await Loop.InvokeAsync(async () =>
            { await combat.InterruptAsync(source); await CombatService.ResetCooldownAsync(source, 11303); });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1130300 + level });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                n => n.UnitId == source.Role!.Id && n.Buffs.Any(b => b.ConfigId == buffId));
            Assert.DoesNotContain(friend.Buffs, b => b.ConfigId / 100 is 11303 or 11328);
        }
        async Task CheckDamage(int bonus)
        {
            foreach (int id in new[] { 1000001, 1100101 })
            {
                var original = SkillConfig.Get(data, id)!.Value;
                var row = new SkillConfig(combat.EffectiveSkillRow(source, id)!.Value);
                Assert.Equal(original.DamageParam1[0] + bonus, row.DamageParam1[0]);
                long expected = (long)Math.Round(Character.Stats(source)[103] * (original.DamageParam1[0] + bonus)
                    / 10000d + original.DamageParam2[0]);
                Assert.Equal(expected, await CardOffenceHitAsync(bot, source, target, original.Row));
            }
        }
        await EmblemMarksAsync(source, 1001, 9);
        await CastWind(1130300 + level); await CheckDamage(normalBonus);
        await EmblemMarksAsync(source, 1001, 10);
        await CastWind(1132800 + level); await CheckDamage(boostedBonus);
        Assert.Single(source.Buffs, b => b.ConfigId / 100 is 11303 or 11328);
        long expiry = source.Buffs.Single(b => b.ConfigId == 1132800 + level).ExpireMs;
        await EmblemMarksAsync(source, 1001, 0);
        var restored = Assert.Single(source.Buffs, b => b.ConfigId / 100 is 11303 or 11328);
        Assert.Equal(1130300 + level, restored.ConfigId);
        Assert.True(restored.ExpireMs <= expiry + 2); // 不因撤授權重給完整3/5秒。
        await CheckDamage(normalBonus);
        await EmblemMarksAsync(source, 1001, 10); await CastWind(1132800 + level);
        await Gm("echowind", "!job 1000");
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId / 100 == 11328);
        Assert.Equal(0, new SkillConfig(combat.EffectiveSkillRow(source, 1130300 + level)!.Value).Mode);
        Assert.DoesNotContain(source.Progress!.Skills.Keys, g => g == 111302);
    }

    [ClientDataTheory]
    [InlineData(1, 200, 300)]
    [InlineData(5, 400, 600)]
    public async Task EmblemReflectDamageUsesSevenMetresAndFiveSlowStacksOnlyWhileTheStageIsOwned(
        int level, long ordinary, long enhanced)
    {
        await using var bot = await InWorld("EchoReflect");
        await Gm("echoreflect", "!lvl 99"); await Gm("echoreflect", "!job 1410");
        await Teach("echoreflect", 11609, level);
        await EquipConditionFixtureAsync("echoreflect", 2500120, 2);
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 101, 100_000);
        var source = Session("echoreflect"); var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>(); combat.Rolls = new GuardianStableDamageRolls();
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < 3; i++) targets.Add(await Target("echoreflect", stationary: true));
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var m in map.Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            { m.Hp = 0; m.Dead = true; await world.RemoveUnitAsync(m); }
            for (int i = 0; i < targets.Count; i++)
            {
                var m = targets[i]; m.Hp = 100_000; m.KeepsDamage = true; m.StaysOutOfBattle = true;
                m.Template = m.Template with { Def = 0, Element = 1 };
                m.X = source.Position.X + (i == 0 ? 1 : i == 1 ? 6.5f : 7.2f);
                m.Y = source.Position.Y; m.Z = source.Position.Z; map.Moved(m);
            }
            await Character.SetHpSpAsync(source, 100_000, null);
        });
        await EmblemMarksAsync(source, 1003, 5);
        int originalSpeed = targets[1].SpeedCm(MonsterService.NowMs);
        Assert.True(originalSpeed > 0);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1160900 + level });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == source.Role!.Id && n.Buffs.Any(b => b.ConfigId == 1161000 + level));
        async Task Hit() => await Loop.InvokeAsync(() => combat.MonsterSkillHitAsync(
            targets[0], DefenceAttack(998950), [source], targets[0].Uid));
        await Hit(); Assert.Equal(100_000 - ordinary, targets[0].Hp);
        Assert.Equal(100_000, targets[1].Hp); Assert.False(buffs.UnitHasGroup(targets[0], 111631));
        await EmblemMarksAsync(source, 1003, 6);
        for (int hit = 1; hit <= 6; hit++)
        {
            await Task.Delay(550); await Hit();
            Assert.Equal(100_000 - hit * enhanced, targets[1].Hp);
            var packet = await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                n => n.UnitId == targets[1].Uid && n.Buffs.Any(b => b.ConfigId == 11163101 && b.PileCount == Math.Min(hit, 5)));
            Assert.Equal(Math.Min(hit, 5), Assert.Single(packet.Buffs, b => b.ConfigId == 11163101).PileCount);
            Assert.Equal((int)Math.Ceiling(originalSpeed * (1 - .05 * Math.Min(hit, 5)) - 1e-6), targets[1].SpeedCm(MonsterService.NowMs));
        }
        Assert.Equal(100_000, targets[2].Hp);
        await EmblemMarksAsync(source, 1003, 0); await Task.Delay(550);
        long beforeNear = targets[0].Hp, beforeFar = targets[1].Hp;
        await Hit(); Assert.Equal(beforeNear - ordinary, targets[0].Hp); Assert.Equal(beforeFar, targets[1].Hp);
        await Task.Delay(3100);
        Assert.False(buffs.UnitHasGroup(targets[0], 111631)); Assert.False(buffs.UnitHasGroup(targets[1], 111631));
        Assert.Equal(originalSpeed, targets[1].SpeedCm(MonsterService.NowMs));
        // 真11303 Buff的+1500只應套一次；反射列已經Effective，不能由ExtraRow再加第二次。
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>()
            .AddSupportAsync(source, 1130301, 3000, source.Role!.Id));
        await Task.Delay(550);
        long beforeModified = targets[0].Hp;
        await Hit();
        Assert.Equal(ordinary + 150, beforeModified - targets[0].Hp);
        await EmblemMarksAsync(source, 1003, 6); await Gm("echoreflect", "!job 1000");
        beforeNear = targets[0].Hp; await Hit(); Assert.Equal(beforeNear, targets[0].Hp);
        Assert.DoesNotContain(source.Progress!.Skills.Keys, g => g == 111645);
    }

    [ClientDataFact]
    public async Task EmblemReflectDamageAppliesTheAuthoredSlowAndDamageToAHostilePlayerAtSixAndHalfMetres()
    {
        await using var bot = await InWorld("EchoRflOwner");
        await using var enemyBot = await InWorld("EchoRflEnemy");
        await Gm("echorflowner", "!lvl 99"); await Gm("echorflowner", "!job 1410");
        await Teach("echorflowner", 11609, 5);
        await EquipConditionFixtureAsync("echorflowner", 2500120, 2);
        var source = Session("echorflowner"); var enemy = Session("echorflenemy");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, enemy])));
        foreach (var (client, player) in new[] { (bot, source), (enemyBot, enemy) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(client, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await client.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(client, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(client, 101, 100_000); await HealingStat(client, 105, 0);
        }
        await HealingStat(bot, 103, 1000);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new GuardianStableDamageRolls();
            combat.SetEventPvPHandlers((a, b) => a != b, (_, _) => Task.CompletedTask);
            source.Position = enemy.Position.Clone(); source.Position.X += 6.5f;
            await Character.SetHpSpAsync(source, 100_000, null); await Character.SetHpSpAsync(enemy, 100_000, null);
        });
        await EmblemMarksAsync(source, 1003, 6);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1160905 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == source.Role!.Id && n.Buffs.Any(b => b.ConfigId == 1161005));
        long speed = enemy.Speed;
        await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(enemy,
            new SkillConfig(DefenceAttack(998951)), source));
        var reflected = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1161815 && h.Dsts.Any(d => d.DstID == enemy.Role!.Id));
        Assert.Equal(600, Assert.Single(reflected.Dsts).Damage);
        await Next(enemyBot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == enemy.Role!.Id && n.Buffs.Any(b => b.ConfigId == 11163101));
        Assert.Equal((long)Math.Ceiling(speed * .95 - 1e-6), enemy.Speed);
        Assert.Equal(99_400, enemy.CurrentHp);
    }
    [ClientDataFact]
    public async Task AstralFollowUpsApplyTheRealWindCutterModifierOnce()
    {
        await using var bot = await InWorld("AstralOnce");
        await Gm("astralonce", "!lvl 99"); await Gm("astralonce", "!job 2400");
        await Teach("astralonce", 12302); await Teach("astralonce", 12343);
        await HealingStat(bot, 104, 1000);
        var source = Session("astralonce");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var target = await Target("astralonce", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new GuardianStableDamageRolls();
            target.Template = target.Template with { Element = 4 };
            await buffs.AddSupportAsync(source, 1130301, 30_000, source.Role!.Id);
        });
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == source.Role!.Id && n.Buffs.Any(b => b.ConfigId == 1236101 && b.PileCount == 3));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1230201, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        // 原件1234501=5500，真風刃+1500，MATK1000、MDEF0、念對火係數1，故每顆恰700。
        for (int i = 0; i < 3; i++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SkillId == 1234501 && h.Dsts.Any(d => d.DstID == target.Uid));
            Assert.Equal(700, Assert.Single(hit.Dsts).Damage);
        }
    }

}
