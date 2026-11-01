using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task HundredSpearsSonicBurstConvertsOnlyOnceAndFiveEnhancedCastsExhaustTheMode(int level)
    {
        await using var bot = await InWorld("SpearCharge");
        await Gm("spearcharge", "!lvl 99"); await Gm("spearcharge", "!job 1400");
        await Teach("spearcharge", 11310, level); await Teach("spearcharge", 11316, level);
        var source = Session("spearcharge");
        await EquipPixelSpearAsync(bot, source); // 百矛穿刺原條件2001/5要求目前穿戴騎士長矛。
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        combat.Rolls = new CardOffenceRolls();
        var target = await Target("spearcharge", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target);
        int completions = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            await buffs.ReconcileEnergyAsync(source);
            Assert.True(await buffs.SetExistingEnergyValueAsync(source, 140002, 99));
            Assert.Equal(5, source.Energy[140002].Value);
            Assert.Equal(0, source.Energy[140003].Value);
            // 暫停測試背景回充以隔離技能消耗；產品仍使用原表每10秒回充。
            source.Energy[140002].NextRecoverMs = MonsterService.NowMs + 60_000;
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) { completions++; completed.TrySetResult(); } return Task.CompletedTask; });
        });
        async Task Cast(int requested)
        {
            completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Loop.InvokeAsync(async () =>
            {
                await combat.InterruptAsync(source);
                await CombatService.ResetCooldownAsync(source, requested / 100);
            });
            await bot.SendGameAsync(CombatService.UseSkill,
                new CastSkillReq { SkillId = requested, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await completed.Task.WaitAsync(Wait);
        }
        await Cast(1131600 + level);
        var charged = await Next(bot, BuffService.EnergyNotify, PlayerEnergyNotify.Parser,
            n => n.EnergyConfigId == 140003 && n.EnergyValue == 5);
        Assert.Equal(5, charged.EnergyValue);
        Assert.Equal(0, source.Energy[140002].Value);
        Assert.Equal(1, new SkillConfig(combat.EffectiveSkillRow(source, 1131000 + level)!.Value).Mode);
        for (int count = 1; count <= 5; count++)
        {
            await Cast(1131000 + level);
            var consumed = await Next(bot, BuffService.EnergyNotify, PlayerEnergyNotify.Parser,
                n => n.EnergyConfigId == 140003 && n.EnergyValue == 5 - count);
            Assert.Equal(5 - count, consumed.EnergyValue);
            Assert.Equal(5 - count, source.Energy[140003].Value);
            Assert.Equal(count == 5 ? 0 : 1, new SkillConfig(combat.EffectiveSkillRow(source, 1131000 + level)!.Value).Mode);
        }
        Assert.Equal(6, completions);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1132401);
        Assert.Equal(0, source.Energy[140002].Value);
        await Loop.InvokeAsync(async () =>
        {
            await combat.InterruptAsync(source); await CombatService.ResetCooldownAsync(source, 11310);
        });
        await bot.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1131000 + level, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.NotEqual(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(6, completions);
    }

    [ClientDataFact]
    public async Task HundredSpearsInterruptedBurstDoesNotConvertOrCreateUnauthorizedMeters()
    {
        await using var bot = await InWorld("SpearInterrupt");
        await Gm("spearinterrupt", "!lvl 99"); await Gm("spearinterrupt", "!job 1400");
        await Teach("spearinterrupt", 11310); await Teach("spearinterrupt", 11316);
        var source = Session("spearinterrupt"); var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var target = await Target("spearinterrupt", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(async () =>
        {
            await buffs.ReconcileEnergyAsync(source);
            Assert.True(await buffs.SetExistingEnergyValueAsync(source, 140002, 3));
            source.Energy[140002].NextRecoverMs = MonsterService.NowMs + 60_000;
        });
        await bot.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1131601, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() => combat.InterruptAsync(source));
        await Task.Delay(1000);
        Assert.Equal(3, source.Energy[140002].Value); Assert.Equal(0, source.Energy[140003].Value);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1132401);
        await Gm("spearinterrupt", "!job 1000");
        await Loop.InvokeAsync(async () =>
        {
            await buffs.ReconcileEnergyAsync(source);
            Assert.False(await buffs.SetExistingEnergyValueAsync(source, 140003, 5));
            Assert.False(source.Energy.ContainsKey(140003));
        });
    }
}
