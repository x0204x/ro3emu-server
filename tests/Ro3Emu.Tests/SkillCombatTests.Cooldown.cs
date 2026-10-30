using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedSkillDuringCooldownDoesNotCastOrSpendAgainAndWorksAfterExpiry(bool automatic)
    {
        const int fireBall = 1201201, group = 12012;
        var skill = Assert.IsType<SkillConfig>(SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), fireBall));
        Assert.True(skill.Cd > skill.ChantVar + skill.ActionTime);
        var cost = Assert.Single(skill.Cost);
        Assert.Equal(new long[] { 1, AttributeId.Sp, 0 }, cost.Take(3));
        long spCost = cost[3];
        Assert.True(spCost > 0);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 20"); await Gm("alice", "!job 2100"); await Teach("alice", group);
        var target = await Target("alice", stationary: true, immobile: true);
        var player = Session("alice");
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 1_000_000_000;
            // 本案例驗證技能扣費；自然回 SP 每五秒依角色相位執行，可能恰在詠唱期間插入。
            // 此案例只驗證冷卻扣費，將這個角色的兩項回魔來源明確歸零。
            // 戰鬥中仍會自然回 SP，不能再以戰鬥旗標假設背景回復已停用。
            Character.DynamicProperties.Add(session => ReferenceEquals(session, player)
                ? [(AttributeId.ReSp, 0L), (AttributeId.PerReSp, 0L)] : []);
            return Task.CompletedTask;
        });
        long beforeSp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(player)[AttributeId.Sp]));
        var request = new CastSkillReq { SkillId = fireBall, TargetId = target.Uid, IsAutoCast = automatic };

        // Send both on the wire before waiting for the first response: the repeated request arrives during its chant.
        await alice.SendGameAsync(CombatService.UseSkill, request);
        await alice.SendGameAsync(CombatService.UseSkill, request);
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var refused = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(ErrorCodes.SkillCooldown, refused.Result); // cooldown keeps precedence over SkillBusy
        Assert.Equal(automatic, refused.IsAutoCast);
        Assert.Equal(fireBall, Assert.Single(refused.Params));
        Assert.Equal(0, refused.SessionId);
        var cooldown = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser,
            n => n.Infos.Any(c => c.SkillId == group));
        long readyAt = Assert.Single(cooldown.Infos, c => c.SkillId == group).CdEndTimestampMs;
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillEnd)
            && SkillEndNotify.Parser.ParseFrom(e.Body).Result == ErrorCodes.SkillCooldown);
        Assert.Equal(beforeSp - spCost, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(player)[AttributeId.Sp])));
        var firstHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        Assert.Equal(fireBall, firstHit.SkillId); // refusal did not cancel the already accepted cast
        var idleDeadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(player.SkillCast != null)))
        {
            Assert.True(DateTime.UtcNow < idleDeadline, "The accepted cast did not finish.");
            await Task.Delay(20);
        }
        long afterHit = target.Hp;

        // With no cast active, 31103 may show G's existing cooldown tip; its parameter must identify the skill row.
        await alice.SendGameAsync(CombatService.UseSkill, request);
        var idleRefusal = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(ErrorCodes.SkillCooldown, idleRefusal.Result);
        Assert.Equal(fireBall, Assert.Single(idleRefusal.Params));
        var tip = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, n => n.Result == ErrorCodes.SkillCooldown);
        Assert.Equal(fireBall, Assert.Single(tip.Params));
        Assert.Equal(afterHit, target.Hp);
        Assert.Equal(beforeSp - spCost, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(player)[AttributeId.Sp])));
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body) is var cast && cast.SrcID == player.Role!.Id && cast.SkillID == fireBall);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is var hit && hit.SrcID == player.Role!.Id && hit.SkillId == fireBall);

        long remaining = readyAt - MonsterService.NowMs;
        if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining + 30));
        await alice.SendGameAsync(CombatService.UseSkill, request);
        var next = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, next.Result);
        Assert.NotEqual(accepted.SessionId, next.SessionId);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == next.SessionId && h.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        Assert.Equal(beforeSp - 2 * spCost, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(player)[AttributeId.Sp])));
        Assert.True(target.Hp < afterHit);
    }

    [ClientDataTheory]
    [InlineData(1230201, 12302, 2400)] // Soul Expansion: CD 500 ms, chant/action longer, fixed SP cost
    [InlineData(1244201, 12442, 2100)] // Fire Burst: CD 0, action 1000 ms
    public async Task SameSkillWithShortOrZeroCooldownCannotCancelItsAcceptedCast(int rowId, int group, int job)
    {
        var skill = Assert.IsType<SkillConfig>(SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), rowId));
        Assert.True(skill.CancelCast);
        Assert.Equal(0, skill.IsNormalAtk);
        Assert.True(skill.Cd < skill.ChantVar + skill.ActionTime);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 20"); await Gm("alice", $"!job {job}"); await Teach("alice", group);
        var target = await Target("alice", stationary: true, immobile: true);
        var player = Session("alice");
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });
        var request = new CastSkillReq { SkillId = rowId, TargetId = target.Uid, TargetPos = target.Pos.Clone() };
        await alice.SendGameAsync(CombatService.UseSkill, request);
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var original = await Loop.InvokeAsync(() => Task.FromResult(player.SkillCast));
        Assert.NotNull(original);
        Assert.Equal(accepted.SessionId, original.Id);
        long paidSp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(player)[AttributeId.Sp]));
        if (skill.Cd > 0)
        {
            var cooldown = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser,
                n => n.Infos.Any(c => c.SkillId == group));
            long remaining = Assert.Single(cooldown.Infos, c => c.SkillId == group).CdEndTimestampMs - MonsterService.NowMs;
            if (remaining >= 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining + 30));
        }
        Assert.True(MonsterService.NowMs < original.StartMs + original.ChantMs + original.ActionMs,
            "The original cast ended before the repeated request.");
        await alice.SendGameAsync(CombatService.UseSkill, request);
        var refused = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(ErrorCodes.SkillBusy, refused.Result);
        Assert.Equal(0, refused.SessionId);
        await Loop.InvokeAsync(() =>
        {
            Assert.Same(original, player.SkillCast);
            Assert.False(original.Cancelled);
            Assert.Equal(paidSp, Character.Stats(player)[AttributeId.Sp]);
            return Task.CompletedTask;
        });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillEnd)
            && SkillEndNotify.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body) is var cast && cast.SrcID == player.Role!.Id && cast.SkillID == rowId);
    }
}
