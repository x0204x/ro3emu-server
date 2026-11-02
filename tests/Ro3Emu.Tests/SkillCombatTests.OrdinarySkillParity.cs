using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task OrdinaryCastFinished(BotClient bot, GameSession source, int skill, long target)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, cast, _) =>
        {
            if (s == source && cast.Request.SkillId / 100 == skill / 100) completed.TrySetResult();
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await completed.Task.WaitAsync(Wait);
    }

    [ClientDataTheory]
    [InlineData(2100, 12015, 1, 902101, 0)]
    [InlineData(2100, 12015, 10, 902101, 0)]
    [InlineData(4200, 14121, 1, 902301, 0)]
    [InlineData(4200, 14121, 5, 902301, 0)]
    [InlineData(4100, 14016, 1, 1401601, 0)]
    [InlineData(1210, 11411, 1, 902501, 0)]
    [InlineData(1210, 11411, 10, 902501, 0)]
    [InlineData(2100, 12015, 1, 0, 1100)]
    [InlineData(2100, 12015, 10, 0, 2000)]
    public async Task OrdinaryPvpSkillAppliesItsRealBuffAndRespectsChance(int job, int group, int level, int expected, int roll)
    {
        await using var alice = await InWorld("OrdinarySource");
        await using var bob = await InWorld("OrdinaryTarget");
        await Gm("ordinarysource", $"!job {job}");
        await Teach("ordinarysource", group, level);
        var source = Session("ordinarysource"); var target = Session("ordinarytarget");
        await CardOffenceRoom((alice, source), (bob, target));
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new StormBoundaryRolls(roll);
        await Loop.InvokeAsync(() =>
        {
            source.Position = target.Position.Clone();
            combat.SetEventPvPHandlers((s, t) => s == source && t == target, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        await OrdinaryCastFinished(alice, source, group * 100 + level, target.Role!.Id);
        if (expected == 0)
            Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 902101);
        else
        {
            var applied = Assert.Single(target.Buffs, b => b.ConfigId == expected);
            Assert.Equal(source.Role!.Id, applied.SourceId);
            Assert.False(applied.IsGuardianOwned);
            Assert.True(applied.ExpireMs > MonsterService.NowMs);
            if (group == 14016) Assert.Contains(applied.Changes, c => c.Attr == AttributeId.PerSpeed && c.Value == -2000);
            if (expected == 902501)
            {
                var buffs = _app.Services.GetRequiredService<BuffService>();
                Assert.True(buffs.Blinded(target));
                var far = target.Position.Clone(); far.X += 6;
                Assert.False(buffs.CanSeePosition(target, far));
                var update = await Next(bob, 31048, AOIAttrChange.Parser,
                    m => m.EntityUuid == (ulong)target.Role.Id && m.Property.Any(p => p.Idx == 2704 && p.Data == 1));
                Assert.Contains(update.Property, p => p.Idx == 2710 && p.Data == BuffService.BlindRadiusCm);
                await Loop.InvokeAsync(() => buffs.RemoveAsync(target, applied));
                Assert.True(buffs.CanSeePosition(target, far));
                await Next(bob, 31048, AOIAttrChange.Parser,
                    m => m.EntityUuid == (ulong)target.Role.Id && m.Property.Any(p => p.Idx == 2704 && p.Data == 0));
            }
        }
    }

    [ClientDataFact]
    public async Task OrdinaryPvpSkillCannotBypassTargetControlImmunity()
    {
        await using var alice = await InWorld("ImmuneSource");
        await using var bob = await InWorld("ImmuneTarget");
        await Gm("immunesource", "!job 2100"); await Teach("immunesource", 12015, 10);
        var source = Session("immunesource"); var target = Session("immunetarget");
        await CardOffenceRoom((alice, source), (bob, target));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new StormBoundaryRolls(0);
        await Loop.InvokeAsync(async () =>
        {
            source.Position = target.Position.Clone();
            combat.SetEventPvPHandlers((s, t) => s == source && t == target, (_, _) => Task.CompletedTask);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(target, 903201, 20000, target.Role!.Id);
        });
        await OrdinaryCastFinished(alice, source, 1201510, target.Role!.Id);
        Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 902101);
    }

    [ClientDataFact]
    public async Task OrdinaryPvpGroundSkillUsesTheAimedPointInsteadOfCasterPosition()
    {
        await using var alice = await InWorld("GroundSource");
        await using var bob = await InWorld("GroundTarget");
        await using var other = await InWorld("GroundOutside");
        await Gm("groundsource", "!job 2200"); await Teach("groundsource", 12108, 3);
        var source = Session("groundsource"); var target = Session("groundtarget"); var outside = Session("groundoutside");
        await CardOffenceRoom((alice, source), (bob, target), (other, outside));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new StormBoundaryRolls(200);
        await Loop.InvokeAsync(() =>
        {
            outside.Position = source.Position.Clone(); outside.Position.Z += 1;
            target.Position = source.Position.Clone(); target.Position.X += 10;
            combat.SetEventPvPHandlers((s, t) => s == source && (t == target || t == outside), (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210803, TargetPos = target.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1210803 && h.Dsts.Any(d => d.DstID == target.Role!.Id));
        Assert.True(Assert.Single(hit.Dsts, d => d.DstID == target.Role!.Id).Damage > 0);
        Assert.DoesNotContain(hit.Dsts, d => d.DstID == outside.Role!.Id);
        Assert.Equal(1_000_000, outside.CurrentHp);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanseRealCastRemovesConfiguredCategoriesButPreservesOtherEffects(bool improved)
    {
        await using var alice = await InWorld("CleanseSource");
        await using var bob = await InWorld("CleanseTarget");
        await Gm("cleansesource", "!job 4100"); await Teach("cleansesource", 14014);
        var source = Session("cleansesource"); var target = Session("cleansetarget");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            source.Position = target.Position.Clone();
            if (improved) await buffs.AddAsync(source, 1410401, 20000, source.Role!.Id);
            foreach (int id in new[] { 902301, 902501, 906501, 908401, 901601, 903001, 1400101 })
                await buffs.AddAsync(target, id, 20000, source.Role!.Id);
        });
        await OrdinaryCastFinished(alice, source, 1401401, target.Role!.Id);
        Assert.DoesNotContain(target.Buffs, b => b.ConfigId is 902301 or 902501 or 906501 or 908401);
        Assert.Equal(!improved, target.Buffs.Any(b => b.ConfigId == 901601));
        foreach (int id in new[] { 903001, 1400101 }) Assert.Contains(target.Buffs, b => b.ConfigId == id);
    }

    [ClientDataFact]
    public async Task PoisonBoomRealCastCleansesExistingPoisonAndBlocksNewPoisonUntilExpiry()
    {
        await using var alice = await InWorld("PoisonGuard");
        await using var bob = await InWorld("PoisonVictim");
        IsolateDirectHealing();
        await Gm("poisonguard", "!job 5100"); await Teach("poisonguard", 15007);
        await HealingStat(bob, 101, 100000);
        var source = Session("poisonguard"); var target = Session("poisonvictim");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            source.Position = target.Position.Clone();
            await Character.SetHpSpAsync(target, 100000, null);
            await buffs.AddAsync(target, 903001, 20000, source.Role!.Id);
        });
        await OrdinaryCastFinished(alice, source, 1500701, target.Role!.Id);
        var tick = buffs.PeriodicTick ?? throw new InvalidOperationException("週期傷害未接線");
        TaskCompletionSource ticked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long poisonGuid = 0, tickAt = 0, interval = 0, before = 0;
        try
        {
            await Loop.InvokeAsync(async () =>
            {
                // 使用真正登錄的週期生命期；手工建構的 effect 應被 PeriodicCurrent 拒絕。
                Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 903001);
                await buffs.AddAsync(target, 903001, 20000, source.Role!.Id);
                var poison = Assert.Single(target.Buffs, b => b.ConfigId == 903001);
                Assert.Single(target.Buffs, b => b.ConfigId == 1501101);
                interval = BuffRuleTables.For(_app.Services.GetRequiredService<GameData>()).Periodic[903001].IntervalMs;
                poisonGuid = poison.Guid;
                tickAt = poison.ExpireMs - poison.DurationMs + interval;
                before = target.CurrentHp ?? 0;
                buffs.PeriodicTick = async (effect, active) =>
                {
                    await tick(effect, active);
                    if (active.Guid == poisonGuid) ticked.TrySetResult();
                };
                buffs.TickPeriodic(tickAt);
            });
            await ticked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Loop.InvokeAsync(async () =>
            {
                Assert.Equal(before, target.CurrentHp);
                Assert.Contains(target.Buffs, b => b.ConfigId == 903001);
                await buffs.RemoveAsync(target, Assert.Single(target.Buffs, b => b.ConfigId == 1501101));
                ticked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                buffs.TickPeriodic(tickAt + interval);
            });
            await ticked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Loop.InvokeAsync(() =>
            {
                Assert.True(target.CurrentHp < before);
                return Task.CompletedTask;
            });
        }
        finally
        {
            await Loop.InvokeAsync(() => { buffs.PeriodicTick = tick; return Task.CompletedTask; });
        }
    }
}
