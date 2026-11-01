using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task ChoralResonanceRequiresTenMarksAndChangesRealHealingShieldWithoutDoubleApplying(int level)
    {
        await using var bot = await InWorld("ChoralEcho");
        IsolateDirectHealing();
        await Gm("choralecho", "!job 4400"); await Teach("choralecho", 14303, level);
        await HealingStat(bot, 101, 100000); await HealingStat(bot, 102, 10000);
        await HealingStat(bot, 104, 1000); await HealingStat(bot, 5, 0);
        foreach (int attr in new[] { 207, 208, 1207, 1208 }) await HealingStat(bot, attr, 0);
        var source = Session("choralecho");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();

        async Task Check(bool enhanced)
        {
            var row = new SkillConfig(combat.EffectiveSkillRow(source, 1430300 + level)!.Value);
            long ratio = 50000 + level * 5000 + (enhanced ? 60000 : 0);
            Assert.Equal(enhanced ? 1 : 0, row.Mode);
            Assert.Equal(enhanced ? "Choral" : "ColuseoHeal", row.ScriptLogic);
            Assert.Equal(ratio, Assert.Single(row.DamageParam1));
            Assert.Equal(enhanced ? 4000 : 2000, row.ChantVar);
            int shieldId = (enhanced ? 11430900 : 1430300) + level;
            Assert.Equal(shieldId, row.Buff1[0][0]);
            await Loop.InvokeAsync(async () =>
            {
                await combat.ClearSkillsAsync(source);
                foreach (var old in source.Buffs.Where(b => b.ConfigId / 100 is 14303 or 114309).ToArray())
                    await buffs.RemoveAsync(source, old);
                source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
                await Character.SetHpSpAsync(source, 1, 10000);
            });
            var stats = Character.HealingStats(source);
            // 取施放當下實際屬性；點亮共鳴可能同時改變一般屬性，不能假設永遠是設定前的1000。
            long expectedHeal = (long)Math.Round(stats[104] * (ratio + Math.Min(stats[5], 100) * 10) / 10000d);
            var shield = BuffConfig.Get(data, shieldId)!.Value;
            long expectedShield = (long)Math.Floor(stats[104] * shield.Data1[0] / 10000d);
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430300 + level });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            Assert.Equal(1430300 + level + (enhanced ? 10 : 0), hit.SkillId);
            Assert.Equal(expectedHeal, Assert.Single(hit.Dsts, d => d.DstID == source.Role!.Id).Damage);
            Assert.Equal(1 + expectedHeal, source.CurrentHp);
            Assert.Equal(expectedShield, Assert.Single(source.Buffs, b => b.ConfigId == shieldId).ShieldRemaining);
            Assert.Equal(5000, source.Buffs.Single(b => b.ConfigId == shieldId).DurationMs);
        }

        await EmblemMarksAsync(source, 1002, 9); await Check(false);
        await EmblemMarksAsync(source, 1002, 10); await Check(true);
        Assert.DoesNotContain(114308, source.Progress!.Skills.Keys);
        await Loop.InvokeAsync(() => buffs.AddAsync(source, 11430801, 60000, source.Role!.Id));
        await Check(true); // 共鳴與原模式Buff共存，仍為一次mode設定，不能再疊60000。
        await Loop.InvokeAsync(() => buffs.RemoveAsync(source, source.Buffs.Single(b => b.ConfigId == 11430801)));
        await Check(true);
        await EmblemOwnedAsync(source, async () =>
        {
            source.Emblems.CurrentPlan = 1;
            await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(source, source.EmblemPower);
        });
        await Check(false);
        await EmblemOwnedAsync(source, async () =>
        {
            source.Emblems.CurrentPlan = 0;
            await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(source, source.EmblemPower);
        });
        await Check(true);
        await EmblemMarksAsync(source, 1002, 0); await Check(false);
        await EmblemMarksAsync(source, 1002, 10);
        await Gm("choralecho", "!job 1000");
        Assert.Equal(0, new SkillConfig(combat.EffectiveSkillRow(source, 1430300 + level)!.Value).Mode);
        Assert.DoesNotContain(114308, source.Progress!.Skills.Keys);
    }

    [ClientDataFact]
    public void ChoralResonanceAllTenLevelsSelectCompleteModeRowsAndPreserveTheShieldConflict()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var grant = Assert.Single(SkillParameterRules.BuffModifiers(data, 11430801));
        Assert.Equal((14303, 15, 2, 1L), (grant.Group, grant.Type, grant.Operation, grant.Value));
        for (int level = 1; level <= 10; level++)
        {
            var normal = SkillConfig.Get(data, 1430300 + level)!.Value;
            var choral = SkillConfig.Get(data, 1430310 + level)!.Value;
            Assert.Equal(60000, choral.DamageParam1[0] - normal.DamageParam1[0]);
            Assert.Equal(11430900 + level, choral.Buff1[0][0]);
            Assert.Empty(choral.Buff2);
            Assert.Equal(1350 + (level - 1) * 50, BuffConfig.Get(data, 11430900 + level)!.Value.Data1[0]);
            Assert.Equal("100", choral.DescData[3]); // 原件衝突保留，不能把文字100%靜默當成Buff執行值。
        }
    }
}
