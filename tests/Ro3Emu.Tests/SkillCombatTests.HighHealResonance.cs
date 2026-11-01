using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<SkillHitInfo> CastHighHeal(Ro3Emu.Bot.BotClient bot, GameSession source, long target, int level)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Completed(GameSession s, ActiveSkillCast cast, System.Text.Json.JsonElement row)
        {
            if (s == source && new SkillConfig(row).SkillId == 14301) complete.TrySetResult();
            return Task.CompletedTask;
        }
        await Loop.InvokeAsync(async () =>
        {
            await combat.ClearSkillsAsync(source);
            source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
            combat.SkillCastCompleted.Add(Completed);
        });
        try
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430100 + level, TargetId = target });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            await complete.Task.WaitAsync(Wait);
            return hit;
        }
        finally { await Loop.InvokeAsync(() => { combat.SkillCastCompleted.Remove(Completed); return Task.CompletedTask; }); }
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    public async Task HighHealResonanceBothRecipientsHealOnceAndPoemShieldHasItsOwnCooldown(int level, bool poem)
    {
        await using var bot = await InWorld("PoemSource");
        await using var mateBot = await InWorld("PoemTarget");
        IsolateDirectHealing();
        await Gm("poemsource", "!job 4400"); await Teach("poemsource", 14301, level);
        await HealingStat(bot, 101, 100000); await HealingStat(bot, 102, 10000); await HealingStat(bot, 104, 1000);
        await HealingStat(mateBot, 101, 100000);
        var source = Session("poemsource"); var mate = Session("poemtarget");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        await EmblemMarksAsync(source, 1002, poem ? 6 : 5);
        Assert.Equal(poem ? 1 : 0, new SkillConfig(combat.EffectiveSkillRow(source, 1430100 + level)!.Value).Mode);
        await Loop.InvokeAsync(async () =>
        {
            mate.Position = source.Position.Clone();
            await Character.SetHpSpAsync(source, 1, 10000); await Character.SetHpSpAsync(mate, 1, null);
        });
        long matk = Character.HealingStats(source)[104];
        long amount = (long)Math.Round(matk * (9000 + level * 500) / 10000d + 100 + level * 10);
        long shield = (long)Math.Floor(matk * .4);
        var hit = await CastHighHeal(bot, source, mate.Role!.Id, level);
        Assert.Equal(2, hit.Dsts.Count);
        Assert.All(hit.Dsts, d => Assert.Equal(amount, d.Damage));
        Assert.Equal(1 + amount, source.CurrentHp); Assert.Equal(1 + amount, mate.CurrentHp);
        Assert.Equal(poem ? shield : 0, buffs.ShieldAmount(source)); Assert.Equal(poem ? shield : 0, buffs.ShieldAmount(mate));
        if (poem)
        {
            await Loop.InvokeAsync(async () =>
            {
                Assert.Equal(0, await buffs.AbsorbAsync(source, shield));
                Assert.Equal(0, await buffs.AbsorbAsync(mate, shield));
            });
        }
        var self = await CastHighHeal(bot, source, source.Role!.Id, level);
        Assert.Single(self.Dsts); Assert.Equal(amount, self.Dsts[0].Damage);
        Assert.Equal(1 + 2 * amount, source.CurrentHp);
        Assert.Equal(0, buffs.ShieldAmount(source)); // 第二次施放仍恢復HP，但5秒內不能補盾。
        if (!poem) return;
        await Task.Delay(5100);
        await CastHighHeal(bot, source, mate.Role.Id, level);
        Assert.Equal(shield, buffs.ShieldAmount(source)); Assert.Equal(shield, buffs.ShieldAmount(mate));
        await EmblemMarksAsync(source, 1002, 5);
        Assert.Equal(0, new SkillConfig(combat.EffectiveSkillRow(source, 1430100 + level)!.Value).Mode);
        await Task.Delay(3100);
        Assert.Equal(0, buffs.ShieldAmount(source)); Assert.Equal(0, buffs.ShieldAmount(mate));
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task HighHealResonanceRhythmCountsOneCastAndAtFiveReallyHealsNearbyAllies(int level)
    {
        await using var bot = await InWorld("RhythmSource");
        await using var mateBot = await InWorld("RhythmTarget");
        await using var outsideBot = await InWorld("RhythmOutside");
        IsolateDirectHealing();
        await Gm("rhythmsource", "!job 4400"); await Teach("rhythmsource", 14301, level);
        await HealingStat(bot, 101, 100000); await HealingStat(bot, 102, 10000); await HealingStat(bot, 104, 1000);
        await HealingStat(mateBot, 101, 100000); await HealingStat(outsideBot, 101, 100000);
        var source = Session("rhythmsource"); var mate = Session("rhythmtarget"); var outside = Session("rhythmoutside");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await EmblemMarksAsync(source, 1001, 9);
        await Loop.InvokeAsync(async () =>
        {
            mate.Position = source.Position.Clone(); outside.Position = source.Position.Clone(); outside.Position.X += 21;
            await Character.SetHpSpAsync(source, 1, 10000); await Character.SetHpSpAsync(mate, 1, null);
            await Character.SetHpSpAsync(outside, 1, null);
        });
        await CastHighHeal(bot, source, mate.Role!.Id, level);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11430501);
        await EmblemMarksAsync(source, 1001, 10);
        var echoTargets = new List<long>();
        combat.HealingApplied.Add((s, p, row, actual) =>
        {
            if (s == source && new SkillConfig(row).Id == 11430501)
            {
                Assert.Equal((long)Math.Round(Character.HealingStats(source)[104] * .6), actual);
                echoTargets.Add(p.Role!.Id);
            }
            return Task.CompletedTask;
        });
        for (int n = 1; n <= 5; n++)
        {
            await CastHighHeal(bot, source, mate.Role.Id, level);
            if (n < 5) Assert.Equal(n, Assert.Single(source.Buffs, b => b.ConfigId == 11430501).PileCount);
        }
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11430501);
        Assert.Equal(2, echoTargets.Count); Assert.Contains(source.Role!.Id, echoTargets); Assert.Contains(mate.Role.Id, echoTargets);
        Assert.DoesNotContain(outside.Role!.Id, echoTargets); Assert.Equal(1, outside.CurrentHp);
        await CastHighHeal(bot, source, source.Role.Id, level);
        Assert.Equal(1, Assert.Single(source.Buffs, b => b.ConfigId == 11430501).PileCount);
        await Task.Delay(5100);
        await CastHighHeal(bot, source, source.Role.Id, level);
        Assert.Equal(1, Assert.Single(source.Buffs, b => b.ConfigId == 11430501).PileCount);
        await EmblemMarksAsync(source, 1001, 9);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11430501);
        await EmblemMarksAsync(source, 1001, 10); await CastHighHeal(bot, source, source.Role.Id, level);
        await Gm("rhythmsource", "!job 1000");
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11430501);
    }

    [ClientDataFact]
    public async Task HighHealResonanceInterruptedChantDoesNotHealOrBuildRhythm()
    {
        await using var bot = await InWorld("RhythmCancel");
        IsolateDirectHealing();
        await Gm("rhythmcancel", "!job 4400"); await Teach("rhythmcancel", 14301, 1);
        await HealingStat(bot, 101, 100000); await HealingStat(bot, 102, 10000);
        var source = Session("rhythmcancel");
        await EmblemMarksAsync(source, 1001, 10);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, 1, 10000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430101, TargetId = source.Role!.Id });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
        Assert.True((await Next(bot, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        await Task.Delay(1500);
        Assert.Equal(1, source.CurrentHp);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 11430501 || b.ConfigId / 100 == 114307);
    }

    [ClientDataFact]
    public async Task HighHealResonanceRechecksHostilityAfterAwaitAndClarifyNeverHealsPvpEnemies()
    {
        await using var bot = await InWorld("RhythmPvp");
        await using var enemyBot = await InWorld("RhythmEnemy");
        await CardOffenceRoom((bot, Session("rhythmpvp")), (enemyBot, Session("rhythmenemy")));
        IsolateDirectHealing();
        await Gm("rhythmpvp", "!job 4400"); await Teach("rhythmpvp", 14301, 1);
        await HealingStat(bot, 101, 100000); await HealingStat(bot, 102, 10000);
        await HealingStat(enemyBot, 101, 100000);
        var source = Session("rhythmpvp"); var enemy = Session("rhythmenemy");
        Assert.NotEqual(0, source.InstanceId); Assert.Equal(source.InstanceId, enemy.InstanceId);
        var combat = _app.Services.GetRequiredService<CombatService>();
        bool hostile = false;
        var echoTargets = new List<long>();
        await EmblemMarksAsync(source, 1001, 10);
        await Loop.InvokeAsync(async () =>
        {
            enemy.Position = source.Position.Clone();
            await Character.SetHpSpAsync(source, 1, 10000); await Character.SetHpSpAsync(enemy, 1, null);
            combat.SetEventPvPHandlers((a, b) => hostile && a == source && b == enemy, (_, _) => Task.CompletedTask);
            combat.HealingApplied.Add((s, target, row, actual) =>
            {
                if (s == source && target == source && new SkillConfig(row).SkillId == 14301) hostile = true;
                if (s == source && new SkillConfig(row).Id == 11430501) echoTargets.Add(target.Role!.Id);
                return Task.CompletedTask;
            });
        });
        // 選取時仍友方，自身療效事件後轉敵方；後續受益者必須重新驗證。
        var first = await CastHighHeal(bot, source, enemy.Role!.Id, 1);
        Assert.Single(first.Dsts); Assert.Equal(source.Role!.Id, first.Dsts[0].DstID);
        Assert.Equal(1, enemy.CurrentHp);
        for (int i = 0; i < 4; i++) await CastHighHeal(bot, source, source.Role.Id, 1);
        Assert.Equal(source.Role.Id, Assert.Single(echoTargets));
        Assert.Equal(1, enemy.CurrentHp);
        Assert.DoesNotContain(enemy.Buffs, b => b.ConfigId / 100 == 114307);
    }
}
