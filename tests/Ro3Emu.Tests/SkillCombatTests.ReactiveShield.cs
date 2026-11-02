using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 200)]
    [InlineData(5, 400)]
    public async Task ReflectDamageUsesTrueChildOnFiveEnemiesWithOneHalfSecondCooldown(int level, long expected)
    {
        await using var bot = await InWorld("RoyalReflect");
        await Gm("royalreflect", "!lvl 99"); await Gm("royalreflect", "!job 1410");
        await Teach("royalreflect", 11609, level);
        await EquipConditionFixtureAsync("royalreflect", 2500120, 2);
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 101, 100_000);
        var player = Session("royalreflect");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < 7; i++) targets.Add(await Target("royalreflect", stationary: true, immobile: true));
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(player.WorldScene);
            foreach (var ambient in map.Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            { ambient.Hp = 0; ambient.Dead = true; await world.RemoveUnitAsync(ambient); }
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                target.X = player.Position.X + (i == 6 ? 6.5f : .3f * (i + 1));
                target.Y = player.Position.Y; target.Z = player.Position.Z;
                target.Hp = 100_000; target.StaysOutOfBattle = true; target.KeepsDamage = true;
                target.Template = target.Template with { Def = 0, Element = 1 };
                map.Moved(target);
            }
            await Character.SetHpSpAsync(player, 100_000, null);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1160900 + level });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 1161000 + level));
        await Loop.InvokeAsync(async () =>
        {
            await combat.MonsterSkillHitAsync(targets[0], DefenceAttack(998911), [player], targets[0].Uid);
            Assert.All(targets.Take(5), m => Assert.Equal(100_000 - expected, m.Hp));
            Assert.All(targets.Skip(5), m => Assert.Equal(100_000, m.Hp));
            await combat.MonsterSkillHitAsync(targets[0], DefenceAttack(998912), [player], targets[0].Uid);
            Assert.All(targets.Take(5), m => Assert.Equal(100_000 - expected, m.Hp));
        });
        await Task.Delay(550);
        await Loop.InvokeAsync(async () =>
        {
            await combat.MonsterSkillHitAsync(targets[0], DefenceAttack(998913, type: 1), [player], targets[0].Uid);
            Assert.All(targets.Take(5), m => Assert.Equal(100_000 - 2 * expected, m.Hp));
            foreach (var buff in player.Buffs.Where(b => b.ConfigId / 100 == 11610).ToArray())
                await _app.Services.GetRequiredService<BuffService>().RemoveAsync(player, buff);
        });
        await Task.Delay(550);
        await Loop.InvokeAsync(async () =>
        {
            await combat.MonsterSkillHitAsync(targets[0], DefenceAttack(998914), [player], targets[0].Uid);
            Assert.All(targets.Take(5), m => Assert.Equal(100_000 - 2 * expected, m.Hp));
        });
        var hits = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Where(h => h.SkillId == 1161800 + level).ToArray();
        Assert.Equal(10, hits.Length);
        Assert.All(hits, h => Assert.Equal(expected, Assert.Single(h.Dsts).Damage));
    }

    [ClientDataTheory]
    [InlineData(1, 200)]
    [InlineData(5, 400)]
    public async Task ReflectDamagePvpDoesNotReflectAgainOrHitFriendlyPlayers(int level, long expected)
    {
        await using var sourceBot = await InWorld("ReflectPvpSource");
        await using var targetBot = await InWorld("ReflectPvpTarget");
        IsolateDirectHealing(); // 等待反射冷卻時自然回血不得污染此案例的精確扣血與非遞迴斷言。
        foreach (string account in new[] { "reflectpvpsource", "reflectpvptarget" })
        {
            await Gm(account, "!lvl 99"); await Gm(account, "!job 1410"); await Teach(account, 11609, level);
            await EquipConditionFixtureAsync(account, 2500120, 2);
        }
        var source = Session("reflectpvpsource"); var defender = Session("reflectpvptarget");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, defender])));
        foreach (var (bot, player) in new[] { (sourceBot, source), (targetBot, defender) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(bot, 101, 100_000); await HealingStat(bot, 103, 1000); await HealingStat(bot, 105, 0);
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1160900 + level });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                b => b.UnitId == player.Role!.Id && b.Buffs.Any(x => x.ConfigId == 1161000 + level));
        }
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((a, b) => a != b, (_, _) => Task.CompletedTask);
            combat.Rolls = new GuardianStableDamageRolls();
            await Character.SetHpSpAsync(source, 100_000, null); await Character.SetHpSpAsync(defender, 100_000, null);
            source.Position = defender.Position.Clone(); source.Position.X += 1;
            Assert.True(combat.Hostile(source, defender)); Assert.True(combat.Hostile(defender, source));
            Assert.Contains(defender.Buffs, b => b.ConfigId == 1161000 + level && b.ExpireMs > MonsterService.NowMs);
            Assert.Equal(level, _app.Services.GetRequiredService<SkillService>().Level(defender.Progress!, 11609));
            await combat.CastExtraOnPlayerAsync(source,
                new Ro3Emu.Server.Data.Config.SkillConfig(DefenceAttack(998915, type: 1)), defender);
            Assert.Equal(99_990, defender.CurrentHp);
            Assert.Equal(100_000 - expected, source.CurrentHp); // 只受原始10點，不因反射再互相反射。
        });
        var reflected = await Next(sourceBot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1161800 + level);
        Assert.Equal(expected, Assert.Single(reflected.Dsts).Damage);
        await Task.Delay(550);
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((_, _) => false, (_, _) => Task.CompletedTask);
            foreach (var observer in combat.PvpSkillHitPublished)
                await observer(source, defender, DefenceAttack(998916), 10, DamageHitResult.Normal);
            Assert.Equal(100_000 - expected, source.CurrentHp);
        });
    }

    [ClientDataFact]
    public async Task ReflectDamageAlsoRunsAfterTheNormalMonsterAttackPublishes()
    {
        await using var bot = await InWorld("ReflectSwing");
        await Gm("reflectswing", "!lvl 99"); await Gm("reflectswing", "!job 1410");
        await Teach("reflectswing", 11609);
        await EquipConditionFixtureAsync("reflectswing", 2500120, 2);
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 101, 100_000);
        var source = Session("reflectswing");
        var enemy = await Target("reflectswing", stationary: true, immobile: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        await Loop.InvokeAsync(async () =>
        {
            enemy.Hp = 100_000; enemy.StaysOutOfBattle = true; enemy.KeepsDamage = true;
            enemy.Template = enemy.Template with { Def = 0, Element = 1, Atk = 100 };
            await Character.SetHpSpAsync(source, 100_000, null);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1160901 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 1161001));
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        Assert.NotNull(monsters.Attack);
        await Loop.InvokeAsync(() => monsters.Attack(enemy, source));
        var normal = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == enemy.Uid);
        Assert.True(Assert.Single(normal.Dsts).Damage > 0);
        var reflected = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1161801 && h.Dsts.Any(d => d.DstID == enemy.Uid));
        Assert.Equal(200, Assert.Single(reflected.Dsts, d => d.DstID == enemy.Uid).Damage);
        await Loop.InvokeAsync(() => { Assert.Equal(99_800, enemy.Hp); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ReflectDamageSkipsAPreselectedPlayerWhoChangesEpochDuringPriorHitPublication()
    {
        await using var ownerBot = await InWorld("RflEpochOwner");
        await using var firstBot = await InWorld("RflEpochFirst");
        await using var lastBot = await InWorld("RflEpochLast");
        await Gm("rflepochowner", "!lvl 99"); await Gm("rflepochowner", "!job 1410");
        await Teach("rflepochowner", 11609);
        await EquipConditionFixtureAsync("rflepochowner", 2500120, 2);
        var owner = Session("rflepochowner"); var first = Session("rflepochfirst"); var last = Session("rflepochlast");
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(owner.SceneEntry, [owner, first, last])));
        foreach (var (bot, player) in new[] { (ownerBot, owner), (firstBot, first), (lastBot, last) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(bot, 101, 100_000); await HealingStat(bot, 105, 0);
        }
        await HealingStat(ownerBot, 103, 1000);
        await ownerBot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1160901 });
        Assert.Equal(0, (await Next(ownerBot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(ownerBot, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 1161001));
        var enemy = await Target("rflepochowner", stationary: true, immobile: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        bool advance = true;
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true; enemy.X = owner.Position.X + 10;
            _app.Services.GetRequiredService<World>().Map(owner.WorldScene).Moved(enemy);
            first.Position = owner.Position.Clone(); first.Position.X += .2f;
            last.Position = owner.Position.Clone(); last.Position.X += .4f;
            combat.Rolls = new GuardianStableDamageRolls();
            combat.SetEventPvPHandlers((a, b) => a == owner && b != owner, (_, _) => Task.CompletedTask);
            combat.PvpSkillHitPublished.Add((a, b, _, _, _) =>
            {
                if (a == owner && b == first && advance) { advance = false; last.SceneGeneration++; }
                return Task.CompletedTask;
            });
            await Character.SetHpSpAsync(owner, 100_000, null);
            await Character.SetHpSpAsync(first, 100_000, null); await Character.SetHpSpAsync(last, 100_000, null);
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998917), [owner], enemy.Uid);
            Assert.Equal(99_800, first.CurrentHp); Assert.Equal(100_000, last.CurrentHp);
        });
        await Task.Delay(550);
        await Loop.InvokeAsync(async () =>
        {
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998918), [owner], enemy.Uid);
            Assert.Equal(99_600, first.CurrentHp); Assert.Equal(99_800, last.CurrentHp);
        });
    }
}
