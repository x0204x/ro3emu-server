using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<(BotClient Bot, GameSession Source, MonsterUnit Target)> VolleyFixtureAsync(int level, int mode)
    {
        var bot = await InWorld("VolleyArrows");
        IsolateDirectHealing();
        await Gm("volleyarrows", "!job 3400"); await Teach("volleyarrows", 13320, level);
        var source = Session("volleyarrows");
        await Loop.InvokeAsync(() =>
        {
            source.Items.RemoveAll(i => i.Grid is 1 or 2);
            source.Items.Add(new InvItem { Guid = 9873301, Entry = 2110120, Pack = 2, Grid = 1, Stack = 1 });
            return Task.CompletedTask;
        });
        await HealingStat(bot, 102, 10000); await HealingStat(bot, 103, 1000);
        await EmblemMarksAsync(source, 1002, mode == 1 ? 10 : 9);
        var target = await Target("volleyarrows", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target); await IsolateAuthoredStageTargetAsync(source, target);
        await Loop.InvokeAsync(async () =>
        {
            target.X = source.Position.X; target.Y = source.Position.Y; target.Z = source.Position.Z + 5;
            _app.Services.GetRequiredService<World>().Map(source.WorldScene).Moved(target);
            await Character.SetHpSpAsync(source, null, 10000);
        });
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
        return (bot, source, target);
    }

    private async Task RunVolleyStagesAsync(BotClient bot, GameSession source, MonsterUnit target, int level, int mode)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var row = new SkillConfig(combat.EffectiveSkillRow(source, 1332000 + level)
            ?? throw new InvalidOperationException("Volley Arrows row required."));
        Assert.Equal(mode, row.Mode);
        int pulses = mode == 0 ? 7 : 10, count = pulses + mode;
        int originalLevel = source.Progress!.Level, completed = 0, resolved = 0, published = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timing = new ConcurrentQueue<CombatService.ImpactTimingSample>();
        var inputs = new ConcurrentQueue<(int Branch, long Attack)>();
        combat.ImpactTimingObserver = timing.Enqueue;
        combat.DamageAdjusters.Add((s, m, actual, _, amount) =>
        {
            if (s == source && m == target && new SkillConfig(actual).SkillId == 13320)
                inputs.Enqueue((actual.GetProperty("_ro3DamageBranch").GetInt32(), Character.Stats(s)[103]));
            return amount;
        });
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) { source.Attack = null; completed++; done.TrySetResult(); } return Task.CompletedTask; });
        combat.CastResolved.Add((s, _, _, _) => { if (s == source) resolved++; return Task.CompletedTask; });
        combat.MonsterSkillHitPublished.Add((s, m, _, _, _) =>
        { if (s == source && m == target) published++; return Task.CompletedTask; });
        using var cdWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 13320));
        int priorCdNotices = bot.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillCdUpdate));
        long beforeHp = target.Hp;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1332000 + level, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = 0 });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, n => n.SessionId == accepted.SessionId);
        var cd = Assert.Single(SkillCdNotify.Parser.ParseFrom((await cdWire.WaitAsync(Wait)).Body).Infos, c => c.SkillId == 13320);
        Assert.Equal(20000, cd.CdEndTimestampMs - cd.CdStartTimestampMs);
        Assert.Equal(0, cd.SkillMode); Assert.Equal(mode == 0 ? 2370 : 3300, timeline.SkillMs);
        var startBuff = await Loop.InvokeAsync(() => Task.FromResult(source.Buffs.Single(b => b.ConfigId == 1332300 + level)));
        Assert.Equal(mode == 0 ? 2000 : 2500, startBuff.DurationMs);
        Assert.Contains(startBuff.Changes, c => c.Attr == 1131 && c.Value == (20 + 2 * level) * 100);
        long cooldown = source.SkillModeCds[(13320, mode)], total = 0;
        Assert.Equal(cd.CdEndTimestampMs, cooldown); Assert.Equal(9999, source.CurrentSp);
        for (int index = 0; index < count; index++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            var dst = Assert.Single(hit.Dsts); Assert.Equal(target.Uid, dst.DstID);
            Assert.True(inputs.TryDequeue(out var input));
            int branch = mode == 1 && index == pulses ? 1 : 0;
            Assert.Equal(branch, input.Branch);
            long expected = (long)Math.Round(input.Attack * row.DamageParam1[branch] / 10000d);
            Assert.Equal(expected, dst.Damage); Assert.Equal(2, hit.Split);
            total += dst.Damage;
            _impactOutput.WriteLine($"VOLLEY mode={mode} level={level} index={index} branch={branch} atk={input.Attack} expected={expected} actual={dst.Damage}");
        }
        await done.Task.WaitAsync(Wait);
        await Task.Delay((int)Math.Max(1, timeline.StartTime + timeline.SkillMs + 100 - MonsterService.NowMs));
        Assert.Equal(count, published); Assert.Equal(1, completed); Assert.Equal(1, resolved);
        Assert.Equal(beforeHp - total, target.Hp); Assert.Equal(originalLevel, source.Progress.Level);
        Assert.Equal(cooldown, source.SkillModeCds[(13320, mode)]); Assert.Equal(9999, source.CurrentSp);
        Assert.Single(bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillCdUpdate)).Skip(priorCdNotices),
            e => SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 13320));
        Assert.Empty(inputs); Assert.DoesNotContain(source.Buffs, b => b.ConfigId == startBuff.ConfigId);
        var stages = CombatService.AuthoredDamageStages(row, data.SkillTemplate(row.ScriptShow));
        var samples = timing.Where(t => t.CastId == accepted.SessionId && t.Stage.EndsWith("-resolve-entered", StringComparison.Ordinal)).ToArray();
        Assert.Equal(count, samples.Length);
        for (int index = 0; index < count; index++)
        {
            Assert.True(stages[index].Point > 0);
            long deadline = timeline.StartTime + CombatService.ImpactOffset(data.SkillTemplate(row.ScriptShow),
                stages[index].Point, timeline.ChantMs, timeline.SkillMs);
            Assert.True(samples[index].TimeMs >= deadline, $"Volley stage {index} precedes its authored deadline.");
        }
        Assert.Equal(mode, stages.Count(s => s.Branch == 1)); // mode1的150%末段只能有一次。
    }

    [ClientDataTheory]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    public async Task VolleyArrowsLowAndHighestPayOnceAndDealAllAuthoredPulsesAndFinisher(int level, int mode)
    {
        var fixture = await VolleyFixtureAsync(level, mode); await using var bot = fixture.Bot;
        await RunVolleyStagesAsync(bot, fixture.Source, fixture.Target, level, mode);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task VolleyArrowsExactEmblemStageNotPermanentAndWithdrawalRestoresSevenPulses(int level)
    {
        var fixture = await VolleyFixtureAsync(level, 0); await using var bot = fixture.Bot;
        var source = fixture.Source; var combat = _app.Services.GetRequiredService<CombatService>();
        SkillConfig Row() => new(combat.EffectiveSkillRow(source, 1332000 + level)
            ?? throw new InvalidOperationException("Volley Arrows row required."));
        Assert.Equal(0, Row().Mode);
        using var grant = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.TryGetValue(13320, out var group)
            && group.AffectParam.TryGetValue(15, out var mode) && mode.AffectValue == 1);
        await EmblemMarksAsync(source, 1002, 10); await grant.WaitAsync(Wait);
        Assert.Equal(1, Row().Mode); Assert.DoesNotContain(113363, source.Progress!.Skills.Keys);
        await EmblemOwnedAsync(source, async () =>
        {
            source.Emblems.CurrentPlan = 1;
            await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(source, source.EmblemPower);
        });
        Assert.Equal(0, Row().Mode);
        await EmblemMarksAsync(source, 1002, 10); Assert.Equal(1, Row().Mode);
        await Gm("volleyarrows", "!job 1000"); Assert.Equal(0, Row().Mode);
        await Gm("volleyarrows", "!job 3400"); Assert.Equal(1, Row().Mode);
        await EmblemMarksAsync(source, 1002, 0); Assert.Equal(0, Row().Mode);
        await RunVolleyStagesAsync(bot, source, fixture.Target, level, 0);
    }

    [ClientDataTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task VolleyArrowsInterruptOrSceneChangePreventsRemainingPulsesAndFinalDamage(int mode, bool sceneChange)
    {
        var fixture = await VolleyFixtureAsync(1, mode); await using var bot = fixture.Bot;
        var source = fixture.Source; var target = fixture.Target;
        var combat = _app.Services.GetRequiredService<CombatService>();
        int stopAfter = mode == 0 ? 1 : 10, published = 0, completed = 0;
        long dealt = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) completed++; return Task.CompletedTask; });
        combat.MonsterSkillHitPublished.Add(async (s, m, _, amount, _) =>
        {
            if (s != source || m != target) return;
            dealt += amount;
            if (++published != stopAfter) return;
            if (sceneChange) source.SceneGeneration++;
            else await combat.InterruptAsync(source);
            stopped.TrySetResult();
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1332001, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = 0 });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, n => n.SessionId == accepted.SessionId);
        long cooldown = source.SkillModeCds[(13320, mode)];
        await stopped.Task.WaitAsync(Wait);
        await Task.Delay((int)Math.Max(1, timeline.StartTime + timeline.SkillMs + 100 - MonsterService.NowMs));
        Assert.Equal(stopAfter, published); Assert.Equal(0, completed); Assert.Equal(1_000_000 - dealt, target.Hp);
        Assert.Equal(cooldown, source.SkillModeCds[(13320, mode)]); Assert.Equal(9999, source.CurrentSp);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task VolleyArrowsEnhancedLowAndHighestDealTenPulsesAndOneFinisherToPlayer(int level)
    {
        await using var bot = await InWorld("VolleyPvp"); await using var enemy = await InWorld("VolleyEnemy");
        await Gm("volleypvp", "!job 3400"); await Teach("volleypvp", 13320, level);
        var source = Session("volleypvp"); var target = Session("volleyenemy");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        await EmblemMarksAsync(source, 1002, 10);
        await Loop.InvokeAsync(() =>
        {
            source.Items.RemoveAll(i => i.Grid is 1 or 2);
            source.Items.Add(new InvItem { Guid = 9873301, Entry = 2110120, Pack = 2, Grid = 1, Stack = 1 });
            target.Position = source.Position.Clone(); target.Position.Z += 5;
            return Task.CompletedTask;
        });
        long atk = Character.Stats(source)[103], total = 0;
        var row = new SkillConfig(combat.EffectiveSkillRow(source, 1332000 + level)
            ?? throw new InvalidOperationException("Volley PvP row required."));
        Assert.Equal(1, row.Mode);
        var done = CastPassiveCompletion(combat, source);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1332000 + level, TargetId = target.Role!.Id, TargetPos = target.Position.Clone(), Face = 0 });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        for (int index = 0; index < 11; index++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            var dst = Assert.Single(hit.Dsts); Assert.Equal(target.Role.Id, dst.DstID);
            long expected = (long)Math.Round(atk * row.DamageParam1[index == 10 ? 1 : 0] / 10000d);
            Assert.Equal(expected, dst.Damage); total += dst.Damage;
        }
        await done.Task.WaitAsync(Wait);
        Assert.Equal(1_000_000 - total, target.CurrentHp);
    }

    [ClientDataTheory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 90)]
    [InlineData(1, 90)]
    public void VolleyArrowsForwardRectangleHonorsPairedLengthAndConfiguredWidth(int mode, int face)
    {
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 1332001 + mode * 10)
            ?? throw new InvalidOperationException("Volley range row required.");
        // 寬6m沿伺服器表；指示器／說明4m衝突另列UNKNOWN，這僅驗候選規則。
        Vec3 At(float forward, float side) => face == 0
            ? new() { X = side, Z = forward } : new() { X = forward, Z = -side };
        bool Contains(float forward, float side) => CombatService.VolleyArrowAreaContains(row, new Vec3(), face,
            At(forward, side)) ?? throw new InvalidOperationException("Volley range owner required.");
        Assert.True(Contains(5, 0)); Assert.True(Contains(17.99f, 2.99f));
        Assert.True(Contains(5, -2.99f)); Assert.False(Contains(18.01f, 0));
        Assert.False(Contains(-.01f, 0)); Assert.False(Contains(5, 3.01f));
        Assert.False(Contains(5, -3.01f));
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task VolleyArrowsRealHitsExcludeRearSideAndBeyondFrontBoundary(int mode)
    {
        var fixture = await VolleyFixtureAsync(1, mode); await using var bot = fixture.Bot;
        var excluded = new List<MonsterUnit>();
        foreach (var (forward, side) in new[] { (-.1f, 0f), (18.1f, 0f), (5f, 3.1f) })
        {
            var target = await Target("volleyarrows", stationary: true, immobile: true);
            await CardOffenceDummyAsync(target);
            await Loop.InvokeAsync(() =>
            {
                target.X = fixture.Source.Position.X + side; target.Y = fixture.Source.Position.Y;
                target.Z = fixture.Source.Position.Z + forward;
                _app.Services.GetRequiredService<World>().Map(fixture.Source.WorldScene).Moved(target);
                return Task.CompletedTask;
            });
            excluded.Add(target);
        }
        await RunVolleyStagesAsync(bot, fixture.Source, fixture.Target, 1, mode);
        Assert.All(excluded, target => Assert.Equal(1_000_000, target.Hp));
    }
}
