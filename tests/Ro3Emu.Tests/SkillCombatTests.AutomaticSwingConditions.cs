using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticSwingRechecksMountedConditionAndCannotResumeTheOldLoop(bool pvp)
    {
        await using var alice = await InWorld("SwingGateA");
        await using var bob = await InWorld("SwingGateB");
        await Gm("swinggatea", "!lvl 99"); await Gm("swinggatea", "!job 3400");
        await Teach("swinggatea", 13310); await Teach("swinggatea", 13312);
        var source = Session("swinggatea"); var other = Session("swinggateb");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        await CardOffenceRoom((alice, source), (bob, other));
        await LeaseCompanion(alice, 1500400);
        await alice.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 });
        Assert.Equal(0, (await Next(alice, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var monster = await Target("swinggatea", stationary: true, immobile: true);
        await CardOffenceDummyAsync(monster);
        var sourceRole = source.Role ?? throw new InvalidOperationException("續擊來源未建角。");
        long targetId = pvp ? (other.Role ?? throw new InvalidOperationException("PvP 目標未建角。")).Id : monster.Uid;
        int completed = 0;
        var disabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>();
            foreach (var ambient in world.Map(source.WorldScene).Units.OfType<MonsterUnit>().Where(m => m != monster).ToArray())
                await world.RemoveUnitAsync(ambient);
            source.Items.RemoveAll(i => i.Pack == 2 && i.Grid is 1 or 2);
            other.Position = source.Position.Clone();
            combat.Rolls = new CardOffenceRolls();
            combat.SetEventPvPHandlers((a, b) => pvp && a == source && b == other, (_, _) => Task.CompletedTask);
            // 原1333101授予10032 mode1；其正常取得owner尚未還原，這裡只安裝真列隔離續擊條件。
            await _app.Services.GetRequiredService<BuffService>().AddAsync(source, 1333101, 60000, sourceRole.Id);
            Assert.Equal(1003201, skills.NormalAttack(source));
            Assert.Equal(1003211, GameData.Int(combat.EffectiveSkillRow(source, 1003201)
                ?? throw new InvalidOperationException("強化狼普攻模式未生效。"), "_iID"));
            combat.SkillCastCompleted.Add((actor, cast, _) =>
            {
                if (actor != source || cast.Request.SkillId != 1003211) return Task.CompletedTask;
                completed++;
                if (completed == 2)
                {
                    // 在第二次實際續擊完成後，精確改變同一角色的現況；不聲稱測了下騎網路流程。
                    var mounts = source.Mounts ?? throw new InvalidOperationException("騎乘資料未初始化。");
                    mounts.MountIds[2].IsRiding = false;
                    Assert.Equal(1, skills.Level(source.Progress ?? throw new InvalidOperationException("角色進度未初始化。"), 10032));
                    Assert.False(skills.IsCareerNormalAttack(3400, 1003211));
                    Assert.True(combat.SkillModeAllowed(source, combat.EffectiveSkillRow(source, 1003201)
                        ?? throw new InvalidOperationException("下騎不應撤除測試用模式來源。")));
                    disabled.TrySetResult();
                }
                return Task.CompletedTask;
            });
        });
        long InitialHp() => pvp ? other.CurrentHp ?? throw new InvalidOperationException("PvP HP未初始化。") : monster.Hp;
        long initialHp = InitialHp();
        long? initialSp = source.CurrentSp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1003201, TargetId = targetId });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SessionId == accepted.SessionId);
        Assert.Equal(1003211, first.SkillID); Assert.True(first.SkillMs > 0);
        var second = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SrcID == sourceRole.Id && u.SessionId != accepted.SessionId && u.SkillID == 1003211);
        Assert.Equal(targetId, second.TargetId);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == second.SessionId);
        await disabled.Task.WaitAsync(Wait);
        // 已收到兩次實際命中後，跨過三個原施放動作週期；不是僅觀察首次施放被拒。
        int observationMs = checked((int)Math.Max(1500, first.SkillMs * 3 + 500));
        await Task.Delay(observationMs);
        long stoppedHp = 0;
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(2, completed); Assert.True(InitialHp() < initialHp);
            stoppedHp = InitialHp(); Assert.Equal(initialSp, source.CurrentSp);
            if (!pvp) Assert.Null(source.Attack);
            return Task.CompletedTask;
        });
        AssertAutomaticSwingPackets(alice, sourceRole.Id, targetId, initialHp - stoppedHp, 2);
        // 條件恢復也不能喚回舊目標迴圈；新的手動意圖才可以重新開始。
        await Loop.InvokeAsync(() =>
        {
            (source.Mounts ?? throw new InvalidOperationException("騎乘資料未初始化。")).MountIds[2].IsRiding = true;
            return Task.CompletedTask;
        });
        await Task.Delay(observationMs);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(2, completed); Assert.Equal(stoppedHp, InitialHp());
            if (!pvp) Assert.Null(source.Attack);
            return Task.CompletedTask;
        });
        AssertAutomaticSwingPackets(alice, sourceRole.Id, targetId, initialHp - stoppedHp, 2);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1003201, TargetId = targetId });
        var restarted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, restarted.Result);
        Assert.NotEqual(accepted.SessionId, restarted.SessionId);
        var nextHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == restarted.SessionId);
        Assert.True(Assert.Single(nextHit.Dsts, d => d.DstID == targetId).Damage > 0);
        await Loop.InvokeAsync(() => combat.InterruptAsync(source));
    }

    private static void AssertAutomaticSwingPackets(BotClient bot, long sourceId, long targetId, long damage, int count)
    {
        var casts = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseInSkill))
            .Select(e => UseInSkillInfo.Parser.ParseFrom(e.Body)).Where(u => u.SrcID == sourceId && u.SkillID == 1003211).ToArray();
        var hits = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Where(h => h.SrcID == sourceId && h.SkillId == 1003211).ToArray();
        Assert.Equal(count, casts.Length); Assert.Equal(count, hits.Length);
        Assert.Equal(count, hits.Select(h => h.SessionId).Distinct().Count());
        Assert.Equal(damage, hits.Sum(h => Assert.Single(h.Dsts, d => d.DstID == targetId).Damage));
    }
}
