using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private Task IsolateAuthoredStageTargetAsync(GameSession source, MonsterUnit target) => Loop.InvokeAsync(async () =>
    {
        var world = _app.Services.GetRequiredService<World>();
        foreach (var other in world.Map(source.WorldScene).Units.OfType<MonsterUnit>().Where(m => m != target).ToArray())
            await world.RemoveUnitAsync(other);
    });

    private void StageAttributeEvidence(GameSession source, string phase)
    {
        var stats = Character.Stats(source);
        _impactOutput.WriteLine("STAGE_MATERIAL " + JsonSerializer.Serialize(new
        {
            Phase = phase,
            source.Progress!.Level,
            source.Progress.Career,
            Stats = stats.Data,
            Buffs = source.Buffs.Select(b => new { b.ConfigId, Changes = b.Changes.Select(c => new { c.Attr, c.Value }) }),
            Providers = Character.ExtraAttributes.Select(p => new
            {
                Owner = p.Method.DeclaringType?.Name + "." + p.Method.Name,
                Values = p(source).Select(c => new { c.Id, c.Value }).ToArray(),
            }).ToArray(),
        }));
    }

    [ClientDataTheory]
    [InlineData(12302, 1, 2)]
    [InlineData(12302, 10, 2)]
    [InlineData(12342, 1, 2)]
    [InlineData(12342, 10, 2)]
    [InlineData(11624, 1, 11)]
    [InlineData(11624, 5, 11)]
    public async Task AuthoredStagesLowAndHighestDealEveryBranchButPayAndCompleteOnce(int group, int level, int count)
    {
        await using var bot = await InWorld("StageDamage");
        IsolateDirectHealing();
        await Gm("stagedamage", group == 11624 ? "!job 1410" : "!job 2400");
        await Teach("stagedamage", group, level);
        if (group == 11624) await EquipConditionFixtureAsync("stagedamage", 2050120);
        await HealingStat(bot, 102, 10000); await HealingStat(bot, 103, 1000); await HealingStat(bot, 104, 1000);
        var source = Session("stagedamage"); var target = await Target("stagedamage", true, immobile: true);
        await CardOffenceDummyAsync(target);
        await IsolateAuthoredStageTargetAsync(source, target);
        int initialLevel = source.Progress!.Level;
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        int completed = 0, resolved = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timing = new ConcurrentQueue<CombatService.ImpactTimingSample>();
        var inputs = new ConcurrentQueue<(long Attack, long Dexterity, long Speed, double Ratio, double Flat)>();
        int materialSamples = 0;
        combat.ImpactTimingObserver = timing.Enqueue;
        combat.DamageAdjusters.Add((s, m, actual, _, amount) =>
        {
            if (s == source && m == target && new SkillConfig(actual).SkillId == group)
            {
                var current = Character.Stats(s); var configured = new SkillConfig(actual);
                int branch = actual.GetProperty("_ro3DamageBranch").GetInt32();
                if (materialSamples++ < 2) StageAttributeEvidence(s, $"damage-group-{group}-branch-{branch}");
                inputs.Enqueue((current[group == 11624 ? 103 : 104], current[5], current[115],
                    configured.DamageParam1[branch], configured.DamageParam2.ElementAtOrDefault(branch)));
            }
            return amount; // 唯讀診斷，不更動產品傷害。
        });
        await Loop.InvokeAsync(async () =>
        {
            if (group == 11624)
                await _app.Services.GetRequiredService<BuffService>().AddSupportAsync(source, 11164701, 30000, source.Role!.Id);
            await Character.SetHpSpAsync(source, null, 10000);
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) { completed++; done.TrySetResult(); } return Task.CompletedTask; });
            combat.CastResolved.Add((s, _, _, _) => { if (s == source) resolved++; return Task.CompletedTask; });
        });
        int requested = group * 100 + level;
        var row = new SkillConfig(combat.EffectiveSkillRow(source, requested)
            ?? throw new InvalidOperationException("Authored effective row required."));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = requested, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = source.Face });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, h => h.SessionId == accepted.SessionId);
        long sp = source.CurrentSp ?? -1, cooldown = source.SkillCds.GetValueOrDefault(group);
        Assert.Equal(10000 - (group == 12302 ? 5 : group == 12342 ? 65 : 0), sp);
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
            Assert.Single(hit.Dsts);
            long damage = Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage;
            Assert.True(inputs.TryDequeue(out var input));
            double resonance = group == 12342 ? Math.Min(input.Dexterity, 100) * 10 : 0;
            double speed = group == 11624 ? 1 + Math.Min(input.Speed, 500) * 50 / 10000d : 1;
            long expected = (long)Math.Round((input.Attack * (input.Ratio + resonance) / 10000d + input.Flat)
                * speed * (group == 11624 ? 1 : .75)); // 原EleConfig: Ghost→Neutral -25%。
            Assert.Equal(row.DamageParam1[index == count - 1 ? 1 : 0], input.Ratio);
            _impactOutput.WriteLine($"STAGE_INPUT index={index} atk={input.Attack} dex={input.Dexterity} aspd={input.Speed} ratio={input.Ratio} flat={input.Flat} expected={expected} actual={damage}");
            Assert.Equal(expected, damage);
            Assert.Equal(group == 12342 && index == 0 ? 3 : 1, hit.Split);
            total += damage;
        }
        await done.Task.WaitAsync(Wait);
        Assert.Equal(initialLevel, source.Progress.Level);
        Assert.Equal(1_000_000 - total, target.Hp);
        Assert.Equal(sp, source.CurrentSp); Assert.Equal(cooldown, source.SkillCds.GetValueOrDefault(group));
        Assert.Equal(1, completed); Assert.Equal(1, resolved);
        var plan = CombatService.AuthoredDamageStages(row, data.SkillTemplate(row.ScriptShow));
        var received = timing.Where(t => t.CastId == accepted.SessionId && t.Stage.EndsWith("-resolve-entered", StringComparison.Ordinal)).ToArray();
        Assert.Equal(count, received.Length);
        for (int index = 0; index < count; index++)
        {
            long deadline = timeline.StartTime + CombatService.ImpactOffset(data.SkillTemplate(row.ScriptShow),
                plan[index].Point, timeline.ChantMs, timeline.SkillMs);
            Assert.True(received[index].TimeMs >= deadline, $"Stage {index} ran before its authored deadline.");
            if (index > 0) Assert.True(received[index].TimeMs >= received[index - 1].TimeMs);
        }
        _impactOutput.WriteLine($"STAGES group={group} level={level} hits={count} total={total} completions={completed}");
    }

    [ClientDataTheory]
    [InlineData(12302, false)]
    [InlineData(12302, true)]
    [InlineData(12342, false)]
    [InlineData(12342, true)]
    [InlineData(11624, false)]
    [InlineData(11624, true)]
    public async Task AuthoredStagesCancelOrChangedSceneStopsEveryRemainingDamage(int group, bool epoch)
    {
        await using var bot = await InWorld("StageCancel");
        await Gm("stagecancel", group == 11624 ? "!job 1410" : "!job 2400"); await Teach("stagecancel", group);
        if (group == 11624) await EquipConditionFixtureAsync("stagecancel", 2050120);
        await HealingStat(bot, 102, 10000); await HealingStat(bot, 103, 1000); await HealingStat(bot, 104, 1000);
        var source = Session("stagecancel"); var target = await Target("stagecancel", true, immobile: true);
        await CardOffenceDummyAsync(target);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        int completed = 0; bool invalidated = false;
        await Loop.InvokeAsync(async () =>
        {
            if (group == 11624)
                await _app.Services.GetRequiredService<BuffService>().AddSupportAsync(source, 11164701, 30000, source.Role!.Id);
            combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) completed++; return Task.CompletedTask; });
            combat.MonsterSkillHitPublished.Add(async (s, m, _, _, _) =>
            {
                if (s != source || m != target || invalidated) return;
                invalidated = true;
                if (epoch) source.SceneGeneration++;
                else await combat.InterruptAsync(source);
            });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = group * 100 + 1, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var first = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        long damage = Assert.Single(first.Dsts, d => d.DstID == target.Uid).Damage;
        await Task.Delay(3000);
        Assert.True(invalidated); Assert.Equal(0, completed); Assert.Equal(1_000_000 - damage, target.Hp);
        Assert.Single(bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
    }
}
