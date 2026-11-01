using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    public async Task HealingModeOneUsesItsRealFormulaAndPoemShield(int level, bool poem)
    {
        await using var bot = await InWorld("HealVariant");
        IsolateDirectHealing();
        await Gm("healvariant", poem ? "!job 4400" : "!job 4100");
        await Gm("healvariant", "!lvl 50");
        await Teach("healvariant", poem ? 14301 : 14007, level);
        await HealingStat(bot, 104, 1000);
        await HealingStat(bot, 101, 100000);
        await HealingStat(bot, 102, 10000);
        var source = Session("healvariant");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(source, poem ? 11430601 : 1423801, 60000, source.Role!.Id);
            await Character.SetHpSpAsync(source, 1, 10000);
        });
        int requested = (poem ? 1430100 : 1400700) + level;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = requested, TargetId = source.Role!.Id });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(requested + 10, hit.SkillId);
        long expected = poem ? (level == 1 ? 1060 : 1600) : (level == 1 ? 1086 : 1239);
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + expected, source.CurrentHp);
        if (poem)
        {
            Assert.Equal(400, buffs.ShieldAmount(source));
            Assert.Equal(3000, Assert.Single(source.Buffs, b => b.ConfigId == 11430700 + level).DurationMs);
        }
    }

    [ClientDataTheory]
    [InlineData(1, 90)]
    [InlineData(1, 91)]
    [InlineData(4, 90)]
    [InlineData(4, 91)]
    public async Task HealingResurrectionModeOneReachesTheRealInvitationAndRestoresHpSp(int level, int targetLevel)
    {
        await using var bot = await InWorld("ReviveVariant");
        await using var target = await InWorld("ReviveTarget");
        IsolateDirectHealing();
        await Gm("revivevariant", "!job 4200");
        await Gm("revivetarget", $"!lvl {targetLevel}");
        await Teach("revivevariant", 14112, level);
        await HealingStat(bot, 102, 10000);
        await HealingStat(target, 101, 10000);
        await HealingStat(target, 102, 1000);
        var source = Session("revivevariant"); var recipient = Session("revivetarget");
        await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<BuffService>().AddAsync(source, 1422001, 60000, source.Role!.Id);
            await Character.SetHpSpAsync(source, null, 10000);
            recipient.Dead = true;
            await Character.SetHpSpAsync(recipient, 0, 0);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411200 + level, TargetId = recipient.Role!.Id });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var invitation = await Next(target, WorldCoreService.ReliveInvitation, EntityReliveReceiveInviteNotify.Parser);
        Assert.Equal(source.Role!.Id, invitation.SourceEntityID);
        await target.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq { SourceEntityID = source.Role.Id, IsAccept = true });
        Assert.Equal(0, (await Next(target, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        await Next(target, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, n => !n.BDead);
        Assert.False(recipient.Dead);
        int tenths = level + (targetLevel <= 90 ? 3 : 2);
        Assert.Equal(tenths * 1000, recipient.CurrentHp);
        Assert.Equal(tenths * 100, recipient.CurrentSp);
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    public async Task HealingSanctuaryUsesSixBoundedTicksWithoutRepeatingCostAndStopsOnClear(int level, bool clear)
    {
        await using var bot = await InWorld("SanctuaryHeal");
        IsolateDirectHealing();
        await Gm("sanctuaryheal", "!job 4200");
        await Teach("sanctuaryheal", 14115, level);
        await HealingStat(bot, 104, 1000);
        await HealingStat(bot, 101, 100000);
        await HealingStat(bot, 102, 10000);
        var source = Session("sanctuaryheal");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var ordinary = await Target("sanctuaryheal", stationary: true, immobile: true);
        var demon = await Target("sanctuaryheal", stationary: true, immobile: true);
        var undead = await Target("sanctuaryheal", stationary: true, immobile: true);
        int completed = 0, healed = 0;
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(source, 1, 10000);
            ordinary.Template = ordinary.Template with { Element = 1, Race = 1 };
            demon.Template = demon.Template with { Element = 1, Race = 3 };
            undead.Template = undead.Template with { Element = 10, Race = 1 };
            ordinary.Hp = demon.Hp = undead.Hp = 1000000;
            // 隔離治療與種族判定，不讓測試靶反擊改變角色的實際 HP。
            ordinary.StaysOutOfBattle = demon.StaysOutOfBattle = undead.StaysOutOfBattle = true;
            combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) completed++; return Task.CompletedTask; });
            combat.HealingApplied.Add((s, _, _, amount) => { if (s == source && amount > 0) healed++; return Task.CompletedTask; });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411500 + level, TargetPos = source.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId && n.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal(level == 1 ? 105 : 200, Assert.Single(first.Dsts, d => d.DstID == source.Role!.Id && d.DamageType == DamageType.Heal).Damage);
        Assert.DoesNotContain(first.Dsts, d => d.DstID == ordinary.Uid);
        Assert.Contains(first.Dsts, d => d.DstID == demon.Uid && d.Damage > 0);
        Assert.Contains(first.Dsts, d => d.DstID == undead.Uid && d.Damage > 0);
        long spAfterCast = source.CurrentSp ?? -1;
        if (clear) await Loop.InvokeAsync(() => combat.ClearSkillsAsync(source));
        await Task.Delay(4100);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(clear ? 1 : 6, healed);
            Assert.Equal(1, completed);
            Assert.Equal(1 + (level == 1 ? 105 : 200) * (clear ? 1 : 6), source.CurrentHp);
            Assert.Equal(spAfterCast, source.CurrentSp);
            Assert.Equal(1000000, ordinary.Hp);
            Assert.True(demon.Hp < 1000000);
            Assert.True(undead.Hp < 1000000);
            return Task.CompletedTask;
        });
    }
}
