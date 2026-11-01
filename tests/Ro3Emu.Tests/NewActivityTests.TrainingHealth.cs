using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    /// <summary>保留原始最大HP；自造傷害只控制測試強度，仍走施放、結算與31048同步。</summary>
    [ClientDataFact]
    public async Task TrainingDummyShowsDamageSurvivesLethalHitsAndResetsWithoutKillRewards()
    {
        await using var bot = await InWorld("TrainingHp");
        SureHitRolls.Install(_app);
        await PrepareTrainingQuest(bot);
        await StartTrainingQuestRun(bot);
        var session = Session(bot);
        var dummy = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
            .OfType<MonsterUnit>().Single(m => m.InstanceId == session.InstanceId)));
        long configuredMaxHp = Assert.IsType<MonsterTemplate>(Service<MonsterService>().Template(3500001)).MaxHp;
        Assert.Equal(configuredMaxHp, dummy.Template.MaxHp);
        long fixedHit = 6_830;
        int deathBarriers = 0, kills = 0;
        var combat = Service<CombatService>();
        await OnLoop(() =>
        {
            combat.DamageAdjusters.Add((_, target, _, _, damage) => target == dummy ? fixedHit : damage);
            combat.BeforeKilled.Add((_, target) => { if (target == dummy) deathBarriers++; return Task.CompletedTask; });
            combat.Killed.Add((_, target) => { if (target == dummy) kills++; return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = dummy.Pos, BStop = true, SpeedRate = 1 });
        await TrainingHealthHitAsync(bot, dummy);
        await Next(bot, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            n => n.EntityUuid == (ulong)dummy.Uid && n.Property.Any(p => p.Idx == 2000 && p.Data == dummy.Template.MaxHp - 6_830));
        Assert.Equal(dummy.Template.MaxHp - 6_830, dummy.Hp);
        await bot.SendGameAsync(TrainingService.ChallengeReconnect, new NullArg());
        Assert.Equal(6_830, (await Next(bot, TrainingService.ChallengeReconnect, TrainingChallengeReconnectRes.Parser)).TotalDamage);

        fixedHit = dummy.Template.MaxHp;
        await Task.Delay(1200);
        await TrainingHealthHitAsync(bot, dummy);
        await Next(bot, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            n => n.EntityUuid == (ulong)dummy.Uid && n.Property.Any(p => p.Idx == 2000 && p.Data == dummy.Template.MaxHp));
        Assert.Equal(dummy.Template.MaxHp, dummy.Hp);
        Assert.False(dummy.Dead);
        Assert.Equal(0, deathBarriers);
        Assert.Equal(0, kills);
        await bot.SendGameAsync(TrainingService.ChallengeReconnect, new NullArg());
        Assert.Equal(6_830 + fixedHit,
            (await Next(bot, TrainingService.ChallengeReconnect, TrainingChallengeReconnectRes.Parser)).TotalDamage);

        await bot.SendGameAsync(TrainingService.ChallengeReset, new NullArg());
        Assert.Equal(0, (await Next(bot, TrainingService.ChallengeReset, TrainingChallengeResetRes.Parser)).Result);
        await bot.SendGameAsync(TrainingService.ChallengeReconnect, new NullArg());
        Assert.Equal(0, (await Next(bot, TrainingService.ChallengeReconnect, TrainingChallengeReconnectRes.Parser)).TotalDamage);
        var replacement = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
            .OfType<MonsterUnit>().Single(m => m.InstanceId == session.InstanceId)));
        Assert.NotEqual(dummy.Uid, replacement.Uid);
        Assert.Equal(configuredMaxHp, replacement.Hp);
    }

    private async Task TrainingHealthHitAsync(BotClient bot, MonsterUnit dummy)
    {
        int normal = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<SkillService>().NormalAttack(Session(bot))));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = dummy.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        await bot.SendGameAsync(CombatService.SkillInterrupt, new NullArg());
        await bot.HeartbeatAsync();
    }
}
