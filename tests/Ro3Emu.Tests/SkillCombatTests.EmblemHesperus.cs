using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // 同一真施放串完整核對共鳴門檻、傷害、命中盾、4秒變體與到期；不替未知的一次性消耗造規則。
    [ClientDataTheory]
    [InlineData(1, 1)]
    [InlineData(10, 5)]
    public async Task EmblemHesperusExactStageAddsDamageAndFourSecondOverbrandWindow(int level, int overbrandLevel)
    {
        await using var bot = await InWorld("EchoHesperus");
        IsolateDirectHealing();
        await Gm("echohesperus", "!job 1410");
        await Teach("echohesperus", 11607, level); await Teach("echohesperus", 11604, overbrandLevel);
        await EquipConditionFixtureAsync("echohesperus", 2050120);
        await HealingStat(bot, 101, 10000); await HealingStat(bot, 102, 10000);
        var source = Session("echohesperus");
        var target = Assert.Single(await AttackShieldTargets("echohesperus", 1));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var emblems = _app.Services.GetRequiredService<EmblemService>();
        var data = _app.Services.GetRequiredService<GameData>();
        SkillConfig Row(int group, int skillLevel) => new(combat.EffectiveSkillRow(source, group * 100 + skillLevel)
            ?? throw new InvalidOperationException("Effective Hesperus/OverBrand row required."));

        async Task Cast(int group, int skillLevel)
        {
            await Loop.InvokeAsync(async () =>
            {
                await combat.InterruptAsync(source);
                source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
                await Character.SetHpSpAsync(source, null, 10000);
            });
            var row = Row(group, skillLevel); var stats = Character.Stats(source);
            double speedBonus = group == 11607 ? 50 : 30;
            long expected = (long)Math.Round(stats[103] * row.DamageParam1[0] / 10000d
                * (1 + Math.Min(stats[115], 500) * speedBonus / 10000d));
            long hp = target.Hp; int baseLevel = source.Progress!.Level;
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = group * 100 + skillLevel, TargetPos = source.Position.Clone(), Face = source.Face });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            var dst = Assert.Single(hit.Dsts); Assert.Equal(target.Uid, dst.DstID);
            Assert.Equal(expected, dst.Damage); Assert.Equal(hp - expected, target.Hp);
            Assert.Equal(group == 11607 ? 3 : row.Mode == 4 ? 2 : 1, hit.Split);
            Assert.Equal(baseLevel, source.Progress.Level);
            Assert.Equal(group == 11607 ? 9950 : 10000, source.CurrentSp);
        }

        await EmblemMarksAsync(source, 1001, 9);
        Assert.Equal(0, Row(11607, level).Mode); Assert.Equal(0, Row(11604, overbrandLevel).Mode);
        var baseRatio = Row(11607, level).DamageParam1[0];
        Assert.Null(combat.EffectiveSkillRow(source, 1160710 + level));
        await Cast(11607, level);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1160101);
        await Loop.InvokeAsync(async () =>
        {
            foreach (var shield in source.Buffs.Where(b => BuffConfig.Get(data, b.ConfigId) is { BuffId: 11607 }).ToArray())
                await buffs.RemoveAsync(source, shield);
        });
        using var modeWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.TryGetValue(11607, out var g)
            && g.AffectParam.TryGetValue(15, out var m) && m.AffectValue == 1);
        await EmblemMarksAsync(source, 1001, 10);
        await modeWire.WaitAsync(Wait);
        Assert.Equal(1, Row(11607, level).Mode); Assert.Equal(baseRatio + 6000, Row(11607, level).DamageParam1[0]);
        Assert.DoesNotContain(111642, source.Progress!.Skills.Keys);
        using var overbrandWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.TryGetValue(11604, out var g)
            && g.AffectParam.TryGetValue(15, out var m) && m.AffectValue == 4);
        await Cast(11607, level);
        await overbrandWire.WaitAsync(Wait);
        var boost = Assert.Single(source.Buffs, b => b.ConfigId == 1160101);
        Assert.Equal(4000, boost.DurationMs);
        var shieldAfter = Assert.Single(source.Buffs, b => b.ConfigId == 1160701);
        Assert.Equal(1, shieldAfter.PileCount); Assert.Equal(300, shieldAfter.ShieldRemaining);
        Assert.Equal(4, Row(11604, overbrandLevel).Mode);
        Assert.Equal(overbrandLevel == 1 ? 5250 : 8250, Row(11604, overbrandLevel).DamageParam1[0]);
        Assert.Equal(650, Row(11604, overbrandLevel).Range[0]);
        using var expiryWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body) is var n && n.IsAll && !n.AffectParamDict.ContainsKey(11604));
        await Cast(11604, overbrandLevel);
        await expiryWire.WaitAsync(Wait);
        Assert.True(MonsterService.NowMs >= boost.ExpireMs);
        Assert.Equal(0, Row(11604, overbrandLevel).Mode);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1160101 && b.ExpireMs > MonsterService.NowMs);
        await EmblemOwnedAsync(source, async () =>
        { source.Emblems.CurrentPlan = 1; await emblems.SyncGenrePlanAsync(source, source.EmblemPower); });
        Assert.Equal(0, Row(11607, level).Mode);
        await EmblemOwnedAsync(source, async () =>
        { source.Emblems.CurrentPlan = 0; await emblems.SyncGenrePlanAsync(source, source.EmblemPower); });
        Assert.Equal(1, Row(11607, level).Mode);
        await Gm("echohesperus", "!job 1000"); Assert.Equal(0, Row(11607, level).Mode);
        await Gm("echohesperus", "!job 1410"); Assert.Equal(1, Row(11607, level).Mode);
        await EmblemMarksAsync(source, 1001, 0); Assert.Equal(0, Row(11607, level).Mode);
    }
}
