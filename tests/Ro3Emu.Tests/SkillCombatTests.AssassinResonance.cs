using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(15301, 1, 1001, 11531601, 11531701, 3000, 26000)]
    [InlineData(15301, 5, 1001, 11531601, 11531701, 3000, 38000)]
    [InlineData(15303, 1, 1002, 11531901, 11532501, 2000, 2300)]
    [InlineData(15303, 10, 1002, 11531901, 11532501, 2000, 3200)]
    public async Task AssassinResonanceUsesRealCompletedCastsForDamageSilenceSpeedAndStageRevocation(
        int group, int level, int mark, int grant, int ownBuff, int duration, int baseRatio)
    {
        await using var bot = await InWorld("AssassinEcho");
        await Gm("assassinecho", "!lvl 99"); await Gm("assassinecho", "!job 5400");
        await Teach("assassinecho", group, level);
        if (group == 15303) await EquipConditionFixtureAsync("assassinecho", 2120120);
        await HealingStat(bot, 103, 1000);
        var source = Session("assassinecho");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var target = await Target("assassinecho", stationary: true, immobile: true);
        var ring = await Target("assassinecho", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target); await CardOffenceDummyAsync(ring);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new CardOffenceRolls();
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var unit in map.Units.OfType<MonsterUnit>().Where(m => m != target && m != ring).ToArray())
            { unit.Hp = 0; unit.Dead = true; await world.RemoveUnitAsync(unit); }
            target.X = source.Position.X + 1; target.Z = source.Position.Z; map.Moved(target);
            ring.X = source.Position.X + 5.5f; ring.Z = source.Position.Z; map.Moved(ring);
            await buffs.ReconcileEnergyAsync(source);
            combat.SkillCastCompleted.Add((s, _, row) =>
            { if (s == source && new SkillConfig(row).SkillId == group) completed.TrySetResult(); return Task.CompletedTask; });
        });
        long speed = source.Speed, resistance = Character.Stats(source)[728];
        async Task Cast(bool enhanced)
        {
            completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Loop.InvokeAsync(async () =>
            {
                await combat.InterruptAsync(source); await CombatService.ResetCooldownAsync(source, group);
                if (group == 15301) Assert.True(await buffs.SetExistingEnergyValueAsync(source, 540001, 99));
            });
            var selected = new SkillConfig(combat.EffectiveSkillRow(source, group * 100 + level)!.Value);
            Assert.Equal(enhanced ? 1 : 0, selected.Mode);
            int ratio = baseRatio + (group == 15303 && enhanced ? 1000 : 0);
            Assert.Equal(ratio, selected.DamageParam1[0]);
            if (group == 15303) Assert.Equal(enhanced ? 600 : 500, selected.Range[0]);
            long before = target.Hp, ringBefore = ring.Hp;
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = group * 100 + level, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await completed.Task.WaitAsync(Wait);
            // RollingCutter原件AXaddBplus1_withlimit：每1%攻速加0.5%，上限500%；無防禦/元素修正。
            double multiplier = group == 15303 ? 1 + .005 * Math.Min(500, Character.Stats(source)[115]) : 1;
            long expected = (long)Math.Round(1000 * ratio / 10000d * multiplier);
            Assert.Equal(expected, before - target.Hp);
            Assert.Equal(group == 15303 && enhanced ? expected : 0, ringBefore - ring.Hp);
            if (!enhanced)
            {
                Assert.DoesNotContain(source.Buffs, b => b.ConfigId == ownBuff);
                Assert.False(buffs.UnitHasGroup(target, 9023));
                return;
            }
            var own = await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                n => n.UnitId == source.Role!.Id && n.Buffs.Any(b => b.ConfigId == ownBuff));
            Assert.Equal(duration, Assert.Single(own.Buffs, b => b.ConfigId == ownBuff).Duration);
            Assert.Equal((long)Math.Ceiling(speed * 1.1 - 1e-6), source.Speed);
            Assert.Equal(resistance + (group == 15301 ? 1000 : 0), Character.Stats(source)[728]);
            if (group == 15301)
            {
                var silence = await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                    n => n.UnitId == target.Uid && n.Buffs.Any(b => b.ConfigId == 902301));
                Assert.Equal(500, Assert.Single(silence.Buffs, b => b.ConfigId == 902301).Duration);
                Assert.True(buffs.UnitActionBlocked(target, ForbiddenState.CastSkill));
                await Task.Delay(650);
                Assert.False(buffs.UnitActionBlocked(target, ForbiddenState.CastSkill));
                Assert.Contains(source.Buffs, b => b.ConfigId == ownBuff);
            }
        }
        await EmblemMarksAsync(source, mark, 5); await Cast(false);
        await EmblemMarksAsync(source, mark, 6); await Cast(true);
        await Task.Delay(duration + 150);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == ownBuff);
        Assert.Equal(speed, source.Speed); Assert.Equal(resistance, Character.Stats(source)[728]);
        await Cast(true);
        await EmblemMarksAsync(source, mark, 0);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == ownBuff);
        Assert.Equal(speed, source.Speed); Assert.Equal(resistance, Character.Stats(source)[728]);
        await Task.Delay(650); await Cast(false);
        await EmblemMarksAsync(source, mark, 6); await Cast(true);
        await Gm("assassinecho", "!job 1000");
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == ownBuff);
        Assert.DoesNotContain(grant, EmblemService.ResonanceSkills(source, data));
        Assert.Equal(0, new SkillConfig(combat.EffectiveSkillRow(source, group * 100 + level)!.Value).Mode);
        Assert.DoesNotContain(source.Progress!.Skills.Keys, g => g == grant / 100);
    }
    [ClientDataFact]
    public async Task AssassinRollingCutterSharesThreeTargetsAcrossPvpAndMonstersAndExcludesOutsideRadius()
    {
        await using var bot = await InWorld("RollOwner");
        await using var first = await InWorld("RollFirst");
        await using var second = await InWorld("RollSecond");
        await using var outside = await InWorld("RollOutside");
        await Gm("rollowner", "!lvl 99"); await Gm("rollowner", "!job 5400");
        await Teach("rollowner", 15303);
        await EquipConditionFixtureAsync("rollowner", 2120120);
        await HealingStat(bot, 103, 1000);
        var source = Session("rollowner");
        var targets = new[] { Session("rollfirst"), Session("rollsecond"), Session("rolloutside") };
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, .. targets])));
        foreach (var (client, player) in new[] { (bot, source), (first, targets[0]), (second, targets[1]), (outside, targets[2]) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(client, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await client.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(client, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(client, 101, 100_000); await HealingStat(client, 105, 0);
        }
        var near = await Target("rollowner", stationary: true, immobile: true);
        var capped = await Target("rollowner", stationary: true, immobile: true);
        await CardOffenceDummyAsync(near); await CardOffenceDummyAsync(capped);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new CardOffenceRolls();
            combat.SetEventPvPHandlers((a, b) => a != b, (_, _) => Task.CompletedTask);
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var unit in map.Units.OfType<MonsterUnit>().Where(m => m != near && m != capped).ToArray())
            { unit.Hp = 0; unit.Dead = true; await world.RemoveUnitAsync(unit); }
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i].Position = source.Position.Clone();
                targets[i].Position.X += i == 0 ? 1 : i == 1 ? 5.5f : 6.5f;
                await Character.SetHpSpAsync(targets[i], 100_000, null);
            }
            near.X = source.Position.X + 2; near.Z = source.Position.Z; map.Moved(near);
            capped.X = source.Position.X + 5.8f; capped.Z = source.Position.Z; map.Moved(capped);
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) completed.TrySetResult(); return Task.CompletedTask; });
        });
        await EmblemMarksAsync(source, 1002, 6);
        // 自身範圍不需要假TargetId；兩玩家＋最近怪物共用原表3人上限。
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530301, TargetPos = source.Position.Clone() });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await completed.Task.WaitAsync(Wait);
        long expected = (long)Math.Round(330 * (1 + .005 * Math.Min(500, Character.Stats(source)[115])));
        // 原件JobConfig5400=圖騰2、初心者1000=圖騰1；TotemRestraintConfig[2]._iTotem0=2000。
        long pvpExpected = expected * 12000 / 10000;
        Assert.Equal(pvpExpected, 100_000 - Character.Stats(targets[0])[2000]);
        Assert.Equal(pvpExpected, 100_000 - Character.Stats(targets[1])[2000]);
        Assert.Equal(100_000, Character.Stats(targets[2])[2000]);
        Assert.Equal(expected, 1_000_000 - near.Hp);
        Assert.Equal(1_000_000, capped.Hp);
        // 合成合法type27加一目標：只測effective參數可用性，不聲稱原服有此裝備組合。
        completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            source.TalentSkillModifiers = [(0, 0, new SkillParameterRules.Modifier(15303, 27, 1, 1, 0))];
            Assert.Equal(4, new SkillConfig(combat.EffectiveSkillRow(source, 1530301)!.Value).TargetMax);
            await combat.InterruptAsync(source); await CombatService.ResetCooldownAsync(source, 15303);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530301 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(expected, 1_000_000 - capped.Hp);
        Assert.Equal(2 * pvpExpected, 100_000 - Character.Stats(targets[0])[2000]);
        Assert.Equal(2 * pvpExpected, 100_000 - Character.Stats(targets[1])[2000]);
        Assert.Equal(100_000, Character.Stats(targets[2])[2000]);
    }

    [ClientDataTheory]
    [InlineData(1, 1300)]
    [InlineData(10, 2200)]
    public async Task AssassinDreadThreatUsesTheAuthoredFiveMetreFiveEnemyCircle(int level, long expected)
    {
        await using var bot = await InWorld("DreadCircle");
        await Gm("dreadcircle", "!lvl 99"); await Gm("dreadcircle", "!job 5400");
        await Teach("dreadcircle", 15306, level); await HealingStat(bot, 103, 1000);
        var source = Session("dreadcircle");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < 7; i++)
        { var target = await Target("dreadcircle", stationary: true, immobile: true); await CardOffenceDummyAsync(target); targets.Add(target); }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new CardOffenceRolls();
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var unit in map.Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            { unit.Hp = 0; unit.Dead = true; await world.RemoveUnitAsync(unit); }
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i].X = source.Position.X + (i < 5 ? 1 + .5f * i : i == 5 ? 4.5f : 5.5f);
                targets[i].Z = source.Position.Z; map.Moved(targets[i]);
            }
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) completed.TrySetResult(); return Task.CompletedTask; });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530600 + level });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await completed.Task.WaitAsync(Wait);
        for (int i = 0; i < targets.Count; i++) Assert.Equal(i < 5 ? expected : 0, 1_000_000 - targets[i].Hp);
    }

}
