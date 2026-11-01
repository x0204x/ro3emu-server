using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1220101, 6000, 10)]
    [InlineData(1220111, 8000, 20)]
    [InlineData(1431601, 1800, 0)]
    [InlineData(1431605, 3000, 0)]
    public async Task AttackSourceMatkPhysicalKeepsPhysicalDefenceAndShield(int id, int ratio, int flat)
    {
        await using var bot = await InWorld("MatkPhysical");
        var source = Session("matkphysical");
        await HealingStat(bot, 103, 200); await HealingStat(bot, 104, 1000);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var target = await Target("matkphysical", stationary: true); await CardOffenceDummyAsync(target);
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", id)!.Value;
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Level = 10, Def = 0, Mdef = 100000 };
            return Task.CompletedTask;
        });
        long baseline = 1000L * ratio / 10000 + flat;
        Assert.Equal(baseline, await CardOffenceHitAsync(bot, source, target, row));
        await HealingStat(bot, 103, 3000);
        Assert.Equal(baseline, await CardOffenceHitAsync(bot, source, target, row));
        await HealingStat(bot, 104, 2000);
        long raised = 2000L * ratio / 10000 + flat;
        Assert.Equal(raised, await CardOffenceHitAsync(bot, source, target, row));
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Def = 200, Mdef = 0 };
            return Task.CompletedTask;
        });
        Assert.Equal((long)Math.Round(raised / 2d), await CardOffenceHitAsync(bot, source, target, row));
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(target, 1411701, 10000, source.Role!.Id, shieldAmount: 100000));
        Assert.Equal(0, await CardOffenceHitAsync(bot, source, target, row));
        var shieldHit = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Last(h => h.SkillId == id);
        Assert.Equal((long)Math.Round(raised / 2d), Assert.Single(shieldHit.Dsts).SheildDamage);
    }

    [ClientDataTheory]
    [InlineData(1521001, 4100, 42)]
    [InlineData(1521010, 5000, 60)]
    [InlineData(1524001, 8500, 115)]
    public async Task AttackSourceHybridAddsBothAttacksButFlatOnlyOnce(int id, int ratio, int flat)
    {
        await using var bot = await InWorld("HybridSource");
        var source = Session("hybridsource");
        await HealingStat(bot, 103, 200); await HealingStat(bot, 104, 1000);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var target = await Target("hybridsource", stationary: true); await CardOffenceDummyAsync(target);
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", id)!.Value;
        Assert.Equal((long)Math.Round(1200d * ratio / 10000 + flat), await CardOffenceHitAsync(bot, source, target, row));
        await HealingStat(bot, 103, 400);
        Assert.Equal((long)Math.Round(1400d * ratio / 10000 + flat), await CardOffenceHitAsync(bot, source, target, row));
        await HealingStat(bot, 104, 2000);
        Assert.Equal((long)Math.Round(2400d * ratio / 10000 + flat), await CardOffenceHitAsync(bot, source, target, row));
    }

    [ClientDataFact]
    public async Task AttackSourceMagicCrasherOnlyAuthoredLevelUsesMatkThroughRealCast()
    {
        await using var bot = await InWorld("MagicCrasher");
        await Gm("magiccrasher", "!job 2300"); await Teach("magiccrasher", 12201);
        await HealingStat(bot, 103, 200); await HealingStat(bot, 104, 1000); await HealingStat(bot, 102, 10000);
        var source = Session("magiccrasher");
        var target = await Target("magiccrasher", stationary: true); await CardOffenceDummyAsync(target);
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1220101, TargetId = target.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(610, Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage);
        Assert.Equal(1_000_000 - 610, target.Hp);
    }

    [ClientDataTheory]
    [InlineData(1, 180)]
    [InlineData(5, 300)]
    public async Task AttackSourceDoubleHolyLightRealPassiveUsesMatkForBothIndependentBranches(int level, long raw)
    {
        await using var bot = await InWorld("DoubleLight");
        await Gm("doublelight", "!job 4400"); await Teach("doublelight", 14309); await Teach("doublelight", 14311, level);
        await HealingStat(bot, 103, 200); await HealingStat(bot, 104, 1000); await HealingStat(bot, 102, 10000);
        var source = Session("doublelight");
        var target = await Target("doublelight", stationary: true); await CardOffenceDummyAsync(target);
        int passiveRolls = 0;
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Level = 10, Def = 200, Mdef = 0 };
            _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls { ProcValue = 0 };
            // PassiveSkills 有獨立的機率來源；CombatRolls 不控制雙重聖光的兩次抽籤。
            _app.Services.GetRequiredService<PassiveSkills>().Roll = bound =>
            { Assert.Equal(10000, bound); passiveRolls++; return 0; };
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430901, TargetId = target.Uid, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var physical = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1431600 + level);
        var magical = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1431700 + level);
        Assert.Equal(raw / 2, Assert.Single(physical.Dsts, d => d.DstID == target.Uid).Damage);
        Assert.Equal(raw, Assert.Single(magical.Dsts, d => d.DstID == target.Uid).Damage);
        Assert.Equal(2, passiveRolls);
        long all = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .SelectMany(e => SkillHitInfo.Parser.ParseFrom(e.Body).Dsts).Where(d => d.DstID == target.Uid).Sum(d => d.Damage);
        Assert.Equal(1_000_000 - all, target.Hp);
    }

    [ClientDataFact]
    public void AttackSourceEveryHybridLevelKeepsTheTwoAuthoredCoefficientsEqual()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var rows = SkillConfig.All(data).Where(r => r.ScriptLogic is "SoulDestroyer" or "Thief9_1").ToArray();
        Assert.Equal(11, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal(row.DescData[1], row.DescData[2]);
            Assert.Equal(double.Parse(row.DescData[1], System.Globalization.CultureInfo.InvariantCulture) * 100, row.DamageParam1[0]);
            Assert.Equal(1, row.DmgType);
        }
        Assert.Equal(1, SkillConfig.Get(data, 1220101)!.Value.MaxLevel);
    }
}
