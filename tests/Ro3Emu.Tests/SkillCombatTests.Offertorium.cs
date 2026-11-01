using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 220)]
    [InlineData(2, 330)]
    [InlineData(3, 440)]
    [InlineData(4, 550)]
    public async Task HealingOffertoriumAllLevelsHealTenTimesAndCompleteOnce(int level, long expected)
    {
        await using var bot = await InWorld("OfferingHealer");
        await using var mateBot = await InWorld("OfferingLastTick");
        IsolateDirectHealing();
        await Gm("offeringhealer", "!job 4400");
        await Teach("offeringhealer", 14315, level);
        await HealingStat(bot, 104, 1000);
        await HealingStat(bot, 101, 100000);
        await HealingStat(bot, 102, 10000);
        await HealingStat(mateBot, 101, 100000);
        await JoinHealingParty(bot, mateBot);
        var source = Session("offeringhealer");
        var mate = Session("offeringlasttick");
        var combat = _app.Services.GetRequiredService<CombatService>();
        int ticks = 0, completed = 0;
        var ten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(source, 1, 10000);
            mate.Position = source.Position.Clone();
            await Character.SetHpSpAsync(mate, 1, null);
            combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) completed++; return Task.CompletedTask; });
            combat.HealingApplied.Add((s, target, row, actual) =>
            {
                if (s == source && target == source)
                {
                    Assert.Equal(1431500 + level, row.GetProperty("_iID").GetInt32());
                    Assert.Equal(expected, actual);
                    if (++ticks == 10) ten.TrySetResult();
                }
                return Task.CompletedTask;
            });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1431500 + level });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(first.Dsts).Damage);
        long sp = source.CurrentSp ?? -1;
        await ten.Task.WaitAsync(TimeSpan.FromSeconds(12));
        await Task.Delay(1150);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(10, ticks);
            Assert.Equal(1, completed);
            Assert.Equal(1 + expected * 10, source.CurrentHp);
            Assert.Equal(1 + expected * 10, mate.CurrentHp);
            Assert.Equal(sp, source.CurrentSp);
            Assert.DoesNotContain(source.Buffs, b => b.ConfigId / 100 == 14325);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task HealingOffertoriumDispelDuringPublishedHealStopsTheRemainingPartyTargets()
    {
        await using var bot = await InWorld("OfferingDispel");
        await using var mateBot = await InWorld("OfferRemain");
        IsolateDirectHealing();
        await Gm("offeringdispel", "!job 4400");
        await Teach("offeringdispel", 14315);
        await HealingStat(bot, 104, 1000); await HealingStat(bot, 102, 10000); await HealingStat(bot, 101, 10000);
        await JoinHealingParty(bot, mateBot);
        var source = Session("offeringdispel"); var mate = Session("offerremain");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            mate.Position = source.Position.Clone();
            await Character.SetHpSpAsync(source, 1, 10000); await Character.SetHpSpAsync(mate, 1, null);
            combat.HealingApplied.Add(async (s, target, _, _) =>
            {
                if (s != source || target != source) return;
                await buffs.RemoveAsync(source, Assert.Single(source.Buffs, b => b.ConfigId == 1432501));
                removed.TrySetResult();
            });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1431501 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await removed.Task.WaitAsync(Wait);
        await Task.Delay(1250);
        Assert.Equal(221, source.CurrentHp); Assert.Equal(1, mate.CurrentHp);
    }

    [ClientDataFact]
    public async Task HealingOffertoriumUsesSourceMatkAndRechecksPartyAndRadiusEveryTick()
    {
        await using var leader = await InWorld("OfferingSource");
        await using var member = await InWorld("OfferingMate");
        await using var stranger = await InWorld("OfferingOther");
        IsolateDirectHealing();
        await Gm("offeringsource", "!job 4400");
        await Teach("offeringsource", 14315);
        await HealingStat(leader, 104, 1000);
        await HealingStat(leader, 102, 10000);
        await HealingStat(member, 104, 10);
        await HealingStat(member, 101, 10000);
        await HealingStat(stranger, 101, 10000);
        await JoinHealingParty(leader, member);
        var source = Session("offeringsource"); var mate = Session("offeringmate"); var other = Session("offeringother");
        await Loop.InvokeAsync(async () =>
        {
            mate.Position = source.Position.Clone(); other.Position = source.Position.Clone();
            await Character.SetHpSpAsync(source, 1, 10000);
            await Character.SetHpSpAsync(mate, 1, null); await Character.SetHpSpAsync(other, 1, null);
        });
        await leader.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1431501 });
        Assert.Equal(0, (await Next(leader, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var first = await Next(member, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1431501 && h.Dsts.Any(d => d.DstID == mate.Role!.Id));
        Assert.Equal(220, Assert.Single(first.Dsts).Damage);
        Assert.Equal(221, mate.CurrentHp); Assert.Equal(1, other.CurrentHp);
        await Loop.InvokeAsync(() => { mate.Position.X += 31; return Task.CompletedTask; });
        await Task.Delay(1250);
        Assert.Equal(221, mate.CurrentHp);
        await member.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(member, TeamService.TeamLeave, LeaveTeamResp.Parser)).Result);
        await Loop.InvokeAsync(() => { mate.Position = source.Position.Clone(); return Task.CompletedTask; });
        await Task.Delay(1250);
        Assert.Equal(221, mate.CurrentHp); Assert.Equal(1, other.CurrentHp);
    }

    [ClientDataTheory]
    [InlineData("dispel")]
    [InlineData("clear")]
    [InlineData("death")]
    [InlineData("scene")]
    [InlineData("refresh")]
    public async Task HealingOffertoriumStopsOnInvalidationAndRefreshDoesNotStack(string change)
    {
        await using var bot = await InWorld("OfferingStop");
        IsolateDirectHealing();
        await Gm("offeringstop", "!job 4400");
        await Teach("offeringstop", 14315);
        await HealingStat(bot, 104, 1000); await HealingStat(bot, 101, 100000); await HealingStat(bot, 102, 10000);
        var source = Session("offeringstop");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, 1, 10000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1431501 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1431501);
        await Loop.InvokeAsync(async () =>
        {
            switch (change)
            {
                case "dispel": await buffs.RemoveAsync(source, Assert.Single(source.Buffs, b => b.ConfigId == 1432501)); break;
                case "clear": await combat.ClearSkillsAsync(source); break;
                case "death": source.Dead = true; break;
                case "scene": source.SceneGeneration++; break;
                case "refresh":
                    await buffs.AddSupportAsync(source, 1432501, 10000, source.Role!.Id);
                    Assert.Equal(1, Assert.Single(source.Buffs, b => b.ConfigId == 1432501).PileCount);
                    break;
            }
        });
        await Task.Delay(1350);
        Assert.Equal(change == "refresh" ? 441 : 221, source.CurrentHp);
    }
}
