using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private void IsolateDirectHealing() => Character.DynamicProperties.Add(_ =>
        [(107, 0L), (108, 0L), (1071, 0L), (1081, 0L)]);

    private async Task HealingStat(Ro3Emu.Bot.BotClient bot, int attr, long value)
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
        { GmType = (GmOpEnum)33, ParamString = $"{bot.Self!.Uuid},{attr},{value}" });
        Assert.Equal(0, (await Next(bot, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(1, 50, 5550, 370)]
    [InlineData(10, 150, 10100, 1000)]
    public async Task HealingColuseoRealLevelsUseDexCapAndProvideAnExpiringShield(int level, int dex, long healing, long shield)
    {
        await using var caster = await InWorld("HealingColuseo");
        IsolateDirectHealing();
        await Gm("healingcoluseo", "!job 4300");
        await Teach("healingcoluseo", 14303, level);
        await HealingStat(caster, 5, dex);
        await HealingStat(caster, 104, 1000);
        await HealingStat(caster, 101, 100_000);
        await HealingStat(caster, 102, 10_000);
        var source = Session("healingcoluseo");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, 1, 10_000));
        Assert.Equal(dex, Character.HealingStats(source)[5]);
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430300 + level });
        var accepted = await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(caster, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(healing, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + healing, source.CurrentHp);
        Assert.Equal(shield, buffs.ShieldAmount(source));
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(0, await buffs.AbsorbAsync(source, 100));
            Assert.Equal(shield - 100, buffs.ShieldAmount(source));
            int index = source.Buffs.FindIndex(b => b.ConfigId == 1430300 + level);
            source.Buffs[index] = source.Buffs[index] with { ExpireMs = MonsterService.NowMs - 1 };
            Assert.Equal(0, buffs.ShieldAmount(source));
            Assert.Equal(40, await buffs.AbsorbAsync(source, 40));
            Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1430300 + level);
        });
    }

    [ClientDataTheory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public async Task HealingRealLevelsApplyOrdinaryReceivedBuffAndBaseLevelToActualHp(int healLevel, int buffLevel)
    {
        await using var caster = await InWorld("HealingSource");
        await using var target = await InWorld("HealingTarget");
        IsolateDirectHealing();
        await Gm("healingsource", "!job 4100");
        await Gm("healingsource", "!lvl 50");
        await Teach("healingsource", 14007, healLevel);
        await HealingStat(caster, 104, 1000);
        await HealingStat(caster, 207, 1000);
        await HealingStat(caster, 1207, 500);
        await HealingStat(target, 101, 100_000);
        await HealingStat(target, 208, 2000);
        var source = Session("healingsource"); var recipient = Session("healingtarget");
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(recipient, 1, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 1420100 + buffLevel, 120000, source.Role!.Id);
        });
        Assert.Equal(1000, Character.HealingStats(source)[104]);
        Assert.Equal(buffLevel * 100, Character.HealingStats(recipient)[1208]);
        long baseAmount = healLevel == 1 ? 1035 : 1180;
        long levelAmount = (long)Math.Floor(baseAmount * 1.05);
        long expected = (long)Math.Floor(levelAmount * 1.15 * (1.2 + buffLevel / 100d));
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1400700 + healLevel, TargetId = recipient.Role!.Id });
        var accepted = await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(caster, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + expected, recipient.CurrentHp);
    }

    [ClientDataFact]
    public async Task HealingExpiredOrdinaryBuffAndTotemPercentAreNotAppliedTwice()
    {
        await using var caster = await InWorld("HealingTotem");
        await using var target = await InWorld("HealingExpiry");
        IsolateDirectHealing();
        await Gm("healingtotem", "!job 4100");
        await Teach("healingtotem", 14007);
        await HealingStat(caster, 104, 1000);
        await HealingStat(target, 101, 100_000);
        var source = Session("healingtotem"); var recipient = Session("healingexpiry");
        var effects = _app.Services.GetRequiredService<TotemEffects>();
        await Loop.InvokeAsync(async () =>
        {
            var item = new InvItem
            {
                Guid = 990991, Entry = 17300001, Stack = 1, Pack = 4,
                Attrs = new Dictionary<int, long> { [3] = 1, [240] = 10053012 },
            };
            source.Items.Add(item);
            source.Totems!.TotemPlans[0].SetUpTotem[1] = item.Guid;
            await effects.RefreshAsync(source);
            await Character.SetHpSpAsync(recipient, 1, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 1420110, 120000, source.Role!.Id);
            int index = recipient.Buffs.FindIndex(b => b.ConfigId == 1420110);
            recipient.Buffs[index] = recipient.Buffs[index] with { ExpireMs = MonsterService.NowMs - 1 };
            Assert.Equal(0, Character.HealingStats(recipient)[1208]);
        });
        Assert.Equal(1.01, effects.HealingMultiplier(source), 3);
        Assert.Equal(100, Character.HealingStats(source)[207]);
        long levelAmount = (long)Math.Floor(1035 * (1 + source.Progress!.Level / 1000d));
        long expected = (long)Math.Floor(levelAmount * 1.01);
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = recipient.Role!.Id });
        var accepted = await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(caster, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + expected, recipient.CurrentHp);
    }

    [ClientDataFact]
    public async Task HealingPeriodicUsesTheAppliersOutputAndTargetsInputThenClampsHp()
    {
        await using var caster = await InWorld("HealTickSrc");
        await using var target = await InWorld("HealTickTarget");
        IsolateDirectHealing();
        await HealingStat(caster, 207, 5000);
        await HealingStat(target, 207, 9000); // 目標自己的輸出不能混成來源輸出。
        await HealingStat(target, 208, 2000);
        var source = Session("healticksrc"); var recipient = Session("healticktarget");
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(recipient, 1, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 980101, 1100, source.Role!.Id);
        });
        var heal = await Next(target, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == source.Role!.Id && h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal(54, Assert.Single(heal.Dsts).Damage); // 30 × 1.5 × 1.2
        Assert.Equal(55, recipient.CurrentHp);
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(recipient, Character.Stats(recipient)[101] - 5, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 980101, 1100, source.Role!.Id);
        });
        var capped = await Next(target, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId != heal.SessionId && h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal(5, Assert.Single(capped.Dsts).Damage);
        Assert.Equal(Character.Stats(recipient)[101], recipient.CurrentHp);
    }
}
