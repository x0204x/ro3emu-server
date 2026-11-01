using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task TimedResonanceShadowAlsoRaisesRealHostilePlayerDamageAndExpires()
    {
        await using var bot = await InWorld("ShadowPvp"); await using var enemyBot = await InWorld("ShadowEnemy");
        await Gm("shadowpvp", "!job 5400"); await Gm("shadowenemy", "!job 5400");
        await Teach("shadowpvp", 15320, 10);
        await EquipConditionFixtureAsync("shadowpvp", 2010120);
        var source = Session("shadowpvp"); var enemy = Session("shadowenemy");
        await CardOffenceRoom((bot, source), (enemyBot, enemy));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>(); var attack = SkillConfig.Get(data, 1000001)!.Value;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(() =>
        {
            enemy.Position = source.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => a == source && b == enemy, (_, _) => Task.CompletedTask);
            combat.SkillCastCompleted.Add((s, _, row) =>
            { if (s == source && new SkillConfig(row).SkillId == 15320) complete.TrySetResult(); return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        async Task<long> Hit()
        {
            long before = enemy.CurrentHp!.Value;
            await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, attack, enemy));
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1000001);
            long damage = Assert.Single(hit.Dsts).Damage;
            Assert.Equal(before - damage, enemy.CurrentHp); return damage;
        }
        long baseline = await Hit(); Assert.True(baseline > 0);
        await EmblemMarksAsync(source, 1003, 10);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1532010, TargetId = enemy.Role!.Id, TargetPos = enemy.Position.Clone() });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await complete.Task.WaitAsync(Wait);
        Assert.Equal(1, Assert.Single(source.Buffs, b => b.ConfigId == 11533101).PileCount);
        Assert.Equal((long)Math.Floor(baseline * 1.05), await Hit());
        await Task.Delay(2100); Assert.Equal(baseline, await Hit());
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task TimedResonanceDragonChangesActualSlashDamageForFiveSecondsAndRevokesWithStage(int level)
    {
        await using var bot = await InWorld("EchoDragon");
        await Gm("echodragon", "!lvl 99"); await Gm("echodragon", "!job 1400");
        await Teach("echodragon", 11303, level); await Teach("echodragon", 11302, level);
        await HealingStat(bot, 103, 1000);
        var source = Session("echodragon");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var target = await Target("echodragon", true, immobile: true);
        await CardOffenceDummyAsync(target); await IsolateAuthoredStageTargetAsync(source, target);
        SkillConfig Row() => new(combat.EffectiveSkillRow(source, 1130200 + level)!.Value);
        async Task Wind()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Complete(GameSession s, ActiveSkillCast _, System.Text.Json.JsonElement row)
            { if (s == source && new SkillConfig(row).SkillId == 11303) done.TrySetResult(); return Task.CompletedTask; }
            await Loop.InvokeAsync(() => { combat.SkillCastCompleted.Add(Complete); return Task.CompletedTask; });
            try
            {
                await Loop.InvokeAsync(async () =>
                { await combat.InterruptAsync(source); source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear(); });
                await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1130300 + level });
                Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await done.Task.WaitAsync(Wait);
            }
            finally { await Loop.InvokeAsync(() => { combat.SkillCastCompleted.Remove(Complete); return Task.CompletedTask; }); }
        }
        await EmblemMarksAsync(source, 1002, 5); await Wind();
        Assert.Equal(0, Row().Mode); Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1133001);
        await EmblemMarksAsync(source, 1002, 6); await Wind();
        var dragon = Assert.Single(source.Buffs, b => b.ConfigId == 1133001);
        Assert.Equal(5000, dragon.DurationMs); Assert.Equal(1, Row().Mode);
        // 去掉另有獨立測試的風刃攻擊加成，只核對龍化模式的真傷害，不能二次套用有效列。
        await Loop.InvokeAsync(async () =>
        { foreach (var b in source.Buffs.Where(b => b.ConfigId / 100 is 11303 or 11328).ToArray()) await buffs.RemoveAsync(source, b); });
        Assert.Equal(level == 1 ? 3900 : 5500, Row().DamageParam1[0]);
        var stats = Character.Stats(source);
        // 原列仍保留攻速每1%加0.2%、最多500%攻速的附加公式；龍化不能漏掉這一層。
        long expected = (long)Math.Round(stats[103] * Row().DamageParam1[0] / 10000d
            * (1 + Math.Min(stats[115], 500) * 20 / 10000d));
        // 使用真施放入口選擇mode；CastExtra的原列覆寫入口不負責選擇職業變體。
        await Loop.InvokeAsync(() => combat.InterruptAsync(source));
        long before = target.Hp;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1130200 + level, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = source.Face });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage); Assert.Equal(before - expected, target.Hp);
        await Task.Delay(5100); Assert.Equal(0, Row().Mode);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1133001);
        await Wind(); Assert.Equal(1, Row().Mode);
        await EmblemMarksAsync(source, 1002, 0); Assert.Equal(0, Row().Mode);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1133001);
        await EmblemMarksAsync(source, 1002, 6); await Wind();
        await Gm("echodragon", "!job 1000"); Assert.Equal(0, Row().Mode);
        Assert.DoesNotContain(source.Progress!.Skills.Keys, g => g == 111303);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task TimedResonanceShadowCapsThreeRealCastsAndAffectsDamageOnlyWithinTwoSeconds(int level)
    {
        await using var bot = await InWorld("EchoShadow");
        await Gm("echoshadow", "!lvl 99"); await Gm("echoshadow", "!job 5400");
        foreach (int group in new[] { 15320, 15322, 15323 }) await Teach("echoshadow", group, level);
        await EquipConditionFixtureAsync("echoshadow", 2010120);
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 102, 10000);
        var source = Session("echoshadow"); var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls(); var data = _app.Services.GetRequiredService<GameData>();
        var target = await Target("echoshadow", true, immobile: true);
        await CardOffenceDummyAsync(target); await IsolateAuthoredStageTargetAsync(source, target);
        var attack = SkillConfig.Get(data, 1000001)!.Value;
        long normal = await CardOffenceHitAsync(bot, source, target, attack.Row);
        async Task Cast(int group)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Complete(GameSession s, ActiveSkillCast _, System.Text.Json.JsonElement row)
            { if (s == source && new SkillConfig(row).SkillId == group) done.TrySetResult(); return Task.CompletedTask; }
            await Loop.InvokeAsync(() => { combat.SkillCastCompleted.Add(Complete); return Task.CompletedTask; });
            try
            {
                await Loop.InvokeAsync(async () =>
                { await combat.InterruptAsync(source); source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear(); });
                await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
                { SkillId = group * 100 + level, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = source.Face });
                Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await done.Task.WaitAsync(Wait);
            }
            finally { await Loop.InvokeAsync(() => { combat.SkillCastCompleted.Remove(Complete); return Task.CompletedTask; }); }
        }
        await EmblemMarksAsync(source, 1003, 9); await Cast(15320);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11533101);
        await EmblemMarksAsync(source, 1003, 10);
        int casts = 0;
        foreach (int group in new[] { 15320, 15322, 15323, 15320 })
        {
            await Cast(group); int count = Math.Min(++casts, 3);
            var chain = Assert.Single(source.Buffs, b => b.ConfigId == 11533101);
            Assert.Equal(2000, chain.DurationMs); Assert.Equal(count, chain.PileCount);
            Assert.Contains(chain.Changes, c => c.Attr == 1215 && c.Value == 500 * count);
            Assert.Equal((long)Math.Floor(normal * (1 + .05 * count)), await CardOffenceHitAsync(bot, source, target, attack.Row));
            Assert.Equal(count, Assert.Single(source.Buffs, b => b.ConfigId == 11533101).PileCount); // 追加傷害不算另一個施放。
        }
        await Task.Delay(2100); Assert.Equal(normal, await CardOffenceHitAsync(bot, source, target, attack.Row));
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11533101);
        await Cast(15320); await EmblemMarksAsync(source, 1003, 0);
        Assert.Equal(normal, await CardOffenceHitAsync(bot, source, target, attack.Row));
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11533101);
        Assert.DoesNotContain(source.Progress!.Skills.Keys, g => g == 115328);
    }
}
