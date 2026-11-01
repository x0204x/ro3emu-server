using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, false, false)]
    [InlineData(5, false, false)]
    [InlineData(1, true, false)]
    [InlineData(5, true, false)]
    [InlineData(1, false, true)]
    [InlineData(5, false, true)]
    public async Task TetraStagesDealFourAuthoredElementsAndOnlyFallbackWhenEveryControlFails(int level, bool immune, bool variant)
    {
        await using var bot = await InWorld("TetraElements");
        IsolateDirectHealing();
        await Gm("tetraelements", "!job 2400"); await Teach("tetraelements", 12316, level);
        await HealingStat(bot, 102, 10000); await HealingStat(bot, 104, 1000);
        var source = Session("tetraelements"); var target = await Target("tetraelements", true, immobile: true);
        await CardOffenceDummyAsync(target);
        await IsolateAuthoredStageTargetAsync(source, target);
        int initialLevel = source.Progress!.Level;
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var inputs = new ConcurrentQueue<(long Matk, double Ratio, int Branch)>();
        combat.DamageAdjusters.Add((s, m, actual, _, amount) =>
        {
            if (s == source && m == target && new SkillConfig(actual).SkillId == 12316)
            {
                int branch = actual.GetProperty("_ro3DamageBranch").GetInt32();
                if (branch < 2) StageAttributeEvidence(s, $"tetra-branch-{branch}");
                inputs.Enqueue((Character.Stats(s)[104], new SkillConfig(actual).DamageParam1[branch], branch));
            }
            return amount;
        });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) completed.TrySetResult(); return Task.CompletedTask; });
        await Loop.InvokeAsync(async () =>
        {
            if (variant) await buffs.AddSupportAsync(source, 1234201, 30000, source.Role!.Id);
            if (immune) await buffs.AddSupportAsync(target, 903301, 20000, target.Uid);
            await Character.SetHpSpAsync(source, null, 10000);
        });
        int requested = 1231600 + level;
        var row = new SkillConfig(combat.EffectiveSkillRow(source, requested)
            ?? throw new InvalidOperationException("Tetra effective row required."));
        Assert.Equal(variant ? 1 : 0, row.Mode);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = requested, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        long total = 0;
        foreach (int element in new[] { 5, 4, 3, 2 })
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
            Assert.Single(hit.Dsts);
            Assert.Equal(element, hit.SrcElement); Assert.Equal(1, hit.Split);
            Assert.True(inputs.TryDequeue(out var input));
            Assert.Equal(row.DamageParam1[input.Branch], input.Ratio);
            long expected = (long)Math.Round(input.Matk * input.Ratio / 10000d);
            long actual = Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage;
            _impactOutput.WriteLine($"TETRA_INPUT level={level} mode={row.Mode} branch={input.Branch} element={element} matk={input.Matk} ratio={input.Ratio} expected={expected} actual={actual}");
            Assert.Equal(expected, actual); total += actual;
        }
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(initialLevel, source.Progress.Level);
        Assert.Equal(1_000_000 - total, target.Hp);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(buffs.UnitHasGroup(target, 9016)); Assert.False(buffs.UnitHasGroup(target, 9027));
            Assert.False(buffs.UnitHasGroup(target, 9021)); Assert.Equal(!immune, buffs.UnitHasGroup(target, 9020));
            if (!immune) Assert.Equal(1500, buffs.UnitActiveBuff(target, 902001, source.Role!.Id)?.DurationMs);
            return Task.CompletedTask;
        });
        if (!immune)
        {
            await Task.Delay(1650);
            Assert.Null(buffs.UnitActiveBuff(target, 902001, source.Role!.Id));
        }
    }

    [ClientDataTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TetraPvpControlSuccessAndImmunityAreCheckedPerRealApplication(bool immune, bool rollsSucceed)
    {
        await using var alice = await InWorld("TetraCaster");
        await using var bob = await InWorld("TetraVictim");
        await Gm("tetracaster", "!job 2400"); await Teach("tetracaster", 12316);
        var source = Session("tetracaster"); var target = Session("tetravictim");
        await CardOffenceRoom((alice, source), (bob, target));
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls { ProcValue = rollsSucceed ? 0 : 9999 };
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) completed.TrySetResult(); return Task.CompletedTask; });
        await Loop.InvokeAsync(async () =>
        {
            source.Position = target.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
            if (immune) await buffs.AddAsync(target, 903301, 20000, target.Role!.Id);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1231601, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        long total = 0;
        foreach (int element in new[] { 5, 4, 3, 2 })
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Role!.Id));
            Assert.Equal(element, hit.SrcElement);
            total += Assert.Single(hit.Dsts, d => d.DstID == target.Role!.Id).Damage;
        }
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(1_000_000 - total, target.CurrentHp);
        var applications = bob.Inbox.Snapshot().Where(e => e.IsGame(BuffService.BuffUpdate))
            .Select(e => BuffUpdateNotify.Parser.ParseFrom(e.Body)).Where(n => n.UnitId == target.Role!.Id)
            .SelectMany(n => n.Buffs).Where(b => b.ConfigId is 901601 or 902701 or 902101 or 902001).ToArray();
        if (immune) Assert.Empty(applications);
        else if (rollsSucceed)
        {
            Assert.Equal(new[] { 902001, 902701, 902101, 901601 }, applications.Select(b => b.ConfigId));
            Assert.DoesNotContain(applications, b => b.ConfigId == 902001 && b.Duration == 1500);
        }
        else
        {
            var fallback = Assert.Single(applications);
            Assert.Equal(902001, fallback.ConfigId); Assert.Equal(1500, fallback.Duration);
        }
    }
}
