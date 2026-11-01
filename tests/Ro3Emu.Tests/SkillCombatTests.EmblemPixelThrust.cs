using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task EquipPixelSpearAsync(BotClient bot, GameSession source)
    {
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>();
            foreach (var monster in world.Map(source.WorldScene).Units.OfType<MonsterUnit>().ToArray())
                await world.RemoveUnitAsync(monster);
            source.Items.RemoveAll(i => i.Grid is 1 or 2);
            source.Items.Add(new InvItem { Guid = 9873101, Entry = 2050120, Pack = 2, Grid = 1, Stack = 1 });
        });
        await HealingStat(bot, 103, 1000);
    }

    private async Task<(ActiveBuff? Mark, long Cooldown)> PixelWireCastAsync(BotClient bot, GameSession source,
        GameSession target, int level, int mode)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, row) =>
        {
            if (s == source && new SkillConfig(row).SkillId == 11308) { source.Attack = null; completed.TrySetResult(); }
            return Task.CompletedTask;
        });
        var (origin, before) = await Loop.InvokeAsync(async () =>
        {
            await combat.InterruptAsync(source);
            source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
            await Character.SetHpSpAsync(target, 1_000_000, 10000);
            var start = source.Position.Clone();
            var nav = _app.Services.GetRequiredService<WorldData>().Map(source.SceneEntry)!.Nav;
            // 出生點 +X 5m 是不可走格，不能把目標放進牆後再要求拉人穿過牆。
            target.Position = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }
                .Select(d => new Vec3
                {
                    X = start.X + d.Item1 * 5, Z = start.Z + d.Item2 * 5,
                    Y = nav?.WalkHeight(start.X + d.Item1 * 5, start.Z + d.Item2 * 5, start.Y) ?? start.Y,
                })
                .First(p => nav == null || nav.IsWalkable(p.X, p.Y, p.Z)
                    && nav.CanWalk(start.X, start.Y, start.Z, p.X, p.Z)
                    && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
            return (start, target.Position.Clone());
        });
        var row = new SkillConfig(combat.EffectiveSkillRow(source, 1130800 + level)
            ?? throw new InvalidOperationException("Phantom Thrust row required."));
        Assert.Equal(mode, row.Mode);
        Assert.Equal(15000 + 5000 * level + mode * 15000, row.DamageParam1[0]);
        long expected = (long)Math.Round(Character.Stats(source)[103] * row.DamageParam1[0] / 10000d);
        using var movement = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillMove)
            && SkillStartMoveNotify.Parser.ParseFrom(e.Body) is var m && m.EntityID == (ulong)target.Role!.Id);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = 1130800 + level, TargetId = target.Role!.Id, TargetPos = before,
            Face = MonsterService.Angle(origin.X, origin.Z, before.X, before.Z),
        });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id).Damage);
        await completed.Task.WaitAsync(Wait);
        var move = SkillStartMoveNotify.Parser.ParseFrom((await movement.WaitAsync(Wait)).Body);
        Assert.Equal(1130800 + mode * 10 + level, move.SkillConfigId); Assert.Equal(2, move.MoveType);
        Assert.Equal(before, move.StartPos);
        Assert.InRange(MonsterService.Distance(origin.X, origin.Z, move.EndPos.X, move.EndPos.Z), 0.999f, 1.001f);
        Assert.InRange(MonsterService.Distance(before.X, before.Z, move.EndPos.X, move.EndPos.Z), 3.999f, 4.001f);
        Assert.Equal(move.EndPos, target.Position);
        Assert.Equal(1_000_000 - expected, target.CurrentHp);
        var mark = target.Buffs.SingleOrDefault(b => b.ConfigId == 1133601);
        if (mode == 0) Assert.Null(mark);
        else
        {
            Assert.NotNull(mark); Assert.Equal(source.Role!.Id, mark.SourceId); Assert.Equal(10000, mark.DurationMs);
            Assert.Equal(1, mark.PileCount);
        }
        long cooldown = source.SkillModeCds.GetValueOrDefault((11308, mode));
        Assert.True(cooldown > MonsterService.NowMs);
        return (mark, cooldown);
    }

    private async Task PixelEnvironmentDeathAsync(GameSession target)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            Assert.True(await combat.EnvironmentDamageAsync(target, 2_000_000) > 0);
            Assert.True(target.Dead);
        });
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(5, true)]
    public async Task EmblemPixelThrustLowAndHighestResetAfterAnyKillerOnlyOnce(int level, bool thirdParty)
    {
        await using var bot = await InWorld("PixelOwner");
        await using var enemy = await InWorld("PixelTarget");
        await using var third = await InWorld("PixelThird");
        await Gm("pixelowner", "!job 1400"); await Teach("pixelowner", 11308, level);
        await Gm("pixelthird", "!job 1100"); await Teach("pixelthird", 11001);
        var source = Session("pixelowner"); var target = Session("pixeltarget"); var killer = Session("pixelthird");
        await CardOffenceRoom((bot, source), (enemy, target), (third, killer));
        await EquipPixelSpearAsync(bot, source);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((a, b) => (a == source || a == killer) && b == target, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        await EmblemMarksAsync(source, 1003, 5);
        await PixelWireCastAsync(bot, source, target, level, 0);
        using var modeWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.TryGetValue(11308, out var group)
            && group.AffectParam.TryGetValue(15, out var mode) && mode.AffectValue == 1);
        await EmblemMarksAsync(source, 1003, 6); await modeWire.WaitAsync(Wait);
        var (mark, _) = await PixelWireCastAsync(bot, source, target, level, 1);
        Assert.NotNull(mark); Assert.DoesNotContain(111305, source.Progress!.Skills.Keys);
        long unrelated = MonsterService.NowMs + 60000;
        await Loop.InvokeAsync(() => { source.SkillModeCds[(11001, 0)] = unrelated; return Task.CompletedTask; });
        using var reset = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 11308 && c.CdEndTimestampMs == c.CdStartTimestampMs));
        if (thirdParty)
        {
            await Loop.InvokeAsync(async () =>
            {
                killer.Position = target.Position.Clone();
                await Character.SetHpSpAsync(target, 1, null);
            });
            await third.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Role!.Id });
            var accepted = await Next(third, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            var kill = await Next(third, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            Assert.True(Assert.Single(kill.Dsts, d => d.DstID == target.Role.Id).IsDead);
        }
        else await PixelEnvironmentDeathAsync(target);
        var notice = SkillCdNotify.Parser.ParseFrom((await reset.WaitAsync(Wait)).Body);
        var cd = Assert.Single(notice.Infos, c => c.SkillId == 11308);
        Assert.Equal(0, cd.SkillMode); Assert.Equal(cd.CdStartTimestampMs, cd.CdEndTimestampMs);
        Assert.False(source.SkillModeCds.ContainsKey((11308, 1))); Assert.Equal(unrelated, source.SkillModeCds[(11001, 0)]);
        // 重送死亡通知不可再刷新後來已開始的冷卻；原標記 DeadClear=0 仍在也不能重新武裝。
        await Loop.InvokeAsync(async () =>
        {
            source.SkillModeCds[(11308, 1)] = unrelated;
            foreach (var callback in combat.PlayerDied) await callback(target);
        });
        await Loop.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(unrelated, source.SkillModeCds[(11308, 1)]);
    }

    [ClientDataTheory]
    [InlineData("expired")]
    [InlineData("grant")]
    [InlineData("source-epoch")]
    [InlineData("target-epoch")]
    [InlineData("source-scene")]
    [InlineData("target-scene")]
    [InlineData("removed")]
    public async Task EmblemPixelThrustRejectsExpiredRevokedOrObsoleteMark(string change)
    {
        await using var bot = await InWorld("PixelReject");
        await using var enemy = await InWorld("PixelRejectT");
        await Gm("pixelreject", "!job 1400"); await Teach("pixelreject", 11308);
        var source = Session("pixelreject"); var target = Session("pixelrejectt");
        await CastPassiveRoomAsync(bot, source, enemy, target); await EquipPixelSpearAsync(bot, source);
        await EmblemMarksAsync(source, 1003, 6);
        var (mark, cooldown) = await PixelWireCastAsync(bot, source, target, 1, 1);
        Assert.NotNull(mark);
        if (change == "expired")
        {
            int waitMs = (int)Math.Max(0, mark.ExpireMs - MonsterService.NowMs + 50);
            await Task.Delay(waitMs);
            Assert.True(MonsterService.NowMs >= mark.ExpireMs);
        }
        else if (change == "grant")
        {
            await EmblemMarksAsync(source, 1003, 0);
            await EmblemMarksAsync(source, 1003, 6); // 穿回不能復活原來已撤權的標記。
        }
        else await Loop.InvokeAsync(async () =>
        {
            if (change == "source-epoch") source.SkillProtocol = new();
            if (change == "target-epoch") target.SkillProtocol = new();
            if (change == "source-scene") source.SceneGeneration++;
            if (change == "target-scene") target.SceneGeneration++;
            if (change == "removed") await _app.Services.GetRequiredService<BuffService>().RemoveAsync(target, mark);
        });
        await PixelEnvironmentDeathAsync(target);
        await Loop.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(cooldown, source.SkillModeCds[(11308, 1)]);
    }

    [ClientDataFact]
    public async Task EmblemPixelThrustPreservesExistingPublicCooldownFloor()
    {
        await using var bot = await InWorld("PixelFloor");
        await using var enemy = await InWorld("PixelFloorTarget");
        await Gm("pixelfloor", "!job 1400"); await Teach("pixelfloor", 11308);
        var source = Session("pixelfloor"); var target = Session("pixelfloortarget");
        await CastPassiveRoomAsync(bot, source, enemy, target); await EquipPixelSpearAsync(bot, source);
        await EmblemMarksAsync(source, 1003, 6); await PixelWireCastAsync(bot, source, target, 1, 1);
        long floor = MonsterService.NowMs + 3000;
        await Loop.InvokeAsync(() => { source.SkillPublicCds[11308] = floor; return Task.CompletedTask; });
        using var reset = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 11308 && c.CdEndTimestampMs == floor));
        await PixelEnvironmentDeathAsync(target); await reset.WaitAsync(Wait);
        Assert.Equal(floor, source.SkillModeCds[(11308, 1)]); Assert.Equal(floor, source.SkillPublicCds[11308]);
    }

    [ClientDataFact]
    public async Task EmblemPixelThrustSingleSourceReplacementResetsOnlyTheLatestOwner()
    {
        await using var firstBot = await InWorld("PixelFirst");
        await using var secondBot = await InWorld("PixelSecond");
        await using var enemy = await InWorld("PixelMarked");
        await Gm("pixelfirst", "!job 1400"); await Teach("pixelfirst", 11308);
        await Gm("pixelsecond", "!job 1400"); await Teach("pixelsecond", 11308, 5);
        var first = Session("pixelfirst"); var second = Session("pixelsecond"); var target = Session("pixelmarked");
        await CardOffenceRoom((firstBot, first), (secondBot, second), (enemy, target));
        await EquipPixelSpearAsync(firstBot, first); await EquipPixelSpearAsync(secondBot, second);
        await EmblemMarksAsync(first, 1003, 6); await EmblemMarksAsync(second, 1003, 6);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        await Loop.InvokeAsync(() =>
        {
            second.Position = first.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => (a == first || a == second) && b == target, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        var (oldMark, firstCooldown) = await PixelWireCastAsync(firstBot, first, target, 1, 1);
        var (newMark, _) = await PixelWireCastAsync(secondBot, second, target, 5, 1);
        Assert.NotNull(oldMark); Assert.NotNull(newMark); Assert.Equal(oldMark.Guid, newMark.Guid);
        Assert.NotSame(oldMark, newMark); Assert.Equal(second.Role!.Id, newMark.SourceId);
        using var reset = secondBot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 11308 && c.CdEndTimestampMs == c.CdStartTimestampMs));
        await PixelEnvironmentDeathAsync(target); await reset.WaitAsync(Wait);
        Assert.Equal(firstCooldown, first.SkillModeCds[(11308, 1)]);
        Assert.False(second.SkillModeCds.ContainsKey((11308, 1)));
    }
}
