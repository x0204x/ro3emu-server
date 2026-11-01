using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<CombatService> CastPassiveRoomAsync(BotClient bot, GameSession source, BotClient enemy, GameSession target)
    {
        await CardOffenceRoom((bot, source), (enemy, target));
        await HealingStat(bot, 104, 1000);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls();
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>();
            foreach (var monster in world.Map(source.WorldScene).Units.OfType<MonsterUnit>().ToArray())
                await world.RemoveUnitAsync(monster);
            source.Position = target.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
        });
        Assert.Equal(1000, Character.Stats(source)[104]);
        return combat;
    }

    private static TaskCompletionSource<ActiveSkillCast> CastPassiveCompletion(CombatService combat, GameSession source)
    {
        var completed = new TaskCompletionSource<ActiveSkillCast>(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, cast, _) =>
        {
            if (s == source) { source.Attack = null; completed.TrySetResult(cast); }
            return Task.CompletedTask;
        });
        return completed;
    }

    [ClientDataTheory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    [InlineData(5, 2)]
    public async Task CastPassivePvpDoubleHolyLightUsesTwoIndependentAuthoredThresholds(int level, int rollsCase)
    {
        await using var bot = await InWorld("PvpDouble");
        await using var enemy = await InWorld("PvpDoubleTarget");
        await Gm("pvpdouble", "!job 4400"); await Teach("pvpdouble", 14309); await Teach("pvpdouble", 14311, level);
        var source = Session("pvpdouble"); var target = Session("pvpdoubletarget");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        var completed = CastPassiveCompletion(combat, source);
        int threshold = level == 1 ? 3000 : 5000, rolls = 0;
        int[] sequence = rollsCase switch { 0 => [threshold - 1, threshold], 1 => [threshold, threshold - 1], _ => [threshold - 1, threshold - 1] };
        var extra = new List<(long Id, long Damage)>();
        _app.Services.GetRequiredService<PassiveSkills>().Roll = bound =>
        { Assert.Equal(10000, bound); Assert.True(rolls < 2); return sequence[rolls++]; };
        combat.PvpSkillHitPublished.Add((s, t, row, amount, _) =>
        {
            if (s == source && t == target && new SkillConfig(row).SkillId is 14316 or 14317)
                extra.Add((new SkillConfig(row).Id, amount));
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1430901, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var main = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(2, rolls); Assert.Equal(rollsCase == 2 ? 2 : 1, extra.Count);
        long raw = level == 1 ? 180 : 300;
        foreach (var (id, amount) in extra)
        {
            Assert.Equal(raw, amount);
            var wire = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == id);
            Assert.Equal(raw, Assert.Single(wire.Dsts, d => d.DstID == target.Role.Id).Damage);
        }
        Assert.Equal(rollsCase != 1, extra.Any(e => e.Id == 1431600 + level));
        Assert.Equal(rollsCase != 0, extra.Any(e => e.Id == 1431700 + level));
        Assert.Equal(1_000_000 - Assert.Single(main.Dsts).Damage - extra.Sum(e => e.Damage), target.CurrentHp);
    }

    [ClientDataFact]
    public async Task CastPassivePvpSingleTargetJudexIgnoresNearbyMonsterAndDoesNotDoubleProc()
    {
        await using var bot = await InWorld("PvpMixedLight");
        await using var enemy = await InWorld("PvpMixedEnemy");
        await Gm("pvpmixedlight", "!job 4400"); await Teach("pvpmixedlight", 14309); await Teach("pvpmixedlight", 14311);
        var source = Session("pvpmixedlight"); var target = Session("pvpmixedenemy");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        var monster = await Target("pvpmixedlight", stationary: true, immobile: true); await CardOffenceDummyAsync(monster);
        await Loop.InvokeAsync(() =>
        {
            monster.X = target.Position.X + .5f; monster.Y = target.Position.Y; monster.Z = target.Position.Z;
            _app.Services.GetRequiredService<World>().Map(source.WorldScene).Moved(monster);
            return Task.CompletedTask;
        });
        int rolls = 0; _app.Services.GetRequiredService<PassiveSkills>().Roll = _ => { rolls++; return 0; };
        var completed = CastPassiveCompletion(combat, source);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1430901, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        // 配對 1430901 是 RangeType=1、TargetMax=1。旁邊有怪物不代表它會被審判擊中。
        var main = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(target.Role.Id, Assert.Single(main.Dsts).DstID);
        foreach (int id in new[] { 1431601, 1431701 })
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == id);
            Assert.Equal(target.Role.Id, Assert.Single(hit.Dsts).DstID);
        }
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(2, rolls);
        Assert.Equal(1_000_000, monster.Hp);
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == monster.Uid));
    }

    [ClientDataFact]
    public async Task CastPassivePvpMixedMagnumBreakGrantsEnchantBladeOnlyOnce()
    {
        await using var bot = await InWorld("PvpMixedEnchant");
        await using var enemy = await InWorld("PvpMixedTarget");
        await Gm("pvpmixedenchant", "!job 1400"); await Teach("pvpmixedenchant", 11004); await Teach("pvpmixedenchant", 11313, 3);
        var source = Session("pvpmixedenchant"); var target = Session("pvpmixedtarget");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        var monster = await Target("pvpmixedenchant", stationary: true, immobile: true); await CardOffenceDummyAsync(monster);
        await Loop.InvokeAsync(() =>
        {
            monster.X = target.Position.X + .5f; monster.Y = target.Position.Y; monster.Z = target.Position.Z;
            _app.Services.GetRequiredService<World>().Map(source.WorldScene).Moved(monster);
            return Task.CompletedTask;
        });
        int grants = 0;
        _app.Services.GetRequiredService<BuffService>().Changed.Add(s =>
        {
            if (s == source && source.Buffs.Any(b => b.ConfigId == 1131503)) grants++;
            return Task.CompletedTask;
        });
        var completed = CastPassiveCompletion(combat, source);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100401, TargetPos = source.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var playerHit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Role!.Id));
        var monsterHit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == monster.Uid));
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(1, grants); Assert.Single(source.Buffs, b => b.ConfigId == 1131503);
        Assert.Equal(1_000_000 - Assert.Single(playerHit.Dsts).Damage, target.CurrentHp);
        Assert.Equal(1_000_000 - Assert.Single(monsterHit.Dsts, d => d.DstID == monster.Uid).Damage, monster.Hp);
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task CastPassivePvpEnchantBladeNeedsActualHpDamageAndAppliesAuthoredDefence(int level, bool shielded)
    {
        await using var bot = await InWorld("PvpEnchant");
        await using var enemy = await InWorld("PvpEnchantTarget");
        await Gm("pvpenchant", "!job 1400"); await Teach("pvpenchant", 11001); await Teach("pvpenchant", 11313, level);
        var source = Session("pvpenchant"); var target = Session("pvpenchanttarget");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        await HealingStat(bot, 105, 1000); await HealingStat(bot, 106, 1000);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        if (shielded) await Loop.InvokeAsync(() => buffs.AddSupportAsync(target, 1411701, 60000, target.Role!.Id, shieldAmount: 100000));
        var completed = CastPassiveCompletion(combat, source);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1100101, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        var damage = Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id);
        await completed.Task.WaitAsync(Wait);
        if (shielded)
        {
            Assert.Equal(0, damage.Damage); Assert.True(damage.SheildDamage > 0); Assert.Equal(1_000_000, target.CurrentHp);
            Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1131500 + level);
            Assert.Equal(1000, Character.Stats(source)[105]); Assert.Equal(1000, Character.Stats(source)[106]);
        }
        else
        {
            Assert.True(damage.Damage > 0); Assert.Equal(1_000_000 - damage.Damage, target.CurrentHp);
            var defence = Assert.Single(source.Buffs, b => b.ConfigId == 1131500 + level);
            Assert.Equal(5000, defence.DurationMs); Assert.Equal(source.Role!.Id, defence.SourceId);
            long value = level == 1 ? 500 : 2500;
            Assert.Contains(defence.Changes, c => c.Attr == 1052 && c.Value == value);
            Assert.Contains(defence.Changes, c => c.Attr == 1062 && c.Value == value);
            Assert.Equal(1000 + value / 10, Character.Stats(source)[105]);
            Assert.Equal(1000 + value / 10, Character.Stats(source)[106]);
            await Loop.InvokeAsync(() => buffs.RemoveAsync(source, defence));
            Assert.Equal(1000, Character.Stats(source)[105]); Assert.Equal(1000, Character.Stats(source)[106]);
        }
    }

    [ClientDataTheory]
    [InlineData(1, 0, 3)]
    [InlineData(10, 0, 3)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 3, 0)]
    [InlineData(1, 4, 0)]
    [InlineData(1, 5, 1)]
    public async Task CastPassivePvpAstralBodiesLaunchOnceAndRespectGrantAndEpoch(int level, int change, int expectedBodies)
    {
        await using var bot = await InWorld("PvpAstral");
        await using var enemy = await InWorld("PvpAstralTarget");
        await Gm("pvpastral", "!job 2400"); await Teach("pvpastral", 12302, level); await Teach("pvpastral", 12343, level);
        var source = Session("pvpastral"); var target = Session("pvpastraltarget");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        int buffId = 1236100 + level, childId = 1234500 + level;
        long bodyGuid = 0;
        var completed = CastPassiveCompletion(combat, source);
        // 真兩段命中之後、原 CastResolved 邊界前切換狀態，沒有 sleep 猜測競態。
        combat.CastResolved.Insert(0, async (s, action, _, _) =>
        {
            if (s != source || action.Request.IsExtraSkill) return;
            await buffs.SetPileCountAsync(source, buffId, 60000, source.Role!.Id, change == 4 ? 2 : 3);
            bodyGuid = source.Buffs.Single(b => b.ConfigId == buffId).Guid;
            if (change == 1) { using var held = await CharacterService.Lock(source); source.Progress!.Skills.Remove(12343); }
            if (change == 2) source.SkillProtocol = new();
            if (change == 3) target.SceneGeneration++;
        });
        var children = new List<long>();
        combat.PvpSkillHitPublished.Add((s, t, row, amount, _) =>
        {
            if (s == source && t == target && new SkillConfig(row).Id == childId)
            {
                children.Add(amount);
                if (change == 5) source.SkillProtocol = new();
            }
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1230200 + level, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        long mainDamage = 0;
        for (int stage = 0; stage < 2; stage++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            mainDamage += Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id).Damage;
        }
        var cast = await completed.Task.WaitAsync(Wait);
        Assert.Equal(2, cast.PublishedPlayerHits.Count); Assert.Equal(expectedBodies, children.Count);
        long expected = (long)Math.Round((level == 1 ? 550 : 1000) * .75);
        Assert.All(children, amount => Assert.Equal(expected, amount));
        for (int i = 0; i < expectedBodies; i++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == childId);
            Assert.Equal(expected, Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id).Damage);
        }
        Assert.Equal(1_000_000 - mainDamage - children.Sum(), target.CurrentHp);
        if (expectedBodies > 0)
        {
            var removed = await Next(bot, BuffService.BuffRemove, BuffRemoveNotify.Parser,
                b => b.UnitId == source.Role!.Id && b.BuffGuids.Contains(bodyGuid));
            Assert.Contains(bodyGuid, removed.BuffGuids);
        }
        else Assert.Contains(source.Buffs, b => b.ConfigId == buffId && b.PileCount == (change == 4 ? 2 : 3));
    }
}
